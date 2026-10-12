using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public class OpportunityQuery : ListQuery
{
    public OpportunityStage? Stage { get; set; }
    /// <summary>Open, Won or Lost.</summary>
    public string? State { get; set; }
    public int? CustomerId { get; set; }
}

public interface IOpportunityService
{
    Task<PagedResult<OpportunityDto>> ListAsync(OpportunityQuery q);
    Task<ServiceResult<OpportunityDto>> GetAsync(int id);
    Task<ServiceResult<OpportunityDto>> CreateAsync(OpportunityInput input);
    Task<ServiceResult<OpportunityDto>> UpdateAsync(int id, OpportunityInput input);
    Task<ServiceResult<OpportunityDto>> ChangeStageAsync(int id, StageChangeInput input);
    Task<ServiceResult> DeleteAsync(int id);
    Task<IReadOnlyList<OpportunityDto>> BoardAsync(string? assignedTo);
}

public sealed class OpportunityService : IOpportunityService
{
    private readonly AppDbContext _db;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public OpportunityService(AppDbContext db, IAccessScope scope, ICurrentUser user, IClock clock)
    { _db = db; _scope = scope; _user = user; _clock = clock; }

    private IQueryable<Opportunity> Base() => _db.Opportunities.Include(o => o.Customer).Include(o => o.Owner);

    public async Task<PagedResult<OpportunityDto>> ListAsync(OpportunityQuery lq)
    {
        var q = await _scope.ApplyAsync(Base().AsNoTracking());
        if (!string.IsNullOrWhiteSpace(lq.Search))
        {
            var p = QueryHelpers.Like(lq.Search);
            q = q.Where(o => EF.Functions.Like(o.OpportunityName, p, "\\") || EF.Functions.Like(o.OpportunityCode, p, "\\")
                || EF.Functions.Like(o.Customer!.CustomerName, p, "\\") || EF.Functions.Like(o.Customer!.CompanyName ?? "", p, "\\"));
        }
        if (lq.Stage is { } st) q = q.Where(o => o.Stage == st);
        if (lq.CustomerId is { } cid) q = q.Where(o => o.CustomerId == cid);
        q = lq.State?.ToLowerInvariant() switch
        {
            "open" => q.Where(o => o.Stage != OpportunityStage.Won && o.Stage != OpportunityStage.Lost),
            "won" => q.Where(o => o.Stage == OpportunityStage.Won),
            "lost" => q.Where(o => o.Stage == OpportunityStage.Lost),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(lq.AssignedTo)) q = q.Where(o => o.AssignedTo == lq.AssignedTo);

        q = (lq.Sort?.ToLowerInvariant(), lq.Desc) switch
        {
            ("code", false) => q.OrderBy(o => o.OpportunityCode), ("code", true) => q.OrderByDescending(o => o.OpportunityCode),
            ("customer", false) => q.OrderBy(o => o.Customer!.CustomerName), ("customer", true) => q.OrderByDescending(o => o.Customer!.CustomerName),
            ("stage", false) => q.OrderBy(o => o.Stage), ("stage", true) => q.OrderByDescending(o => o.Stage),
            ("amount", false) => q.OrderBy(o => o.Amount), ("amount", true) => q.OrderByDescending(o => o.Amount),
            ("probability", false) => q.OrderBy(o => o.Probability), ("probability", true) => q.OrderByDescending(o => o.Probability),
            ("close", false) => q.OrderBy(o => o.ExpectedCloseDate), ("close", true) => q.OrderByDescending(o => o.ExpectedCloseDate),
            ("name", false) => q.OrderBy(o => o.OpportunityName), ("name", true) => q.OrderByDescending(o => o.OpportunityName),
            _ => q.OrderByDescending(o => o.CreatedDate).ThenByDescending(o => o.OpportunityId),
        };
        var page = await q.ToPagedAsync(lq);
        return new PagedResult<OpportunityDto> { Items = page.Items.Select(Map).ToList(), Page = page.Page, PageSize = page.PageSize, TotalCount = page.TotalCount };
    }

