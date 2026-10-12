using AcxiomCRM.Application.Common;
using AcxiomCRM.Data;
using AcxiomCRM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

/// <summary>Explicit audit entries for events that are not plain entity changes (auth, security, workflows).</summary>
public interface IAuditService
{
    Task LogAsync(string module, string action, string? entityName = null, string? recordId = null,
        string? details = null, string result = "Success", string? userId = null, string? userName = null,
        string? oldValue = null, string? newValue = null);
}

public sealed class AuditService : IAuditService
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public AuditService(AppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task LogAsync(string module, string action, string? entityName = null, string? recordId = null,
        string? details = null, string result = "Success", string? userId = null, string? userName = null,
        string? oldValue = null, string? newValue = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = userId ?? _user.UserId,
            UserName = Trim(userName ?? _user.UserName, 256),
            Module = module,
            Action = action,
            EntityName = entityName,
            RecordId = recordId,
            Details = Trim(details, 1000),
            OldValue = Trim(oldValue, 4000),
            NewValue = Trim(newValue, 4000),
            Result = result,
            IpAddress = _user.IpAddress,
            CreatedDate = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private static string? Trim(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);
}
