using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public class LeadQuery : ListQuery
{
    public LeadStatus? Status { get; set; }
    public LeadPriority? Priority { get; set; }
    public LeadSource? Source { get; set; }
    public int? ConvertedCustomerId { get; set; }
}

public record LeadConversionResult(int CustomerId, int? OpportunityId);

public interface ILeadService
{
    Task<PagedResult<LeadDto>> ListAsync(LeadQuery q);
    Task<ServiceResult<LeadDto>> GetAsync(int id);
    Task<ServiceResult<LeadDto>> CreateAsync(LeadInput input);
    Task<ServiceResult<LeadDto>> UpdateAsync(int id, LeadInput input);
    Task<ServiceResult<LeadDto>> ChangeStatusAsync(int id, LeadStatus status);
    Task<ServiceResult<LeadConversionResult>> ConvertAsync(int id, LeadConversionInput input);
    Task<ServiceResult> DeleteAsync(int id);
}

public sealed class LeadService : ILeadService
{
    private readonly AppDbContext _db;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;

    public LeadService(AppDbContext db, IAccessScope scope, ICurrentUser user, IClock clock, IAuditService audit)
    { _db = db; _scope = scope; _user = user; _clock = clock; _audit = audit; }

    public async Task<PagedResult<LeadDto>> ListAsync(LeadQuery lq)
    {
        var q = await _scope.ApplyAsync(_db.Leads.AsNoTracking().Include(l => l.Owner));
        if (!string.IsNullOrWhiteSpace(lq.Search))
        {
            var p = QueryHelpers.Like(lq.Search);
            q = q.Where(l => EF.Functions.Like(l.LeadName, p, "\\") || EF.Functions.Like(l.CompanyName ?? "", p, "\\")
                || EF.Functions.Like(l.Email ?? "", p, "\\") || EF.Functions.Like(l.Phone ?? "", p, "\\") || EF.Functions.Like(l.LeadCode, p, "\\"));
        }
        if (lq.Status is { } st) q = q.Where(l => l.Status == st);
        if (lq.Priority is { } pr) q = q.Where(l => l.Priority == pr);
        if (lq.Source is { } so) q = q.Where(l => l.Source == so);
        if (lq.ConvertedCustomerId is { } cc) q = q.Where(l => l.ConvertedCustomerId == cc);
        if (!string.IsNullOrWhiteSpace(lq.AssignedTo)) q = q.Where(l => l.AssignedTo == lq.AssignedTo);

        q = (lq.Sort?.ToLowerInvariant(), lq.Desc) switch
        {
            ("code", false) => q.OrderBy(l => l.LeadCode), ("code", true) => q.OrderByDescending(l => l.LeadCode),
            ("company", false) => q.OrderBy(l => l.CompanyName), ("company", true) => q.OrderByDescending(l => l.CompanyName),
            ("status", false) => q.OrderBy(l => l.Status), ("status", true) => q.OrderByDescending(l => l.Status),
            ("priority", false) => q.OrderBy(l => l.Priority), ("priority", true) => q.OrderByDescending(l => l.Priority),
            ("value", false) => q.OrderBy(l => l.ExpectedValue), ("value", true) => q.OrderByDescending(l => l.ExpectedValue),
            ("created", false) => q.OrderBy(l => l.CreatedDate), ("created", true) => q.OrderByDescending(l => l.CreatedDate),
            ("name", false) => q.OrderBy(l => l.LeadName), ("name", true) => q.OrderByDescending(l => l.LeadName),
            _ => q.OrderByDescending(l => l.CreatedDate).ThenByDescending(l => l.LeadId),
        };
        var page = await q.ToPagedAsync(lq);
        return new PagedResult<LeadDto> { Items = page.Items.Select(Map).ToList(), Page = page.Page, PageSize = page.PageSize, TotalCount = page.TotalCount };
    }

    public async Task<ServiceResult<LeadDto>> GetAsync(int id)
    {
        var l = await (await _scope.ApplyAsync(_db.Leads.AsNoTracking().Include(x => x.Owner))).FirstOrDefaultAsync(x => x.LeadId == id);
        return l is null ? ServiceResult<LeadDto>.NotFound() : ServiceResult<LeadDto>.Ok(Map(l));
    }