    public async Task<IReadOnlyList<OpportunityDto>> BoardAsync(string? assignedTo)
    {
        var q = await _scope.ApplyAsync(Base().AsNoTracking());
        if (!string.IsNullOrWhiteSpace(assignedTo)) q = q.Where(o => o.AssignedTo == assignedTo);
        var rows = await q.OrderBy(o => o.ExpectedCloseDate).Take(500).ToListAsync();
        return rows.Select(Map).ToList();
    }

    public async Task<ServiceResult<OpportunityDto>> GetAsync(int id)
    {
        var o = await (await _scope.ApplyAsync(Base().AsNoTracking())).FirstOrDefaultAsync(x => x.OpportunityId == id);
        return o is null ? ServiceResult<OpportunityDto>.NotFound() : ServiceResult<OpportunityDto>.Ok(Map(o));
    }

    public async Task<ServiceResult<OpportunityDto>> CreateAsync(OpportunityInput input)
    {
        var errors = InputValidator.Validate(input);
        if (input.Stage is OpportunityStage.Won or OpportunityStage.Lost) errors["Stage"] = "A new opportunity must start in an active stage.";
        BusinessErrors(input, input.Stage, errors);
        if (errors.Count > 0) return ServiceResult<OpportunityDto>.Invalid(errors);

        var customer = await (await _scope.ApplyAsync(_db.Customers)).FirstOrDefaultAsync(c => c.CustomerId == input.CustomerId);
        if (customer is null) return ServiceResult<OpportunityDto>.Invalid("CustomerId", "Select a valid customer.");
        var (owner, err) = await _scope.ResolveAssigneeAsync(input.AssignedTo);
        if (owner is null) return ServiceResult<OpportunityDto>.Invalid(nameof(OpportunityInput.AssignedTo), err!);

        var now = _clock.UtcNow;
        var o = new Opportunity { CustomerId = customer.CustomerId, AssignedTo = owner, CreatedBy = _user.UserId!, CreatedDate = now, ModifiedDate = now };
        Apply(o, input);
        _db.Opportunities.Add(o);
        await _db.SaveChangesAsync();
        await _db.Entry(o).Reference(x => x.Customer).LoadAsync();
        await _db.Entry(o).Reference(x => x.Owner).LoadAsync();
        return ServiceResult<OpportunityDto>.Created(Map(o));
    }

    public async Task<ServiceResult<OpportunityDto>> UpdateAsync(int id, OpportunityInput input)
    {
        var errors = InputValidator.Validate(input);
        var o = await (await _scope.ApplyAsync(Base())).FirstOrDefaultAsync(x => x.OpportunityId == id);
        if (o is null) return ServiceResult<OpportunityDto>.NotFound();
        if (!o.IsActive) return ServiceResult<OpportunityDto>.Conflict("Closed (Won/Lost) opportunities cannot be modified.");
        if (!OpportunityRules.CanTransition(o.Stage, input.Stage)) errors["Stage"] = $"Cannot move from {o.Stage} to {input.Stage}.";
        BusinessErrors(input, input.Stage, errors);
        if (errors.Count > 0) return ServiceResult<OpportunityDto>.Invalid(errors);

        var customer = await (await _scope.ApplyAsync(_db.Customers)).FirstOrDefaultAsync(c => c.CustomerId == input.CustomerId);
        if (customer is null) return ServiceResult<OpportunityDto>.Invalid("CustomerId", "Select a valid customer.");
        var (owner, err) = await _scope.ResolveAssigneeAsync(string.IsNullOrWhiteSpace(input.AssignedTo) ? o.AssignedTo : input.AssignedTo);
        if (owner is null) return ServiceResult<OpportunityDto>.Invalid(nameof(OpportunityInput.AssignedTo), err!);

        o.CustomerId = customer.CustomerId;
        o.AssignedTo = owner;
        Apply(o, input);
        o.ModifiedDate = _clock.UtcNow;
        await _db.SaveChangesAsync();
        await _db.Entry(o).Reference(x => x.Customer).LoadAsync();
        await _db.Entry(o).Reference(x => x.Owner).LoadAsync();
        return ServiceResult<OpportunityDto>.Ok(Map(o));
    }

