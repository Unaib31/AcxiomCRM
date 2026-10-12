using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Security;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcxiomCRM.Application.Services;

public class AuditQuery : ListQuery
{
    public string? UserId { get; set; }
    public string? Module { get; set; }
    public string? Action { get; set; }
    public string? Result { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}

public record AuditDto(long AuditLogId, DateTime CreatedLocal, string? UserName, string Module, string Action, string? EntityName,
    string? RecordId, string Result, string? Details, string? IpAddress, string? OldValue, string? NewValue);

public record HistoryItem(DateTime CreatedLocal, string? UserName, string Action, string? Details);

public interface IAuditQueryService
{
    /// <summary>Admin: everything. Manager (if enabled): business events of their team. Others: nothing.</summary>
    Task<PagedResult<AuditDto>> ListAsync(AuditQuery q);
    Task<List<AuditDto>> ExportAsync(AuditQuery q, int max = 20000);
    Task<IReadOnlyList<string>> ModulesAsync();
    Task<IReadOnlyList<string>> ActionsAsync();
    Task<IReadOnlyList<HistoryItem>> HistoryAsync(string entityName, int id, int take = 25);
    bool CanView { get; }
}

public sealed class AuditQueryService : IAuditQueryService
{
    private static readonly string[] RestrictedModules = { "Authentication", "Users", "Security" };
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAccessScope _scope;
    private readonly IClock _clock;
    private readonly SecurityOptions _opt;

    public AuditQueryService(AppDbContext db, ICurrentUser me, IAccessScope scope, IClock clock, IOptions<SecurityOptions> opt)
    { _db = db; _me = me; _scope = scope; _clock = clock; _opt = opt.Value; }

    public bool CanView => _me.IsAdmin || (_me.IsManager && _opt.ManagerCanViewAudit);

    private async Task<IQueryable<AuditLog>> BaseAsync(AuditQuery q)
    {
        IQueryable<AuditLog> a = _db.AuditLogs.AsNoTracking();
        if (!CanView) return a.Where(_ => false);
        if (!_me.IsAdmin)
        {
            var ids = (await _scope.GetAsync()).OwnerIds.ToList();
            a = a.Where(x => x.UserId != null && ids.Contains(x.UserId) && !RestrictedModules.Contains(x.Module));
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var p = QueryHelpers.Like(q.Search);
            a = a.Where(x => EF.Functions.Like(x.Details ?? "", p, "\\") || EF.Functions.Like(x.UserName ?? "", p, "\\") || EF.Functions.Like(x.RecordId ?? "", p, "\\") || EF.Functions.Like(x.EntityName ?? "", p, "\\"));
        }
        if (!string.IsNullOrWhiteSpace(q.UserId)) a = a.Where(x => x.UserId == q.UserId);
        if (!string.IsNullOrWhiteSpace(q.Module)) a = a.Where(x => x.Module == q.Module);
        if (!string.IsNullOrWhiteSpace(q.Action)) a = a.Where(x => x.Action == q.Action);
        if (!string.IsNullOrWhiteSpace(q.Result)) a = a.Where(x => x.Result == q.Result);
        if (q.From is { } f) { var from = _clock.StartOfDayUtc(f); a = a.Where(x => x.CreatedDate >= from); }
        if (q.To is { } t) { var to = _clock.StartOfDayUtc(t.AddDays(1)); a = a.Where(x => x.CreatedDate < to); }
        return q.Desc && q.Sort == "date" ? a.OrderBy(x => x.CreatedDate).ThenBy(x => x.AuditLogId) : a.OrderByDescending(x => x.CreatedDate).ThenByDescending(x => x.AuditLogId);
    }

    public async Task<PagedResult<AuditDto>> ListAsync(AuditQuery q)
    {
        var page = await (await BaseAsync(q)).ToPagedAsync(q);
        return new PagedResult<AuditDto> { Items = page.Items.Select(Map).ToList(), Page = page.Page, PageSize = page.PageSize, TotalCount = page.TotalCount };
    }

    public async Task<List<AuditDto>> ExportAsync(AuditQuery q, int max = 20000) =>
        (await (await BaseAsync(q)).Take(max).ToListAsync()).Select(Map).ToList();

    public async Task<IReadOnlyList<string>> ModulesAsync() => !CanView ? new List<string>() :
        await _db.AuditLogs.Where(a => _me.IsAdmin || !RestrictedModules.Contains(a.Module)).Select(a => a.Module).Distinct().OrderBy(x => x).ToListAsync();

    public async Task<IReadOnlyList<string>> ActionsAsync() => !CanView ? new List<string>() :
        await _db.AuditLogs.Where(a => _me.IsAdmin || !RestrictedModules.Contains(a.Module)).Select(a => a.Action).Distinct().OrderBy(x => x).ToListAsync();

    /// <summary>Caller must already have verified the record is within the user's scope.</summary>
    public async Task<IReadOnlyList<HistoryItem>> HistoryAsync(string entityName, int id, int take = 25)
    {
        var rid = id.ToString();
        var rows = await _db.AuditLogs.AsNoTracking().Where(a => a.EntityName == entityName && a.RecordId == rid)
            .OrderByDescending(a => a.CreatedDate).ThenByDescending(a => a.AuditLogId).Take(take).ToListAsync();
        return rows.Select(a => new HistoryItem(_clock.ToLocal(a.CreatedDate), a.UserName, a.Action, a.Details)).ToList();
    }

    private AuditDto Map(AuditLog a) => new(a.AuditLogId, _clock.ToLocal(a.CreatedDate), a.UserName, a.Module, a.Action, a.EntityName, a.RecordId,
        a.Result, a.Details, _me.IsAdmin ? a.IpAddress : null, _me.IsAdmin ? a.OldValue : null, _me.IsAdmin ? a.NewValue : null);
}