    public async Task<ServiceResult<LeadDto>> CreateAsync(LeadInput input)
    {
        var errors = InputValidator.Validate(input);
        if (input.Status is not (LeadStatus.New or LeadStatus.Contacted))
            errors["Status"] = "A new lead must start as New or Contacted.";
        if (errors.Count > 0) return ServiceResult<LeadDto>.Invalid(errors);

        var (owner, err) = await _scope.ResolveAssigneeAsync(input.AssignedTo);
        if (owner is null) return ServiceResult<LeadDto>.Invalid(nameof(LeadInput.AssignedTo), err!);

        var now = _clock.UtcNow;
        var l = new Lead { CreatedBy = _user.UserId!, CreatedDate = now, ModifiedDate = now, AssignedTo = owner };
        Apply(l, input);
        _db.Leads.Add(l);
        await _db.SaveChangesAsync();
        await _db.Entry(l).Reference(x => x.Owner).LoadAsync();
        return ServiceResult<LeadDto>.Created(Map(l));
    }

    public async Task<ServiceResult<LeadDto>> UpdateAsync(int id, LeadInput input)
    {
        var errors = InputValidator.Validate(input);
        if (errors.Count > 0) return ServiceResult<LeadDto>.Invalid(errors);

        var l = await (await _scope.ApplyAsync(_db.Leads)).FirstOrDefaultAsync(x => x.LeadId == id);
        if (l is null) return ServiceResult<LeadDto>.NotFound();
        if (l.Status == LeadStatus.Converted) return ServiceResult<LeadDto>.Conflict("Converted leads are read-only.");

        if (input.Status == LeadStatus.Converted && l.Status != LeadStatus.Converted)
            return ServiceResult<LeadDto>.Invalid("Status", "Use the Convert action to convert a qualified lead.");
        if (!LeadWorkflow.CanTransition(l.Status, input.Status))
            return ServiceResult<LeadDto>.Invalid("Status", $"A lead cannot move from {l.Status} to {input.Status}.");

        var (owner, err) = await _scope.ResolveAssigneeAsync(string.IsNullOrWhiteSpace(input.AssignedTo) ? l.AssignedTo : input.AssignedTo);
        if (owner is null) return ServiceResult<LeadDto>.Invalid(nameof(LeadInput.AssignedTo), err!);

        Apply(l, input);
        l.AssignedTo = owner;
        l.ModifiedDate = _clock.UtcNow;
        await _db.SaveChangesAsync();
        await _db.Entry(l).Reference(x => x.Owner).LoadAsync();
        return ServiceResult<LeadDto>.Ok(Map(l));
    }

    public async Task<ServiceResult<LeadDto>> ChangeStatusAsync(int id, LeadStatus status)
    {
        if (!Enum.IsDefined(status)) return ServiceResult<LeadDto>.Invalid("Status", "Select a valid lead status.");
        var l = await (await _scope.ApplyAsync(_db.Leads.Include(x => x.Owner))).FirstOrDefaultAsync(x => x.LeadId == id);
        if (l is null) return ServiceResult<LeadDto>.NotFound();
        if (status == LeadStatus.Converted) return ServiceResult<LeadDto>.Invalid("Status", "Use the Convert action to convert a qualified lead.");
        if (!LeadWorkflow.CanTransition(l.Status, status))
            return ServiceResult<LeadDto>.Invalid("Status", $"A lead cannot move from {l.Status} to {status}.");
        l.Status = status;
        l.ModifiedDate = _clock.UtcNow;
        await _db.SaveChangesAsync();
        return ServiceResult<LeadDto>.Ok(Map(l));
    }