    public async Task<ServiceResult<OpportunityDto>> ChangeStageAsync(int id, StageChangeInput input)
    {
        var errors = InputValidator.Validate(input);
        if (errors.Count > 0) return ServiceResult<OpportunityDto>.Invalid(errors);
        var o = await (await _scope.ApplyAsync(Base())).FirstOrDefaultAsync(x => x.OpportunityId == id);
        if (o is null) return ServiceResult<OpportunityDto>.NotFound();
        if (!o.IsActive) return ServiceResult<OpportunityDto>.Conflict("Closed (Won/Lost) opportunities cannot be modified.");
        if (input.Stage == o.Stage) return ServiceResult<OpportunityDto>.Ok(Map(o));

        var prob = OpportunityRules.DefaultProbability(input.Stage);
        var be = OpportunityRules.Validate(o.Amount, prob, o.ExpectedCloseDate, input.Stage, _clock.Today);
        if (be.Count > 0) return ServiceResult<OpportunityDto>.Invalid(be.ToDictionary(k => k.Key, v => v.Value));

        o.Stage = input.Stage;
        o.Probability = prob;
        o.ModifiedDate = _clock.UtcNow;
        if (!o.IsActive)
        {
            o.ClosedDate = o.ModifiedDate;
            o.OutcomeNotes = ValidationRules.Clean(input.OutcomeNotes);
        }
        await _db.SaveChangesAsync();
        return ServiceResult<OpportunityDto>.Ok(Map(o));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        if (_user.Role == RoleNames.SalesExecutive) return ServiceResult.Forbidden("Only managers and administrators can delete opportunities.");
        var o = await (await _scope.ApplyAsync(_db.Opportunities)).FirstOrDefaultAsync(x => x.OpportunityId == id);
        if (o is null) return ServiceResult.NotFound();
        o.IsDeleted = true;
        o.DeletedDate = _clock.UtcNow;
        o.ModifiedDate = o.DeletedDate.Value;
        await _db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    private void BusinessErrors(OpportunityInput input, OpportunityStage stage, Dictionary<string, string> errors)
    {
        if (input.Amount is null || input.Probability is null || input.ExpectedCloseDate is null) return;
        var rule = OpportunityRules.Validate(input.Amount.Value, input.Probability.Value, input.ExpectedCloseDate.Value, stage, _clock.Today);
        // Won/Lost set the probability automatically, so only validate the user's value for active stages.
        if (!OpportunityRules.IsActive(stage)) rule.Remove("Probability");
        foreach (var kv in rule) errors[kv.Key] = kv.Value;
    }

    private void Apply(Opportunity o, OpportunityInput i)
    {
        var wasActive = o.IsActive;
        o.OpportunityName = i.OpportunityName!.Trim();
        o.Amount = decimal.Round(i.Amount!.Value, 2);
        o.Stage = i.Stage;
        o.Probability = OpportunityRules.IsActive(i.Stage) ? i.Probability!.Value : OpportunityRules.DefaultProbability(i.Stage);
        o.ExpectedCloseDate = i.ExpectedCloseDate!.Value;
        o.Source = i.Source;
        o.Notes = ValidationRules.Clean(i.Notes);
        if (!o.IsActive)
        {
            o.ClosedDate = _clock.UtcNow;
            o.OutcomeNotes = ValidationRules.Clean(i.OutcomeNotes);
        }
    }

    public static OpportunityDto Map(Opportunity o) => new(o.OpportunityId, o.OpportunityCode, o.OpportunityName, o.CustomerId, o.Customer?.CustomerName,
        o.LeadId, o.Amount, o.Stage.ToString(), o.Probability, o.WeightedAmount, o.ExpectedCloseDate, o.Source?.ToString(), o.Notes, o.OutcomeNotes,
        o.ClosedDate, o.AssignedTo, o.Owner?.FullName, o.CreatedDate);
}
