using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public class FollowUpQuery : ListQuery
{
    public FollowUpStatus? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    /// <summary>upcoming | overdue | today | all</summary>
    public string? View { get; set; }
    public RelatedType? RelatedType { get; set; }
    public int? RelatedId { get; set; }
}

public interface IFollowUpService
{
    Task<PagedResult<FollowUpDto>> ListAsync(FollowUpQuery q);
    Task<ServiceResult<FollowUpDto>> GetAsync(int id);
    Task<ServiceResult<FollowUpDto>> CreateAsync(FollowUpInput input);
    Task<ServiceResult<FollowUpDto>> UpdateAsync(int id, FollowUpInput input);
    Task<ServiceResult<FollowUpDto>> CompleteAsync(int id, string? remarks);
    Task<ServiceResult<FollowUpDto>> SetStatusAsync(int id, FollowUpStatus status);
    Task<ServiceResult> DeleteAsync(int id);
}

public sealed class FollowUpService : IFollowUpService
{
    private readonly AppDbContext _db;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public FollowUpService(AppDbContext db, IAccessScope scope, ICurrentUser user, IClock clock)
    { _db = db; _scope = scope; _user = user; _clock = clock; }

    private IQueryable<FollowUp> Base() => _db.FollowUps.Include(f => f.Customer).Include(f => f.Lead).Include(f => f.Opportunity).Include(f => f.Owner);

