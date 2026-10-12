using System.Globalization;
using System.Text;
using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Security;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public class ReportQuery : ListQuery
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Status { get; set; }
}

public record ReportDefinition(string Key, string Title, string Description, string Icon, string[] Roles, string StatusLabel = "Status", string[]? StatusOptions = null);

public class ReportSection
{
    public string Title { get; set; } = "";
    public string[] Columns { get; set; } = Array.Empty<string>();
    public List<string[]> Rows { get; set; } = new();
    /// <summary>Indexes of columns that hold numbers (right aligned).</summary>
    public int[] NumericColumns { get; set; } = Array.Empty<int>();
    /// <summary>Sort key per column (null = not sortable); keys are the whitelist used by the report queries.</summary>
    public string?[]? SortKeys { get; set; }
}

public class ReportResult
{
    public ReportDefinition Definition { get; set; } = default!;
    public List<ReportSection> Sections { get; set; } = new();
    public Dictionary<string, string> Summary { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; }
    public bool Paged { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public record PipelineStageRow(string Stage, int Count, decimal Amount, decimal WeightedAmount);
public record PipelineOwnerRow(string OwnerId, string Owner, int OpenCount, decimal OpenAmount, decimal WeightedAmount);
public record PipelineSummary(IReadOnlyList<PipelineStageRow> ByStage, IReadOnlyList<PipelineOwnerRow> ByOwner, int OpenCount, decimal OpenAmount, decimal WeightedAmount);

public interface IReportService
{
    Task<PipelineSummary> PipelineSummaryAsync();
    IReadOnlyList<ReportDefinition> Available();
    ReportDefinition? Find(string key);
    Task<ReportResult> RunAsync(string key, ReportQuery q);
    Task<string> ExportCsvAsync(string key, ReportQuery q);
}

public sealed class ReportService : IReportService
{
    private const int ExportLimit = 50_000;
    private static readonly string[] All = RoleNames.All;
    private static readonly string[] Mgmt = { RoleNames.Admin, RoleNames.Manager };

    private static readonly ReportDefinition[] Definitions =
    {
        new("customers", "Customer Report", "Customer list with status, owner and creation date.", "bi-people", All, "Status", Enum.GetNames<CustomerStatus>()),
        new("leads", "Lead Report", "Lead source, status, owner and conversion information.", "bi-funnel", All, "Status", Enum.GetNames<LeadStatus>()),
        new("followups", "Follow-Up Report", "Planned, completed, missed and overdue activities.", "bi-calendar-check", All, "Status", new[] { "Planned", "Completed", "Missed", "Cancelled", "Overdue" }),
        new("opportunities", "Opportunity Report", "Stage, amount, probability and expected close date.", "bi-briefcase", All, "Stage", Enum.GetNames<OpportunityStage>()),
        new("pipeline", "Pipeline Report", "Stage-wise and owner-wise pipeline amounts, including weighted pipeline.", "bi-bar-chart-steps", All),
        new("conversion", "Sales & Conversion Report", "Lead conversion and opportunity outcome metrics.", "bi-graph-up-arrow", All),
        new("useractivity", "User Activity Report", "Actions and activity counts per user.", "bi-person-lines-fill", Mgmt),
        new("audit", "Audit Report", "Security and business audit events.", "bi-shield-check", new[] { RoleNames.Admin }, "Result", new[] { "Success", "Failure" }),
    };

    private readonly AppDbContext _db;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _me;
    private readonly IClock _clock;
    private readonly IAuditQueryService _audit;

    public ReportService(AppDbContext db, IAccessScope scope, ICurrentUser me, IClock clock, IAuditQueryService audit)
    { _db = db; _scope = scope; _me = me; _clock = clock; _audit = audit; }

    public IReadOnlyList<ReportDefinition> Available() => Definitions.Where(d => _me.Role is { } r && d.Roles.Contains(r)).ToList();
    public ReportDefinition? Find(string key) => Available().FirstOrDefault(d => d.Key == key);

    public async Task<ReportResult> RunAsync(string key, ReportQuery q) => await BuildAsync(key, q, export: false);

    public async Task<string> ExportCsvAsync(string key, ReportQuery q)
    {
        var r = await BuildAsync(key, q, export: true);
        var sb = new StringBuilder();
        foreach (var s in r.Sections)
        {
            if (r.Sections.Count > 1) sb.AppendLine(Csv(s.Title));
            sb.AppendLine(string.Join(",", s.Columns.Select(Csv)));
            foreach (var row in s.Rows) sb.AppendLine(string.Join(",", row.Select(Csv)));
            if (r.Sections.Count > 1) sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Quotes the value and neutralises spreadsheet formulas (CSV injection).</summary>
    public static string Csv(string? v)
    {
        v ??= "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    public async Task<PipelineSummary> PipelineSummaryAsync()
    {
        var rows = await (await _scope.ApplyAsync(_db.Opportunities.AsNoTracking())).Select(o => new { o.Stage, o.Amount, o.Probability, o.AssignedTo }).ToListAsync();
        var names = await UserNamesAsync();
        var byStage = Enum.GetValues<OpportunityStage>().Select(s =>
        {
            var g = rows.Where(o => o.Stage == s).ToList();
            return new PipelineStageRow(s.ToString(), g.Count, g.Sum(o => o.Amount), g.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability)));
        }).ToList();
        var open = rows.Where(o => OpportunityRules.IsActive(o.Stage)).ToList();
        var byOwner = open.GroupBy(o => o.AssignedTo).Select(g => new PipelineOwnerRow(g.Key, names.GetValueOrDefault(g.Key, "Unknown"), g.Count(),
            g.Sum(o => o.Amount), g.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability)))).OrderByDescending(o => o.OpenAmount).ToList();
        return new PipelineSummary(byStage, byOwner, open.Count, open.Sum(o => o.Amount), open.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability)));
    }

    private async Task<ReportResult> BuildAsync(string key, ReportQuery q, bool export)
    {
        var def = Find(key) ?? throw new KeyNotFoundException("Unknown or unauthorised report.");
        var res = new ReportResult { Definition = def, Page = q.Page, PageSize = q.PageSize };
        switch (key)
        {
            case "customers": await CustomersAsync(res, q, export); break;
            case "leads": await LeadsAsync(res, q, export); break;
            case "followups": await FollowUpsAsync(res, q, export); break;
            case "opportunities": await OpportunitiesAsync(res, q, export); break;
            case "pipeline": await PipelineAsync(res, q); break;
            case "conversion": await ConversionAsync(res, q); break;
            case "useractivity": await UserActivityAsync(res, q); break;
            case "audit": await AuditAsync(res, q, export); break;
        }
        return res;
    }

    private (DateTime? From, DateTime? To) Bounds(ReportQuery q) =>
        (q.From is { } f ? _clock.StartOfDayUtc(f) : null, q.To is { } t ? _clock.StartOfDayUtc(t.AddDays(1)) : null);

    private static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
    private string Local(DateTime utc) => _clock.ToLocal(utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private async Task<Dictionary<string, string>> UserNamesAsync() =>
        await _db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.FullName);

    private async Task CustomersAsync(ReportResult r, ReportQuery q, bool export)
    {
        var (from, to) = Bounds(q);
        var cq = await _scope.ApplyAsync(_db.Customers.AsNoTracking().Include(c => c.Owner));
        if (Enum.TryParse<CustomerStatus>(q.Status, out var st) && Enum.IsDefined(st)) cq = cq.Where(c => c.Status == st);
        if (!string.IsNullOrWhiteSpace(q.AssignedTo)) cq = cq.Where(c => c.AssignedTo == q.AssignedTo);
        if (from is { } f) cq = cq.Where(c => c.CreatedDate >= f);
        if (to is { } t) cq = cq.Where(c => c.CreatedDate < t);
        if (!string.IsNullOrWhiteSpace(q.Search)) { var p = QueryHelpers.Like(q.Search); cq = cq.Where(c => EF.Functions.Like(c.CustomerName, p, "\\") || EF.Functions.Like(c.CompanyName ?? "", p, "\\") || EF.Functions.Like(c.Email, p, "\\")); }
        r.TotalCount = await cq.CountAsync();
        cq = (q.Sort, q.Desc) switch
        {
            ("name", false) => cq.OrderBy(c => c.CustomerName), ("name", true) => cq.OrderByDescending(c => c.CustomerName),
            ("status", false) => cq.OrderBy(c => c.Status), ("status", true) => cq.OrderByDescending(c => c.Status),
            ("created", false) => cq.OrderBy(c => c.CreatedDate),
            _ => cq.OrderByDescending(c => c.CreatedDate),
        };
        var rows = await Page(cq, q, export, r).ToListAsync();
        r.Paged = !export;
        r.Sections.Add(new ReportSection
        {
            Title = "Customers", Columns = new[] { "Code", "Name", "Company", "Email", "Status", "Owner", "Created" },
            SortKeys = new string?[] { null, "name", null, null, "status", null, "created" },
            Rows = rows.Select(c => new[] { c.CustomerCode, c.CustomerName, c.CompanyName ?? "", c.Email, c.Status.ToString(), c.Owner?.FullName ?? "", Local(c.CreatedDate) }).ToList(),
        });
        r.Summary["Customers"] = r.TotalCount.ToString();
    }

    private async Task LeadsAsync(ReportResult r, ReportQuery q, bool export)
    {
        var (from, to) = Bounds(q);
        var lq = await _scope.ApplyAsync(_db.Leads.AsNoTracking().Include(l => l.Owner));
        if (Enum.TryParse<LeadStatus>(q.Status, out var st) && Enum.IsDefined(st)) lq = lq.Where(l => l.Status == st);
        if (!string.IsNullOrWhiteSpace(q.AssignedTo)) lq = lq.Where(l => l.AssignedTo == q.AssignedTo);
        if (from is { } f) lq = lq.Where(l => l.CreatedDate >= f);
        if (to is { } t) lq = lq.Where(l => l.CreatedDate < t);
        if (!string.IsNullOrWhiteSpace(q.Search)) { var p = QueryHelpers.Like(q.Search); lq = lq.Where(l => EF.Functions.Like(l.LeadName, p, "\\") || EF.Functions.Like(l.CompanyName ?? "", p, "\\")); }
        r.TotalCount = await lq.CountAsync();
        lq = (q.Sort, q.Desc) switch
        {
            ("name", false) => lq.OrderBy(l => l.LeadName), ("name", true) => lq.OrderByDescending(l => l.LeadName),
            ("status", false) => lq.OrderBy(l => l.Status), ("status", true) => lq.OrderByDescending(l => l.Status),
            ("created", false) => lq.OrderBy(l => l.CreatedDate),
            _ => lq.OrderByDescending(l => l.CreatedDate),
        };
        var rows = await Page(lq, q, export, r).ToListAsync();
        r.Paged = !export;
        r.Sections.Add(new ReportSection
        {
            Title = "Leads", Columns = new[] { "Code", "Lead", "Company", "Source", "Status", "Priority", "Expected Value", "Owner", "Converted", "Created" },
            SortKeys = new string?[] { null, "name", null, null, "status", null, null, null, null, "created" },
            NumericColumns = new[] { 6 },
            Rows = rows.Select(l => new[] { l.LeadCode, l.LeadName, l.CompanyName ?? "", l.Source.ToString(), l.Status.ToString(), l.Priority.ToString(), Money(l.ExpectedValue),
                l.Owner?.FullName ?? "", l.ConvertedDate is { } c ? Local(c) : "No", Local(l.CreatedDate) }).ToList(),
        });
        r.Summary["Leads"] = r.TotalCount.ToString();
    }

    private async Task FollowUpsAsync(ReportResult r, ReportQuery q, bool export)
    {
        var today = _clock.Today;
        var fq = await _scope.ApplyAsync(_db.FollowUps.AsNoTracking().Include(x => x.Customer).Include(x => x.Lead).Include(x => x.Opportunity).Include(x => x.Owner));
        if (q.Status == "Overdue") fq = fq.Where(x => x.Status == FollowUpStatus.Planned && x.FollowUpDate < today);
        else if (Enum.TryParse<FollowUpStatus>(q.Status, out var st) && Enum.IsDefined(st)) fq = fq.Where(x => x.Status == st);
        if (!string.IsNullOrWhiteSpace(q.AssignedTo)) fq = fq.Where(x => x.AssignedTo == q.AssignedTo);
        if (q.From is { } f) fq = fq.Where(x => x.FollowUpDate >= f);
        if (q.To is { } t) fq = fq.Where(x => x.FollowUpDate <= t);
        if (!string.IsNullOrWhiteSpace(q.Search)) { var p = QueryHelpers.Like(q.Search); fq = fq.Where(x => EF.Functions.Like(x.Subject, p, "\\")); }
        r.TotalCount = await fq.CountAsync();
        fq = q.Desc ? fq.OrderByDescending(x => x.FollowUpDate) : fq.OrderBy(x => x.FollowUpDate);
        var rows = await Page(fq, q, export, r).ToListAsync();
        r.Paged = !export;
        r.Sections.Add(new ReportSection
        {
            Title = "Follow-ups", Columns = new[] { "Date", "Subject", "Related To", "Type", "Status", "Overdue", "Owner" },
            SortKeys = new string?[] { "date", null, null, null, null, null, null },
            Rows = rows.Select(x => new[] { x.FollowUpDate.ToString("yyyy-MM-dd"), x.Subject,
                x.Opportunity?.OpportunityName ?? x.Lead?.LeadName ?? x.Customer?.CustomerName ?? "", x.FollowUpType.ToString(), x.Status.ToString(),
                FollowUpRules.IsOverdue(x.Status, x.FollowUpDate, today) ? "Yes" : "No", x.Owner?.FullName ?? "" }).ToList(),
        });
        var totals = await (await _scope.ApplyAsync(_db.FollowUps.AsNoTracking())).GroupBy(x => x.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        foreach (var s in Enum.GetValues<FollowUpStatus>()) r.Summary[s.ToString()] = (totals.FirstOrDefault(x => x.Key == s)?.N ?? 0).ToString();
        r.Summary["Overdue"] = (await (await _scope.ApplyAsync(_db.FollowUps.AsNoTracking())).CountAsync(x => x.Status == FollowUpStatus.Planned && x.FollowUpDate < today)).ToString();
    }

    private async Task OpportunitiesAsync(ReportResult r, ReportQuery q, bool export)
    {
        var (from, to) = Bounds(q);
        var oq = await _scope.ApplyAsync(_db.Opportunities.AsNoTracking().Include(o => o.Customer).Include(o => o.Owner));
        if (Enum.TryParse<OpportunityStage>(q.Status, out var st) && Enum.IsDefined(st)) oq = oq.Where(o => o.Stage == st);
        if (!string.IsNullOrWhiteSpace(q.AssignedTo)) oq = oq.Where(o => o.AssignedTo == q.AssignedTo);
        if (q.From is { } f) oq = oq.Where(o => o.ExpectedCloseDate >= f);
        if (q.To is { } t) oq = oq.Where(o => o.ExpectedCloseDate <= t);
        if (!string.IsNullOrWhiteSpace(q.Search)) { var p = QueryHelpers.Like(q.Search); oq = oq.Where(o => EF.Functions.Like(o.OpportunityName, p, "\\") || EF.Functions.Like(o.Customer!.CustomerName, p, "\\")); }
        r.TotalCount = await oq.CountAsync();
        oq = (q.Sort, q.Desc) switch
        {
            ("amount", false) => oq.OrderBy(o => o.Amount), ("amount", true) => oq.OrderByDescending(o => o.Amount),
            ("close", true) => oq.OrderByDescending(o => o.ExpectedCloseDate),
            _ => oq.OrderBy(o => o.ExpectedCloseDate),
        };
        var rows = await Page(oq, q, export, r).ToListAsync();
        r.Paged = !export;
        r.Sections.Add(new ReportSection
        {
            Title = "Opportunities", Columns = new[] { "Code", "Opportunity", "Customer", "Stage", "Amount", "Probability %", "Weighted", "Expected Close", "Owner" },
            SortKeys = new string?[] { null, null, null, null, "amount", null, null, "close", null },
            NumericColumns = new[] { 4, 5, 6 },
            Rows = rows.Select(o => new[] { o.OpportunityCode, o.OpportunityName, o.Customer?.CustomerName ?? "", o.Stage.ToString(), Money(o.Amount),
                o.Probability.ToString(), Money(o.WeightedAmount), o.ExpectedCloseDate.ToString("yyyy-MM-dd"), o.Owner?.FullName ?? "" }).ToList(),
        });
        var all = await (await _scope.ApplyAsync(_db.Opportunities.AsNoTracking())).Select(o => new { o.Amount, o.Stage }).ToListAsync();
        r.Summary["Total amount"] = Money(all.Sum(o => o.Amount));
        r.Summary["Open amount"] = Money(all.Where(o => OpportunityRules.IsActive(o.Stage)).Sum(o => o.Amount));
    }

    private async Task PipelineAsync(ReportResult r, ReportQuery q)
    {
        var (from, to) = Bounds(q);
        var oq = await _scope.ApplyAsync(_db.Opportunities.AsNoTracking());
        if (!string.IsNullOrWhiteSpace(q.AssignedTo)) oq = oq.Where(o => o.AssignedTo == q.AssignedTo);
        if (from is { } f) oq = oq.Where(o => o.CreatedDate >= f);
        if (to is { } t) oq = oq.Where(o => o.CreatedDate < t);
        var rows = await oq.Select(o => new { o.Stage, o.Amount, o.Probability, o.AssignedTo }).ToListAsync();
        var names = await UserNamesAsync();
        var stages = Enum.GetValues<OpportunityStage>();

        var byStage = new ReportSection { Title = "Pipeline by stage", Columns = new[] { "Stage", "Opportunities", "Amount", "Weighted amount" }, NumericColumns = new[] { 1, 2, 3 } };
        foreach (var s in stages)
        {
            var g = rows.Where(o => o.Stage == s).ToList();
            byStage.Rows.Add(new[] { s.ToString(), g.Count.ToString(), Money(g.Sum(o => o.Amount)), Money(g.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability))) });
        }
        byStage.Rows.Add(new[] { "Total", rows.Count.ToString(), Money(rows.Sum(o => o.Amount)), Money(rows.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability))) });

        var active = stages.Where(OpportunityRules.IsActive).ToArray();
        var byOwner = new ReportSection
        {
            Title = "Open pipeline by owner (amount)",
            Columns = new[] { "Owner" }.Concat(active.Select(s => s.ToString())).Concat(new[] { "Open total", "Weighted" }).ToArray(),
        };
        byOwner.NumericColumns = Enumerable.Range(1, active.Length + 2).ToArray();
        foreach (var g in rows.Where(o => OpportunityRules.IsActive(o.Stage)).GroupBy(o => o.AssignedTo).OrderByDescending(g => g.Sum(x => x.Amount)))
            byOwner.Rows.Add(new[] { names.GetValueOrDefault(g.Key, "Unknown") }
                .Concat(active.Select(s => Money(g.Where(o => o.Stage == s).Sum(o => o.Amount))))
                .Concat(new[] { Money(g.Sum(o => o.Amount)), Money(g.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability))) }).ToArray());
        r.Sections.Add(byStage);
        r.Sections.Add(byOwner);
        var open = rows.Where(o => OpportunityRules.IsActive(o.Stage)).ToList();
        r.Summary["Open pipeline"] = Money(open.Sum(o => o.Amount));
        r.Summary["Weighted pipeline"] = Money(open.Sum(o => OpportunityRules.Weighted(o.Amount, o.Probability)));
        r.Summary["Open opportunities"] = open.Count.ToString();
    }

    private async Task ConversionAsync(ReportResult r, ReportQuery q)
    {
        var (from, to) = Bounds(q);
        var lq = await _scope.ApplyAsync(_db.Leads.AsNoTracking());
        var oq = await _scope.ApplyAsync(_db.Opportunities.AsNoTracking());
        if (!string.IsNullOrWhiteSpace(q.AssignedTo)) { lq = lq.Where(l => l.AssignedTo == q.AssignedTo); oq = oq.Where(o => o.AssignedTo == q.AssignedTo); }
        if (from is { } f) { lq = lq.Where(l => l.CreatedDate >= f); oq = oq.Where(o => o.CreatedDate >= f); }
        if (to is { } t) { lq = lq.Where(l => l.CreatedDate < t); oq = oq.Where(o => o.CreatedDate < t); }
        var leads = await lq.Select(l => new { l.Source, l.Status, l.AssignedTo }).ToListAsync();
        var opps = await oq.Select(o => new { o.Stage, o.Amount, o.AssignedTo }).ToListAsync();
        var names = await UserNamesAsync();

        string Pct(int a, int b) => b == 0 ? "0.0%" : (a * 100.0 / b).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        var bySource = new ReportSection { Title = "Lead conversion by source", Columns = new[] { "Source", "Leads", "Converted", "Not converted", "Conversion rate" }, NumericColumns = new[] { 1, 2, 3, 4 } };
        foreach (var s in Enum.GetValues<LeadSource>())
        {
            var g = leads.Where(l => l.Source == s).ToList();
            if (g.Count == 0) continue;
            var conv = g.Count(l => l.Status == LeadStatus.Converted);
            bySource.Rows.Add(new[] { s.ToString(), g.Count.ToString(), conv.ToString(), (g.Count - conv).ToString(), Pct(conv, g.Count) });
        }
        var totalConv = leads.Count(l => l.Status == LeadStatus.Converted);
        bySource.Rows.Add(new[] { "Total", leads.Count.ToString(), totalConv.ToString(), (leads.Count - totalConv).ToString(), Pct(totalConv, leads.Count) });

        var byOwner = new ReportSection { Title = "Opportunity outcomes by owner", Columns = new[] { "Owner", "Won", "Lost", "Win rate", "Won amount", "Lost amount", "Avg won deal" }, NumericColumns = new[] { 1, 2, 3, 4, 5, 6 } };
        foreach (var g in opps.GroupBy(o => o.AssignedTo).OrderBy(g => names.GetValueOrDefault(g.Key, "")))
        {
            var won = g.Where(o => o.Stage == OpportunityStage.Won).ToList();
            var lost = g.Where(o => o.Stage == OpportunityStage.Lost).ToList();
            if (won.Count + lost.Count == 0) continue;
            byOwner.Rows.Add(new[] { names.GetValueOrDefault(g.Key, "Unknown"), won.Count.ToString(), lost.Count.ToString(), Pct(won.Count, won.Count + lost.Count),
                Money(won.Sum(o => o.Amount)), Money(lost.Sum(o => o.Amount)), Money(won.Count == 0 ? 0 : won.Sum(o => o.Amount) / won.Count) });
        }
        r.Sections.Add(bySource);
        r.Sections.Add(byOwner);
        var wonAll = opps.Count(o => o.Stage == OpportunityStage.Won);
        var lostAll = opps.Count(o => o.Stage == OpportunityStage.Lost);
        r.Summary["Lead conversion rate"] = Pct(totalConv, leads.Count);
        r.Summary["Win rate"] = Pct(wonAll, wonAll + lostAll);
        r.Summary["Won amount"] = Money(opps.Where(o => o.Stage == OpportunityStage.Won).Sum(o => o.Amount));
    }

    private async Task UserActivityAsync(ReportResult r, ReportQuery q)
    {
        var (from, to) = Bounds(q);
        var scope = await _scope.GetAsync();
        var aq = _db.AuditLogs.AsNoTracking().Where(a => a.UserId != null);
        if (!scope.IsAdmin) { var ids = scope.OwnerIds.ToList(); aq = aq.Where(a => ids.Contains(a.UserId!)); }
        if (!string.IsNullOrWhiteSpace(q.AssignedTo)) aq = aq.Where(a => a.UserId == q.AssignedTo);
        if (from is { } f) aq = aq.Where(a => a.CreatedDate >= f);
        if (to is { } t) aq = aq.Where(a => a.CreatedDate < t);
        var rows = await aq.Select(a => new { a.UserId, a.Action, a.Result, a.CreatedDate }).ToListAsync();
        var names = await UserNamesAsync();
        var sec = new ReportSection { Title = "Activity by user", Columns = new[] { "User", "Total actions", "Logins", "Failed logins", "Creates", "Updates", "Deletes", "Last activity" }, NumericColumns = new[] { 1, 2, 3, 4, 5, 6 } };
        foreach (var g in rows.GroupBy(x => x.UserId!).OrderByDescending(g => g.Count()))
            sec.Rows.Add(new[] { names.GetValueOrDefault(g.Key, "Unknown"), g.Count().ToString(), g.Count(x => x.Action == "Login").ToString(), g.Count(x => x.Action == "LoginFailed").ToString(),
                g.Count(x => x.Action == "Create").ToString(), g.Count(x => x.Action is "Update" or "StatusChange" or "Reschedule").ToString(), g.Count(x => x.Action == "Delete").ToString(), Local(g.Max(x => x.CreatedDate)) });
        r.Sections.Add(sec);
        r.Summary["Total actions"] = rows.Count.ToString();
        r.Summary["Active users"] = rows.Select(x => x.UserId).Distinct().Count().ToString();
    }

    private async Task AuditAsync(ReportResult r, ReportQuery q, bool export)
    {
        var aq = new AuditQuery { From = q.From, To = q.To, Result = q.Status, Search = q.Search, UserId = q.AssignedTo, Page = q.Page, PageSize = q.PageSize };
        List<AuditDto> items;
        if (export) { items = await _audit.ExportAsync(aq, ExportLimit); r.TotalCount = items.Count; }
        else { var p = await _audit.ListAsync(aq); items = p.Items.ToList(); r.TotalCount = p.TotalCount; r.Paged = true; }
        r.Sections.Add(new ReportSection
        {
            Title = "Audit events", Columns = new[] { "Time", "User", "Module", "Action", "Entity", "Record", "Result", "Details" },
            Rows = items.Select(a => new[] { a.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), a.UserName ?? "", a.Module, a.Action, a.EntityName ?? "", a.RecordId ?? "", a.Result, a.Details ?? "" }).ToList(),
        });
        r.Summary["Events"] = r.TotalCount.ToString();
    }

    private static IQueryable<T> Page<T>(IQueryable<T> query, ReportQuery q, bool export, ReportResult r) =>
        export ? query.Take(ExportLimit) : query.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize);
}
