using AcxiomCRM.Application.Common;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public record SearchHit(string Type, int Id, string Title, string? Subtitle, string Url);

public interface ISearchService
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string term, int perType = 8);
}

public sealed class SearchService : ISearchService
{
    private readonly AppDbContext _db;
    private readonly IAccessScope _scope;
    public SearchService(AppDbContext db, IAccessScope scope) { _db = db; _scope = scope; }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string term, int perType = 8)
    {
        term = (term ?? "").Trim();
        if (term.Length < 2 || term.Length > 100) return Array.Empty<SearchHit>();
        var p = QueryHelpers.Like(term);
        var hits = new List<SearchHit>();

        var cs = await (await _scope.ApplyAsync(_db.Customers.AsNoTracking()))
            .Where(c => EF.Functions.Like(c.CustomerName, p, "\\") || EF.Functions.Like(c.Email, p, "\\") || EF.Functions.Like(c.Phone, p, "\\") || EF.Functions.Like(c.CompanyName ?? "", p, "\\"))
            .OrderBy(c => c.CustomerName).Take(perType).Select(c => new { c.CustomerId, c.CustomerName, c.CompanyName, c.Email }).ToListAsync();
        hits.AddRange(cs.Select(c => new SearchHit("Customer", c.CustomerId, c.CustomerName, c.CompanyName ?? c.Email, $"/Customers/Details/{c.CustomerId}")));

        var ls = await (await _scope.ApplyAsync(_db.Leads.AsNoTracking()))
            .Where(l => EF.Functions.Like(l.LeadName, p, "\\") || EF.Functions.Like(l.CompanyName ?? "", p, "\\") || EF.Functions.Like(l.Email ?? "", p, "\\"))
            .OrderBy(l => l.LeadName).Take(perType).Select(l => new { l.LeadId, l.LeadName, l.CompanyName, l.Status }).ToListAsync();
        hits.AddRange(ls.Select(l => new SearchHit("Lead", l.LeadId, l.LeadName, $"{l.CompanyName} · {l.Status}".Trim(' ', '·'), $"/Leads/Details/{l.LeadId}")));

        var os = await (await _scope.ApplyAsync(_db.Opportunities.AsNoTracking().Include(o => o.Customer)))
            .Where(o => EF.Functions.Like(o.OpportunityName, p, "\\") || EF.Functions.Like(o.Customer!.CustomerName, p, "\\"))
            .OrderBy(o => o.OpportunityName).Take(perType).Select(o => new { o.OpportunityId, o.OpportunityName, Customer = o.Customer!.CustomerName, o.Stage }).ToListAsync();
        hits.AddRange(os.Select(o => new SearchHit("Opportunity", o.OpportunityId, o.OpportunityName, $"{o.Customer} · {o.Stage}", $"/Opportunities/Details/{o.OpportunityId}")));
        return hits;
    }
}

public record NotificationItem(int FollowUpId, string Subject, DateOnly Date, bool Overdue, string? Related);
public record NotificationSummary(int Count, int Overdue, IReadOnlyList<NotificationItem> Items);

public interface INotificationService
{
    Task<NotificationSummary> ForCurrentUserAsync();
}

public sealed class NotificationService : INotificationService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IClock _clock;
    public NotificationService(AppDbContext db, ICurrentUser me, IClock clock) { _db = db; _me = me; _clock = clock; }

    /// <summary>Own follow-ups that are overdue or due today.</summary>
    public async Task<NotificationSummary> ForCurrentUserAsync()
    {
        if (_me.UserId is null) return new NotificationSummary(0, 0, Array.Empty<NotificationItem>());
        var today = _clock.Today;
        var q = _db.FollowUps.AsNoTracking().Where(f => f.AssignedTo == _me.UserId && f.Status == FollowUpStatus.Planned && f.FollowUpDate <= today);
        var count = await q.CountAsync();
        var overdue = await q.CountAsync(f => f.FollowUpDate < today);
        var items = await q.OrderBy(f => f.FollowUpDate).Take(5)
            .Select(f => new { f.FollowUpId, f.Subject, f.FollowUpDate, Rel = f.Opportunity!.OpportunityName ?? f.Lead!.LeadName ?? f.Customer!.CustomerName }).ToListAsync();
        return new NotificationSummary(count, overdue, items.Select(i => new NotificationItem(i.FollowUpId, i.Subject, i.FollowUpDate, i.FollowUpDate < today, i.Rel)).ToList());
    }
}
