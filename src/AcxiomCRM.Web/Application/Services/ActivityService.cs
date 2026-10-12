using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public class ActivityQuery : ListQuery
{
    public ActivityType? Type { get; set; }
    public ActivityStatus? Status { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int? CustomerId { get; set; }
    public int? LeadId { get; set; }
}

public interface IActivityService
{
    Task<PagedResult<ActivityDto>> ListAsync(ActivityQuery q);
    Task<ServiceResult<ActivityDto>> GetAsync(int id);
    Task<ServiceResult<ActivityDto>> CreateAsync(ActivityInput input);
    Task<ServiceResult<ActivityDto>> UpdateAsync(int id, ActivityInput input);
    Task<ServiceResult> DeleteAsync(int id);
}

public sealed class ActivityService : IActivityService
{
    private readonly AppDbContext _db;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public ActivityService(AppDbContext db, IAccessScope scope, ICurrentUser user, IClock clock)
    { _db = db; _scope = scope; _user = user; _clock = clock; }

    private IQueryable<Activity> Base() => _db.Activities.Include(a => a.Customer).Include(a => a.Lead).Include(a => a.Owner);

    public async Task<PagedResult<ActivityDto>> ListAsync(ActivityQuery lq)
    {
        var q = await _scope.ApplyAsync(Base().AsNoTracking());
        if (!string.IsNullOrWhiteSpace(lq.Search))
        {
            var p = QueryHelpers.Like(lq.Search);
            q = q.Where(a => EF.Functions.Like(a.Subject, p, "\\") || EF.Functions.Like(a.Customer!.CustomerName, p, "\\") || EF.Functions.Like(a.Lead!.LeadName, p, "\\"));
        }
        if (lq.Type is { } t) q = q.Where(a => a.ActivityType == t);
        if (lq.Status is { } s) q = q.Where(a => a.Status == s);
        if (lq.From is { } f) q = q.Where(a => a.ActivityDate >= f);
        if (lq.To is { } to) q = q.Where(a => a.ActivityDate <= to);
        if (lq.CustomerId is { } cid) q = q.Where(a => a.CustomerId == cid);
        if (lq.LeadId is { } lid) q = q.Where(a => a.LeadId == lid);
        if (!string.IsNullOrWhiteSpace(lq.AssignedTo)) q = q.Where(a => a.AssignedTo == lq.AssignedTo);
        q = (lq.Sort?.ToLowerInvariant(), lq.Desc) switch
        {
            ("subject", false) => q.OrderBy(a => a.Subject), ("subject", true) => q.OrderByDescending(a => a.Subject),
            ("type", false) => q.OrderBy(a => a.ActivityType), ("type", true) => q.OrderByDescending(a => a.ActivityType),
            ("status", false) => q.OrderBy(a => a.Status), ("status", true) => q.OrderByDescending(a => a.Status),
            ("date", false) => q.OrderBy(a => a.ActivityDate),
            _ => q.OrderByDescending(a => a.ActivityDate).ThenByDescending(a => a.ActivityId),
        };
        var page = await q.ToPagedAsync(lq);
        return new PagedResult<ActivityDto> { Items = page.Items.Select(Map).ToList(), Page = page.Page, PageSize = page.PageSize, TotalCount = page.TotalCount };
    }

    public async Task<ServiceResult<ActivityDto>> GetAsync(int id)
    {
        var a = await (await _scope.ApplyAsync(Base().AsNoTracking())).FirstOrDefaultAsync(x => x.ActivityId == id);
        return a is null ? ServiceResult<ActivityDto>.NotFound() : ServiceResult<ActivityDto>.Ok(Map(a));
    }

    public async Task<ServiceResult<ActivityDto>> CreateAsync(ActivityInput input)
    {
        var (errors, owner) = await CheckAsync(input, null);
        if (errors.Count > 0) return ServiceResult<ActivityDto>.Invalid(errors);
        var a = new Activity { AssignedTo = owner!, CreatedBy = _user.UserId!, CreatedDate = _clock.UtcNow };
        Apply(a, input);
        _db.Activities.Add(a);
        await _db.SaveChangesAsync();
        return ServiceResult<ActivityDto>.Created(Map(await Base().AsNoTracking().FirstAsync(x => x.ActivityId == a.ActivityId)));
    }

    public async Task<ServiceResult<ActivityDto>> UpdateAsync(int id, ActivityInput input)
    {
        var a = await (await _scope.ApplyAsync(_db.Activities)).FirstOrDefaultAsync(x => x.ActivityId == id);
        if (a is null) return ServiceResult<ActivityDto>.NotFound();
        var (errors, owner) = await CheckAsync(input, a.AssignedTo);
        if (errors.Count > 0) return ServiceResult<ActivityDto>.Invalid(errors);
        a.AssignedTo = owner!;
        Apply(a, input);
        await _db.SaveChangesAsync();
        return ServiceResult<ActivityDto>.Ok(Map(await Base().AsNoTracking().FirstAsync(x => x.ActivityId == id)));
    }

    public async Task<ServiceResult> DeleteAsync(int id)
    {
        if (_user.Role == RoleNames.SalesExecutive) return ServiceResult.Forbidden("Only managers and administrators can delete activities.");
        var a = await (await _scope.ApplyAsync(_db.Activities)).FirstOrDefaultAsync(x => x.ActivityId == id);
        if (a is null) return ServiceResult.NotFound();
        _db.Activities.Remove(a);
        await _db.SaveChangesAsync();
        return ServiceResult.Ok();
    }

    private async Task<(Dictionary<string, string> Errors, string? Owner)> CheckAsync(ActivityInput input, string? currentOwner)
    {
        var errors = InputValidator.Validate(input);
        if (input.CustomerId is null && input.LeadId is null) errors["CustomerId"] = "Select a customer or a lead for this activity.";
        if (input.CustomerId is { } cid && !await (await _scope.ApplyAsync(_db.Customers)).AnyAsync(c => c.CustomerId == cid)) errors["CustomerId"] = "Select a valid customer.";
        if (input.LeadId is { } lid && !await (await _scope.ApplyAsync(_db.Leads)).AnyAsync(l => l.LeadId == lid)) errors["LeadId"] = "Select a valid lead.";
        var (owner, err) = await _scope.ResolveAssigneeAsync(string.IsNullOrWhiteSpace(input.AssignedTo) ? currentOwner : input.AssignedTo);
        if (owner is null) errors["AssignedTo"] = err!;
        return (errors, owner);
    }

    private static void Apply(Activity a, ActivityInput i)
    {
        a.ActivityType = i.ActivityType;
        a.Subject = i.Subject!.Trim();
        a.Description = ValidationRules.Clean(i.Description);
        a.ActivityDate = i.ActivityDate!.Value;
        a.CustomerId = i.CustomerId;
        a.LeadId = i.LeadId;
        a.Status = i.Status;
    }

    public static ActivityDto Map(Activity a) => new(a.ActivityId, a.ActivityType.ToString(), a.Subject, a.Description, a.ActivityDate,
        a.CustomerId, a.Customer?.CustomerName, a.LeadId, a.Lead?.LeadName, a.Status.ToString(), a.AssignedTo, a.Owner?.FullName);
}