    public async Task<PagedResult<FollowUpDto>> ListAsync(FollowUpQuery lq)
    {
        var today = _clock.Today;
        var q = await _scope.ApplyAsync(Base().AsNoTracking());
        if (!string.IsNullOrWhiteSpace(lq.Search))
        {
            var p = QueryHelpers.Like(lq.Search);
            q = q.Where(f => EF.Functions.Like(f.Subject, p, "\\") || EF.Functions.Like(f.Customer!.CustomerName, p, "\\")
                || EF.Functions.Like(f.Lead!.LeadName, p, "\\") || EF.Functions.Like(f.Opportunity!.OpportunityName, p, "\\"));
        }
        if (lq.Status is { } st) q = q.Where(f => f.Status == st);
        if (lq.From is { } from) q = q.Where(f => f.FollowUpDate >= from);
        if (lq.To is { } to) q = q.Where(f => f.FollowUpDate <= to);
        if (!string.IsNullOrWhiteSpace(lq.AssignedTo)) q = q.Where(f => f.AssignedTo == lq.AssignedTo);
        if (lq.RelatedType is { } rt && lq.RelatedId is { } rid)
            q = rt switch
            {
                RelatedType.Customer => q.Where(f => f.CustomerId == rid),
                RelatedType.Lead => q.Where(f => f.LeadId == rid),
                _ => q.Where(f => f.OpportunityId == rid),
            };
        q = lq.View?.ToLowerInvariant() switch
        {
            "overdue" => q.Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate < today),
            "upcoming" => q.Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate >= today),
            "today" => q.Where(f => f.Status == FollowUpStatus.Planned && f.FollowUpDate == today),
            _ => q,
        };

        q = (lq.Sort?.ToLowerInvariant(), lq.Desc) switch
        {
            ("subject", false) => q.OrderBy(f => f.Subject), ("subject", true) => q.OrderByDescending(f => f.Subject),
            ("status", false) => q.OrderBy(f => f.Status), ("status", true) => q.OrderByDescending(f => f.Status),
            ("type", false) => q.OrderBy(f => f.FollowUpType), ("type", true) => q.OrderByDescending(f => f.FollowUpType),
            ("date", true) => q.OrderByDescending(f => f.FollowUpDate).ThenByDescending(f => f.FollowUpId),
            _ => q.OrderBy(f => f.FollowUpDate).ThenBy(f => f.FollowUpId),
        };
        var page = await q.ToPagedAsync(lq);
        return new PagedResult<FollowUpDto> { Items = page.Items.Select(f => Map(f, today)).ToList(), Page = page.Page, PageSize = page.PageSize, TotalCount = page.TotalCount };
    }

    public async Task<ServiceResult<FollowUpDto>> GetAsync(int id)
    {
        var f = await (await _scope.ApplyAsync(Base().AsNoTracking())).FirstOrDefaultAsync(x => x.FollowUpId == id);
        return f is null ? ServiceResult<FollowUpDto>.NotFound() : ServiceResult<FollowUpDto>.Ok(Map(f, _clock.Today));
    }

    public async Task<ServiceResult<FollowUpDto>> CreateAsync(FollowUpInput input)
    {
        var errors = InputValidator.Validate(input);
        if (input.Status != FollowUpStatus.Planned) errors["Status"] = "A new follow-up must be Planned.";
        if (input.FollowUpDate is { } d && d < _clock.Today) errors["FollowUpDate"] = "Follow-up date cannot be earlier than today.";
        if (errors.Count > 0) return ServiceResult<FollowUpDto>.Invalid(errors);

        var link = await ResolveRelatedAsync(input);
        if (link.Error is not null) return ServiceResult<FollowUpDto>.Invalid("RelatedId", link.Error);
        var (owner, err) = await _scope.ResolveAssigneeAsync(input.AssignedTo);
        if (owner is null) return ServiceResult<FollowUpDto>.Invalid(nameof(FollowUpInput.AssignedTo), err!);

        var now = _clock.UtcNow;
        var f = new FollowUp
        {
            CustomerId = link.CustomerId, LeadId = link.LeadId, OpportunityId = link.OpportunityId,
            AssignedTo = owner, CreatedBy = _user.UserId!, CreatedDate = now, ModifiedDate = now,
        };
        Apply(f, input);
        _db.FollowUps.Add(f);
        await _db.SaveChangesAsync();
        return await ReloadAsync(f.FollowUpId, ResultStatus.Created);
    }

    public async Task<ServiceResult<FollowUpDto>> UpdateAsync(int id, FollowUpInput input)
    {
        var errors = InputValidator.Validate(input);
        var f = await (await _scope.ApplyAsync(Base())).FirstOrDefaultAsync(x => x.FollowUpId == id);
        if (f is null) return ServiceResult<FollowUpDto>.NotFound();
        if (f.Status is FollowUpStatus.Completed or FollowUpStatus.Cancelled)
            return ServiceResult<FollowUpDto>.Conflict($"{f.Status} follow-ups cannot be modified.");
        if (!FollowUpRules.CanTransition(f.Status, input.Status)) errors["Status"] = $"Cannot move from {f.Status} to {input.Status}.";

        // Rule applies to new/planned follow-ups: any planned follow-up must be dated today or later.
        if (input.FollowUpDate is { } d && input.Status == FollowUpStatus.Planned && d < _clock.Today && d != f.FollowUpDate)
            errors["FollowUpDate"] = "Follow-up date cannot be earlier than today.";
        if (input.Status == FollowUpStatus.Planned && input.FollowUpDate is { } d2 && d2 < _clock.Today && f.Status == FollowUpStatus.Missed)
            errors["FollowUpDate"] = "Reschedule a missed follow-up to today or later.";
        if (errors.Count > 0) return ServiceResult<FollowUpDto>.Invalid(errors);

        var link = await ResolveRelatedAsync(input);
        if (link.Error is not null) return ServiceResult<FollowUpDto>.Invalid("RelatedId", link.Error);
        var (owner, err) = await _scope.ResolveAssigneeAsync(string.IsNullOrWhiteSpace(input.AssignedTo) ? f.AssignedTo : input.AssignedTo);
        if (owner is null) return ServiceResult<FollowUpDto>.Invalid(nameof(FollowUpInput.AssignedTo), err!);

        var oldDate = f.FollowUpDate;
        f.CustomerId = link.CustomerId; f.LeadId = link.LeadId; f.OpportunityId = link.OpportunityId;
        f.AssignedTo = owner;
        Apply(f, input);
        if (f.FollowUpDate != oldDate) f.RescheduleCount++;
        if (f.Status == FollowUpStatus.Completed) f.CompletedDate = _clock.UtcNow;
        f.ModifiedDate = _clock.UtcNow;
        await _db.SaveChangesAsync();
        return await ReloadAsync(f.FollowUpId, ResultStatus.Ok);
    }

    public async Task<ServiceResult<FollowUpDto>> CompleteAsync(int id, string? remarks)
    {
        var f = await (await _scope.ApplyAsync(Base())).FirstOrDefaultAsync(x => x.FollowUpId == id);
        if (f is null) return ServiceResult<FollowUpDto>.NotFound();
        if (f.Status is not (FollowUpStatus.Planned or FollowUpStatus.Missed))
            return ServiceResult<FollowUpDto>.Conflict($"A {f.Status} follow-up cannot be completed.");
        if (remarks is { Length: > 2000 }) return ServiceResult<FollowUpDto>.Invalid("Remarks", "Remarks cannot exceed 2000 characters.");

        await using var tx = await _db.Database.BeginTransactionAsync();
        var now = _clock.UtcNow;
        f.Status = FollowUpStatus.Completed;
        f.CompletedDate = now;
        f.ModifiedDate = now;
        if (!string.IsNullOrWhiteSpace(remarks)) f.Remarks = ValidationRules.Clean(remarks);

        // Update the related CRM record: a contacted "New" lead moves along the workflow, and the interaction is logged.
        if (f.Lead is { Status: LeadStatus.New } lead)
        {
            lead.Status = LeadStatus.Contacted;
            lead.ModifiedDate = now;
        }
        if (f.Customer is { } cust) cust.ModifiedDate = now;
        if (f.Opportunity is { IsActive: true } opp) opp.ModifiedDate = now;

        _db.Activities.Add(new Activity
        {
            ActivityType = (ActivityType)(int)f.FollowUpType,
            Subject = f.Subject.Length > 200 ? f.Subject[..200] : f.Subject,
            Description = $"Completed follow-up planned for {f.FollowUpDate:yyyy-MM-dd}." + (string.IsNullOrWhiteSpace(f.Remarks) ? "" : " " + f.Remarks),
            ActivityDate = _clock.Today,
            CustomerId = f.CustomerId ?? f.Opportunity?.CustomerId,
            LeadId = f.LeadId,
            Status = ActivityStatus.Completed,
            AssignedTo = f.AssignedTo,
            CreatedBy = _user.UserId!,
            CreatedDate = now,
        });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return ServiceResult<FollowUpDto>.Ok(Map(f, _clock.Today));
    }

    public async Task<ServiceResult<FollowUpDto>> SetStatusAsync(int id, FollowUpStatus status)
    {
        if (status is not (FollowUpStatus.Missed or FollowUpStatus.Cancelled))
            return ServiceResult<FollowUpDto>.Invalid("Status", "Use Complete to complete a follow-up.");
        var f = await (await _scope.ApplyAsync(Base())).FirstOrDefaultAsync(x => x.FollowUpId == id);
        if (f is null) return ServiceResult<FollowUpDto>.NotFound();
        if (f.Status != FollowUpStatus.Planned) return ServiceResult<FollowUpDto>.Conflict($"A {f.Status} follow-up cannot be changed to {status}.");
        if (status == FollowUpStatus.Missed && f.FollowUpDate >= _clock.Today)
            return ServiceResult<FollowUpDto>.Conflict("Only overdue follow-ups can be marked as missed.");
        f.Status = status;
        f.ModifiedDate = _clock.UtcNow;
        await _db.SaveChangesAsync();
        return ServiceResult<FollowUpDto>.Ok(Map(f, _clock.Today));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        if (_user.Role == RoleNames.SalesExecutive) return ServiceResult.Forbidden("Only managers and administrators can delete follow-ups. You can cancel it instead.");
        var f = await (await _scope.ApplyAsync(_db.FollowUps)).FirstOrDefaultAsync(x => x.FollowUpId == id);
        if (f is null) return ServiceResult.NotFound();
        _db.FollowUps.Remove(f);
        await _db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    private async Task<ServiceResult<FollowUpDto>> ReloadAsync(int id, ResultStatus status)
    {
        var f = await Base().AsNoTracking().FirstAsync(x => x.FollowUpId == id);
        var dto = Map(f, _clock.Today);
        return status == ResultStatus.Created ? ServiceResult<FollowUpDto>.Created(dto) : ServiceResult<FollowUpDto>.Ok(dto);
    }

    private async Task<(int? CustomerId, int? LeadId, int? OpportunityId, string? Error)> ResolveRelatedAsync(FollowUpInput i)
    {
        var id = i.RelatedId!.Value;
        switch (i.RelatedType)
        {
            case RelatedType.Customer:
                return await (await _scope.ApplyAsync(_db.Customers)).AnyAsync(c => c.CustomerId == id) ? (id, null, null, null) : (null, null, null, "Select a valid customer.");
            case RelatedType.Lead:
                return await (await _scope.ApplyAsync(_db.Leads)).AnyAsync(c => c.LeadId == id) ? (null, id, null, null) : (null, null, null, "Select a valid lead.");
            case RelatedType.Opportunity:
                return await (await _scope.ApplyAsync(_db.Opportunities)).AnyAsync(c => c.OpportunityId == id) ? (null, null, id, null) : (null, null, null, "Select a valid opportunity.");
            default:
                return (null, null, null, "Select a valid related record type.");
        }
    }

    private static void Apply(FollowUp f, FollowUpInput i)
    {
        f.FollowUpDate = i.FollowUpDate!.Value;
        f.Subject = i.Subject!.Trim();
        f.FollowUpType = i.FollowUpType;
        f.Remarks = ValidationRules.Clean(i.Remarks);
        f.Status = i.Status;
    }

    public static FollowUpDto Map(FollowUp f, DateOnly today)
    {
        var (type, id, name) = f.OpportunityId is not null ? ("Opportunity", f.OpportunityId.Value, f.Opportunity?.OpportunityName)
            : f.LeadId is not null ? ("Lead", f.LeadId.Value, f.Lead?.LeadName)
            : ("Customer", f.CustomerId ?? 0, f.Customer?.CustomerName);
        return new FollowUpDto(f.FollowUpId, type, id, name, f.FollowUpDate, f.Subject, f.FollowUpType.ToString(), f.Remarks,
            f.Status.ToString(), FollowUpRules.IsOverdue(f.Status, f.FollowUpDate, today), f.AssignedTo, f.Owner?.FullName, f.CompletedDate, f.CreatedDate);
    }
}
