using AcxiomCRM.Application.Common;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

/// <summary>
/// Server-side resource authorization. Admin sees everything, Manager sees own + direct reports,
/// Sales Executive sees only records assigned to them. Every query goes through <see cref="ApplyAsync{T}"/>.
/// </summary>
public sealed class AccessScopeService : IAccessScope
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private ScopeInfo? _scope;

    public AccessScopeService(AppDbContext db, ICurrentUser user) { _db = db; _user = user; }

    public async Task<ScopeInfo> GetAsync()
    {
        if (_scope is not null) return _scope;
        var uid = _user.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var role = _user.Role ?? RoleNames.SalesExecutive;
        var ids = new List<string> { uid };
        if (role == RoleNames.Manager)
            ids.AddRange(await _db.Users.Where(u => u.ManagerId == uid).Select(u => u.Id).ToListAsync());
        return _scope = new ScopeInfo(uid, role, role == RoleNames.Admin, ids);
    }

    public async Task<IQueryable<T>> ApplyAsync<T>(IQueryable<T> query) where T : IOwnedRecord
    {
        var s = await GetAsync();
        if (s.IsAdmin) return query;
        var ids = s.OwnerIds.ToList();
        return query.Where(x => ids.Contains(x.AssignedTo));
    }

    public async Task<IReadOnlyList<UserOption>> AssignableUsersAsync()
    {
        var s = await GetAsync();
        var q = _db.Users.Where(u => u.IsActive);
        if (!s.IsAdmin)
        {
            var ids = s.OwnerIds.ToList();
            q = q.Where(u => ids.Contains(u.Id));
        }
        var users = await q.OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName }).ToListAsync();
        var roleMap = await (from ur in _db.UserRoles join r in _db.Roles on ur.RoleId equals r.Id
                             select new { ur.UserId, r.Name }).ToListAsync();
        return users.Select(u => new UserOption(u.Id, u.FullName,
            RoleNames.Display(roleMap.FirstOrDefault(r => r.UserId == u.Id)?.Name ?? ""))).ToList();
    }

    public async Task<(string? OwnerId, string? Error)> ResolveAssigneeAsync(string? requested)
    {
        var s = await GetAsync();
        if (string.IsNullOrWhiteSpace(requested)) requested = s.UserId;
        if (!s.IsAdmin && !s.OwnerIds.Contains(requested))
            return (null, "You can only assign records to yourself or your team.");
        var ok = await _db.Users.AnyAsync(u => u.Id == requested && u.IsActive);
        return ok ? (requested, null) : (null, "Select a valid active user.");
    }
}