    public async Task<ServiceResult<LeadConversionResult>> ConvertAsync(int id, LeadConversionInput input)
    {
        var errors = InputValidator.Validate(input);
        if (errors.Count > 0) return ServiceResult<LeadConversionResult>.Invalid(errors);

        var lead = await (await _scope.ApplyAsync(_db.Leads)).FirstOrDefaultAsync(x => x.LeadId == id);
        if (lead is null) return ServiceResult<LeadConversionResult>.NotFound();
        if (lead.Status != LeadStatus.Qualified)
            return ServiceResult<LeadConversionResult>.Conflict("Only Qualified leads can be converted.");

        await using var tx = await _db.Database.BeginTransactionAsync();
        var now = _clock.UtcNow;
        Customer customer;
        if (input.ExistingCustomerId is { } cid)
        {
            var existing = await (await _scope.ApplyAsync(_db.Customers)).FirstOrDefaultAsync(c => c.CustomerId == cid);
            if (existing is null) return ServiceResult<LeadConversionResult>.Invalid(nameof(input.ExistingCustomerId), "Select a valid customer.");
            customer = existing;
        }
        else
        {
            var ci = new CustomerInput
            {
                CustomerName = lead.LeadName, Email = lead.Email, Phone = lead.Phone, CompanyName = lead.CompanyName,
                Status = CustomerStatus.Active, Notes = lead.Notes,
            };
            var ce = new Dictionary<string, string>();
            if (!ValidationRules.IsValidEmail(lead.Email)) ce["Email"] = "The lead needs a valid email before it can become a customer.";
            if (!ValidationRules.IsValidPhone(lead.Phone)) ce["Phone"] = "The lead needs a valid phone number before it can become a customer.";
            if (ce.Count > 0) return ServiceResult<LeadConversionResult>.Invalid(ce);

            var email = ValidationRules.NormalizeEmail(lead.Email!);
            var phone = ValidationRules.NormalizePhone(lead.Phone!);
            var dup = await _db.Customers.AnyAsync(c => c.Email == email || c.Phone == phone);
            if (dup) return ServiceResult<LeadConversionResult>.Conflict("A customer with this email or phone already exists. Link the lead to the existing customer instead.");

            customer = new Customer
            {
                CustomerName = ci.CustomerName!.Trim(), Email = email, Phone = phone, CompanyName = ValidationRules.Clean(lead.CompanyName),
                Status = CustomerStatus.Active, Notes = ValidationRules.Clean(lead.Notes), AssignedTo = lead.AssignedTo,
                CreatedBy = _user.UserId!, CreatedDate = now, ModifiedDate = now,
            };
            _db.Customers.Add(customer);
        }

        Opportunity? opp = null;
        if (input.CreateOpportunity)
        {
            var amount = input.Amount ?? lead.ExpectedValue;
            var prob = input.Probability ?? OpportunityRules.DefaultProbability(OpportunityStage.Qualification);
            var close = input.ExpectedCloseDate ?? _clock.Today.AddDays(30);
            var oe = OpportunityRules.Validate(amount, prob, close, OpportunityStage.Qualification, _clock.Today);
            if (oe.Count > 0) return ServiceResult<LeadConversionResult>.Invalid(oe.ToDictionary(k => k.Key == "Amount" ? "Amount" : k.Key, v => v.Value));

            opp = new Opportunity
            {
                OpportunityName = ValidationRules.Clean(input.OpportunityName) ?? $"{lead.CompanyName ?? lead.LeadName} - Opportunity",
                Customer = customer, Lead = lead, Amount = amount, Probability = prob, ExpectedCloseDate = close,
                Stage = OpportunityStage.Qualification, Source = lead.Source, Notes = lead.Notes, AssignedTo = lead.AssignedTo,
                CreatedBy = _user.UserId!, CreatedDate = now, ModifiedDate = now,
            };
            _db.Opportunities.Add(opp);
        }
        await _db.SaveChangesAsync();

        lead.Status = LeadStatus.Converted;
        lead.ConvertedCustomerId = customer.CustomerId;
        lead.ConvertedOpportunityId = opp?.OpportunityId;
        lead.ConvertedDate = now;
        lead.ModifiedDate = now;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await _audit.LogAsync("Leads", "LeadConversion", nameof(Lead), lead.LeadId.ToString(),
            $"Lead {lead.LeadCode} converted to customer {customer.CustomerCode}" + (opp is null ? "" : $" and opportunity {opp.OpportunityCode}"));
        return ServiceResult<LeadConversionResult>.Ok(new LeadConversionResult(customer.CustomerId, opp?.OpportunityId));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        if (_user.Role == RoleNames.SalesExecutive) return ServiceResult.Forbidden("Only managers and administrators can delete leads.");
        var l = await (await _scope.ApplyAsync(_db.Leads)).FirstOrDefaultAsync(x => x.LeadId == id);
        if (l is null) return ServiceResult.NotFound();
        l.IsDeleted = true;
        l.DeletedDate = _clock.UtcNow;
        l.ModifiedDate = l.DeletedDate.Value;
        await _db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    private static void Apply(Lead l, LeadInput i)
    {
        l.LeadName = i.LeadName!.Trim();
        l.Email = string.IsNullOrWhiteSpace(i.Email) ? null : ValidationRules.NormalizeEmail(i.Email);
        l.Phone = string.IsNullOrWhiteSpace(i.Phone) ? null : ValidationRules.NormalizePhone(i.Phone);
        l.CompanyName = ValidationRules.Clean(i.CompanyName);
        l.Source = i.Source;
        l.Status = i.Status;
        l.Priority = i.Priority;
        l.ExpectedValue = i.ExpectedValue;
        l.Notes = ValidationRules.Clean(i.Notes);
    }

    public static LeadDto Map(Lead l) => new(l.LeadId, l.LeadCode, l.LeadName, l.Email, l.Phone, l.CompanyName, l.Source.ToString(),
        l.Status.ToString(), l.Priority.ToString(), l.ExpectedValue, l.Notes, l.AssignedTo, l.Owner?.FullName, l.CreatedDate,
        l.ConvertedCustomerId, l.ConvertedOpportunityId, l.ConvertedDate);
}
