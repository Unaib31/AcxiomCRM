using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public class DashboardQuery
{
    /// <summary>all | today | week | month | custom</summary>
    public string Range { get; set; } = "all";
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public record ChartSeries(string[] Labels, decimal[] Values);
public record StageChart(string[] Labels, int[] Counts, decimal[] Amounts);
public record MonthlySales(string[] Labels, decimal[] Won, decimal[] Lost, int[] WonCount);
public record TeamRow(string Name, int OpenLeads, int OpenOpportunities, decimal Pipeline, decimal Won);
public record AdminStats(int Users, int ActiveUsers, int LockedUsers, int FailedLogins24h, int AuditEvents24h, List<AuditDto> Recent);

public class DashboardData
{
    public string RangeKey { get; set; } = "all";
    public string RangeLabel { get; set; } = "All time";
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string Scope { get; set; } = "";
    public int TotalCustomers { get; set; }
    public int TotalLeads { get; set; }
    public int OpenLeads { get; set; }
    public int TotalOpportunities { get; set; }
    public int OpenOpportunities { get; set; }
    public int WonOpportunities { get; set; }
    public int LostOpportunities { get; set; }
    public decimal PipelineValue { get; set; }
    public decimal WeightedPipeline { get; set; }
    public int PendingFollowUps { get; set; }
    public int OverdueFollowUps { get; set; }
    public ChartSeries LeadStatus { get; set; } = new(Array.Empty<string>(), Array.Empty<decimal>());
    public StageChart Pipeline { get; set; } = new(Array.Empty<string>(), Array.Empty<int>(), Array.Empty<decimal>());
    public MonthlySales Monthly { get; set; } = new(Array.Empty<string>(), Array.Empty<decimal>(), Array.Empty<decimal>(), Array.Empty<int>());
    public List<FollowUpDto> UpcomingFollowUps { get; set; } = new();
    public List<TeamRow> Team { get; set; } = new();
    public AdminStats? Admin { get; set; }
}

public interface IDashboardService
{
    Task<DashboardData> GetAsync(DashboardQuery q);
}

public sealed class DashboardService : IDashboardService
{
    private readonly AppDbContext _db;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _me;
    private readonly IClock _clock;
    private readonly IAuditQueryService _audit;

    public DashboardService(AppDbContext db, IAccessScope scope, ICurrentUser me, IClock clock, IAuditQueryService audit)
    { _db = db; _scope = scope; _me = me; _clock = clock; _audit = audit; }

    public async Task<DashboardData> GetAsync(DashboardQuery q)
    {
        var today = _clock.Today;
        var (key, from, to, label) = ResolveRange(q, today);
        DateTime? fromUtc = from is null ? null : _clock.StartOfDayUtc(from.Value);
        DateTime? toUtc = to is null ? null : _clock.StartOfDayUtc(to.Value.AddDays(1));
        var scope = await _scope.GetAsync();
        var d = new DashboardData
        {
            RangeKey = key, RangeLabel = label, From = from, To = to,
            Scope = scope.IsAdmin ? "Organisation-wide" : scope.IsManager ? "Your team" : "Your assigned records",
        };

        var customers = await _scope.ApplyAsync(_db.Customers.AsNoTracking());
        var leads = await _scope.ApplyAsync(_db.Leads.AsNoTracking());
        var opps = await _scope.ApplyAsync(_db.Opportunities.AsNoTracking());
        var fups = await _scope.ApplyAsync(_db.FollowUps.AsNoTracking());

        if (fromUtc is { } f && toUtc is { } t)
        {
            customers = customers.Where(c => c.CreatedDate >= f && c.CreatedDate < t);
            leads = leads.Where(l => l.CreatedDate >= f && l.CreatedDate < t);
        }

        d.TotalCustomers = await customers.CountAsync();
        var leadRows = await leads.Select(l => l.Status).ToListAsync();
        d.TotalLeads = leadRows.Count;
        d.OpenLeads = leadRows.Count(LeadWorkflow.IsOpen);
        var statusOrder = new[] { LeadStatus.New, LeadStatus.Contacted, LeadStatus.Qualified, LeadStatus.Unqualified, LeadStatus.Lost, LeadStatus.Converted };
        d.LeadStatus = new ChartSeries(statusOrder.Select(s => s.ToString()).ToArray(), statusOrder.Select(s => (decimal)leadRows.Count(x => x == s)).ToArray());

        var oppRows = await opps.Select(o => new { o.Stage, o.Amount, o.Probability, o.CreatedDate, o.ClosedDate, o.AssignedTo }).ToListAsync();
        bool Created(DateTime c) => fromUtc is null || (c >= fromUtc && c < toUtc);
        var created = oppRows.Where(o => Created(o.CreatedDate)).ToList();
        d.TotalOpportunities = created.Count;
        var open = created.Where(o => OpportunityRules.IsActive(o.Stage)).ToList();
        d.OpenOpportunities = open.Count;
        d.PipelineValue = open.Sum(o => o.Amount);
        d.WeightedPipeline = open.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability));
        bool Closed(DateTime? c) => fromUtc is null || (c is { } cc && cc >= fromUtc && cc < toUtc);
        d.WonOpportunities = oppRows.Count(o => o.Stage == OpportunityStage.Won && Closed(o.ClosedDate));
        d.LostOpportunities = oppRows.Count(o => o.Stage == OpportunityStage.Lost && Closed(o.ClosedDate));

        var stages = Enum.GetValues<OpportunityStage>();
        d.Pipeline = new StageChart(stages.Select(s => s.ToString()).ToArray(),
            stages.Select(s => created.Count(o => o.Stage == s)).ToArray(),
            stages.Select(s => created.Where(o => o.Stage == s).Sum(o => o.Amount)).ToArray());

        // Monthly sales/outcomes: the selected range (max 12 months) or the trailing six months.
        var endMonth = new DateOnly((to ?? today).Year, (to ?? today).Month, 1);
        var startMonth = from is null ? endMonth.AddMonths(-5) : new DateOnly(from.Value.Year, from.Value.Month, 1);
        if (((endMonth.Year - startMonth.Year) * 12 + endMonth.Month - startMonth.Month) > 11) startMonth = endMonth.AddMonths(-11);
        var months = new List<DateOnly>();
        for (var m = startMonth; m <= endMonth; m = m.AddMonths(1)) months.Add(m);
        var closedRows = oppRows.Where(o => o.ClosedDate != null)
            .Select(o => new { o.Stage, o.Amount, Local = _clock.ToLocal(o.ClosedDate!.Value) }).ToList();
        d.Monthly = new MonthlySales(
            months.Select(m => m.ToString("MMM yyyy")).ToArray(),
            months.Select(m => closedRows.Where(c => c.Stage == OpportunityStage.Won && c.Local.Year == m.Year && c.Local.Month == m.Month).Sum(c => c.Amount)).ToArray(),
            months.Select(m => closedRows.Where(c => c.Stage == OpportunityStage.Lost && c.Local.Year == m.Year && c.Local.Month == m.Month).Sum(c => c.Amount)).ToArray(),
            months.Select(m => closedRows.Count(c => c.Stage == OpportunityStage.Won && c.Local.Year == m.Year && c.Local.Month == m.Month)).ToArray());

        d.PendingFollowUps = await fups.CountAsync(x => x.Status == FollowUpStatus.Planned);
        d.OverdueFollowUps = await fups.CountAsync(x => x.Status == FollowUpStatus.Planned && x.FollowUpDate < today);
        var upcoming = await fups.Include(x => x.Customer).Include(x => x.Lead).Include(x => x.Opportunity).Include(x => x.Owner)
            .Where(x => x.Status == FollowUpStatus.Planned).OrderBy(x => x.FollowUpDate).ThenBy(x => x.FollowUpId).Take(8).ToListAsync();
        d.UpcomingFollowUps = upcoming.Select(x => FollowUpService.Map(x, today)).ToList();

        if (!scope.IsAdmin && scope.IsManager)
        {
            var owners = await _db.Users.Where(u => scope.OwnerIds.Contains(u.Id)).Select(u => new { u.Id, u.FullName }).ToListAsync();
            var allLeads = await leads.Select(l => new { l.AssignedTo, l.Status }).ToListAsync();
            d.Team = owners.Select(o => new TeamRow(o.FullName,
                allLeads.Count(l => l.AssignedTo == o.Id && LeadWorkflow.IsOpen(l.Status)),
                oppRows.Count(x => x.AssignedTo == o.Id && OpportunityRules.IsActive(x.Stage)),
                oppRows.Where(x => x.AssignedTo == o.Id && OpportunityRules.IsActive(x.Stage)).Sum(x => x.Amount),
                oppRows.Where(x => x.AssignedTo == o.Id && x.Stage == OpportunityStage.Won).Sum(x => x.Amount))).OrderByDescending(t => t.Pipeline).ToList();
        }
        if (scope.IsAdmin) d.Admin = await AdminStatsAsync();
        return d;
    }

    private async Task<AdminStats> AdminStatsAsync()
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var now = DateTimeOffset.UtcNow;
        var recent = (await _audit.ListAsync(new AuditQuery { PageSize = 8 })).Items.ToList();
        return new AdminStats(
            await _db.Users.CountAsync(), await _db.Users.CountAsync(u => u.IsActive),
            await _db.Users.CountAsync(u => u.LockoutEnd != null && u.LockoutEnd > now),
            await _db.AuditLogs.CountAsync(a => a.Action == "LoginFailed" && a.CreatedDate >= since),
            await _db.AuditLogs.CountAsync(a => a.CreatedDate >= since), recent);
    }

    private static (string Key, DateOnly? From, DateOnly? To, string Label) ResolveRange(DashboardQuery q, DateOnly today)
    {
        switch ((q.Range ?? "all").ToLowerInvariant())
        {
            case "today": return ("today", today, today, "Today");
            case "week":
                var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                return ("week", monday, monday.AddDays(6), "This week");
            case "month":
                var first = new DateOnly(today.Year, today.Month, 1);
                return ("month", first, first.AddMonths(1).AddDays(-1), "This month");
            case "custom" when q.From is { } f && q.To is { } t && f <= t:
                return ("custom", f, t, $"{f:dd MMM yyyy} – {t:dd MMM yyyy}");
            default: return ("all", null, null, "All time");
        }
    }
}
