using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Security;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Domain.Rules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Application.Services;

public class UserEditInput
{
    [Required(ErrorMessage = "Full name is required."), StringLength(150, ErrorMessage = "Full name cannot exceed 150 characters."), Display(Name = "Full Name")]
    public string? FullName { get; set; }

    [Required(ErrorMessage = "Email is required."), StringLength(254, ErrorMessage = "Email cannot exceed 254 characters."),
     RegularExpression(ValidationRules.EmailPattern, ErrorMessage = ValidationRules.EmailMessage)]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Role is required.")]
    public string? Role { get; set; }

    [Display(Name = "Reports To")]
    public string? ManagerId { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;
}

public class UserCreateInput : UserEditInput
{
    [Required(ErrorMessage = "Password is required."), StringLength(128, ErrorMessage = "Password cannot exceed 128 characters."), DataType(DataType.Password)]
    public string? Password { get; set; }

    [Required(ErrorMessage = "Please confirm the password."), DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match."), Display(Name = "Confirm Password")]
    public string? ConfirmPassword { get; set; }
}

public class UserQuery : ListQuery
{
    public string? Role { get; set; }
    public bool? Active { get; set; }
    public bool? Locked { get; set; }
}

public record UserDto(string Id, string FullName, string Email, string Role, bool IsActive, bool IsLockedOut, DateTimeOffset? LockoutEnd,
    int FailedLoginCount, string? ManagerId, string? ManagerName, DateTime CreatedDate, DateTime? LastLoginDate);

public interface IUserAdminService
{
    Task<PagedResult<UserDto>> ListAsync(UserQuery q);
    Task<ServiceResult<UserDto>> GetAsync(string id);
    Task<ServiceResult<UserDto>> CreateAsync(UserCreateInput input);
    Task<ServiceResult<UserDto>> UpdateAsync(string id, UserEditInput input);
    Task<ServiceResult> SetActiveAsync(string id, bool active);
    Task<ServiceResult> UnlockAsync(string id);
    Task<ServiceResult<string>> CreateResetTokenAsync(string id);
    Task<IReadOnlyList<UserOption>> ManagersAsync();
    Task<IReadOnlyList<(string Role, string Description, int Users)>> RolesAsync();
}

public sealed class UserAdminService : IUserAdminService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<ApplicationRole> _roles;
    private readonly AppDbContext _db;
    private readonly ICurrentUser _me;
    private readonly IAuditService _audit;
    private readonly IAuthService _auth;

    public UserAdminService(UserManager<ApplicationUser> users, RoleManager<ApplicationRole> roles, AppDbContext db,
        ICurrentUser me, IAuditService audit, IAuthService auth)
    { _users = users; _roles = roles; _db = db; _me = me; _audit = audit; _auth = auth; }

    private IQueryable<ApplicationUser> Visible()
    {
        var q = _db.Users.Include(u => u.Manager).AsNoTracking();
        if (_me.IsAdmin) return q;
        if (!_me.IsManager) return q.Where(_ => false);
        var uid = _me.UserId!;
        return q.Where(u => u.Id == uid || u.ManagerId == uid); // managers only see their team
    }

    public async Task<PagedResult<UserDto>> ListAsync(UserQuery lq)
    {
        var q = Visible();
        if (!string.IsNullOrWhiteSpace(lq.Search))
        {
            var p = QueryHelpers.Like(lq.Search);
            q = q.Where(u => EF.Functions.Like(u.FullName, p, "\\") || EF.Functions.Like(u.Email ?? "", p, "\\"));
        }
        if (lq.Active is { } a) q = q.Where(u => u.IsActive == a);
        if (lq.Locked is { } l)
        {
            var now = DateTimeOffset.UtcNow;
            q = l ? q.Where(u => u.LockoutEnd != null && u.LockoutEnd > now) : q.Where(u => u.LockoutEnd == null || u.LockoutEnd <= now);
        }
        if (!string.IsNullOrWhiteSpace(lq.Role))
            q = q.Where(u => _db.UserRoles.Any(ur => ur.UserId == u.Id && _db.Roles.Any(r => r.Id == ur.RoleId && r.Name == lq.Role)));
        q = (lq.Sort?.ToLowerInvariant(), lq.Desc) switch
        {
            ("email", false) => q.OrderBy(u => u.Email), ("email", true) => q.OrderByDescending(u => u.Email),
            ("created", false) => q.OrderBy(u => u.CreatedDate), ("created", true) => q.OrderByDescending(u => u.CreatedDate),
            ("name", true) => q.OrderByDescending(u => u.FullName),
            _ => q.OrderBy(u => u.FullName),
        };
        var total = await q.CountAsync();
        var rows = await q.Skip((lq.Page - 1) * lq.PageSize).Take(lq.PageSize).ToListAsync();
        var ids = rows.Select(r => r.Id).ToList();
        var roleMap = await (from ur in _db.UserRoles join r in _db.Roles on ur.RoleId equals r.Id where ids.Contains(ur.UserId) select new { ur.UserId, r.Name }).ToListAsync();
        return new PagedResult<UserDto>
        {
            Items = rows.Select(u => Map(u, roleMap.FirstOrDefault(r => r.UserId == u.Id)?.Name ?? "")).ToList(),
            Page = lq.Page, PageSize = lq.PageSize, TotalCount = total
        };
    }

    public async Task<ServiceResult<UserDto>> GetAsync(string id)
    {
        var u = await (Visible()).FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return ServiceResult<UserDto>.NotFound();
        var role = (await _db.UserRoles.Where(ur => ur.UserId == id).Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name).FirstOrDefaultAsync()) ?? "";
        return ServiceResult<UserDto>.Ok(Map(u, role));
    }

    public async Task<ServiceResult<UserDto>> CreateAsync(UserCreateInput input)
    {
        if (!_me.IsAdmin) return ServiceResult<UserDto>.Forbidden();
        var errors = InputValidator.Validate(input);
        await ValidateRoleAndManagerAsync(input, errors, null);
        if (errors.Count > 0) return ServiceResult<UserDto>.Invalid(errors);

        var email = ValidationRules.NormalizeEmail(input.Email!);
        var user = new ApplicationUser
        {
            UserName = email, Email = email, EmailConfirmed = true, FullName = input.FullName!.Trim(), IsActive = input.IsActive,
            ManagerId = input.Role == RoleNames.SalesExecutive ? ValidationRules.Clean(input.ManagerId) : null, CreatedDate = DateTime.UtcNow,
        };
        var create = await _users.CreateAsync(user, input.Password!);
        if (!create.Succeeded) return ServiceResult<UserDto>.Invalid(IdentityErrors(create));
        await _users.AddToRoleAsync(user, input.Role!);
        await _auth.RecordPasswordAsync(user);
        await _audit.LogAsync("Users", "UserCreated", nameof(ApplicationUser), user.Id, $"User {user.Email} created with role {input.Role}", newValue: $"{{\"Role\":\"{input.Role}\",\"IsActive\":{input.IsActive.ToString().ToLower()}}}");
        return ServiceResult<UserDto>.Created(Map(user, input.Role!));
    }

    public async Task<ServiceResult<UserDto>> UpdateAsync(string id, UserEditInput input)
    {
        if (!_me.IsAdmin) return ServiceResult<UserDto>.Forbidden();
        var user = await _users.FindByIdAsync(id);
        if (user is null) return ServiceResult<UserDto>.NotFound();
        var errors = InputValidator.Validate(input);
        await ValidateRoleAndManagerAsync(input, errors, id);
        var oldRole = (await _users.GetRolesAsync(user)).FirstOrDefault() ?? "";
        if (id == _me.UserId && (input.Role != oldRole || !input.IsActive))
            errors["Role"] = "You cannot change your own role or deactivate your own account.";
        else if ((oldRole == RoleNames.Admin && user.IsActive) && (input.Role != RoleNames.Admin || !input.IsActive) && await ActiveAdminCountAsync() <= 1)
            errors["Role"] = "At least one active administrator is required.";
        if (errors.Count > 0) return ServiceResult<UserDto>.Invalid(errors);

        var email = ValidationRules.NormalizeEmail(input.Email!);
        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            var other = await _users.FindByEmailAsync(email);
            if (other is not null && other.Id != id) return ServiceResult<UserDto>.Conflict("An account with this email already exists.", "Email");
            user.Email = email; user.UserName = email;
        }
        var wasActive = user.IsActive;
        user.FullName = input.FullName!.Trim();
        user.IsActive = input.IsActive;
        user.ManagerId = input.Role == RoleNames.SalesExecutive ? ValidationRules.Clean(input.ManagerId) : null;
        var upd = await _users.UpdateAsync(user);
        if (!upd.Succeeded) return ServiceResult<UserDto>.Invalid(IdentityErrors(upd));

        var invalidate = false;
        if (oldRole != input.Role)
        {
            if (oldRole != "") await _users.RemoveFromRoleAsync(user, oldRole);
            await _users.AddToRoleAsync(user, input.Role!);
            await _audit.LogAsync("Users", "RoleChange", nameof(ApplicationUser), user.Id, $"Role changed for {user.Email}", oldValue: oldRole, newValue: input.Role);
            invalidate = true;
        }
        if (wasActive != input.IsActive)
        {
            await _audit.LogAsync("Users", input.IsActive ? "UserActivated" : "UserDeactivated", nameof(ApplicationUser), user.Id, $"{user.Email} {(input.IsActive ? "activated" : "deactivated")}");
            invalidate = true;
        }
        if (invalidate) await _users.UpdateSecurityStampAsync(user); // signs the user out everywhere
        await _audit.LogAsync("Users", "UserUpdated", nameof(ApplicationUser), user.Id, $"Profile updated for {user.Email}");
        return ServiceResult<UserDto>.Ok(Map(user, input.Role!));
    }

    public async Task<ServiceResult> SetActiveAsync(string id, bool active)
    {
        var r = await GetAsync(id);
        if (!r.Succeeded) return ServiceResult.NotFound();
        var v = r.Value!;
        return (await UpdateAsync(id, new UserEditInput { FullName = v.FullName, Email = v.Email, Role = v.Role, ManagerId = v.ManagerId, IsActive = active })) is { Succeeded: true }
            ? ServiceResult.Ok()
            : ServiceResult.Conflict(active ? "Could not activate the user." : "You cannot deactivate this account (own account or last administrator).");
    }

    public async Task<ServiceResult> UnlockAsync(string id)
    {
        if (!_me.IsAdmin) return ServiceResult.Forbidden();
        var user = await _users.FindByIdAsync(id);
        if (user is null) return ServiceResult.NotFound();
        await _users.SetLockoutEndDateAsync(user, null);
        await _users.ResetAccessFailedCountAsync(user);
        await _audit.LogAsync("Authentication", "AccountUnlocked", nameof(ApplicationUser), user.Id, $"Account {user.Email} unlocked by administrator");
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult<string>> CreateResetTokenAsync(string id)
    {
        if (!_me.IsAdmin) return ServiceResult<string>.Forbidden();
        var user = await _users.FindByIdAsync(id);
        if (user is null) return ServiceResult<string>.NotFound();
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        await _audit.LogAsync("Security", "PasswordResetInitiated", nameof(ApplicationUser), user.Id, $"Password reset link issued for {user.Email}"); // token is never logged
        return ServiceResult<string>.Ok(token);
    }

    public async Task<IReadOnlyList<UserOption>> ManagersAsync()
    {
        var managers = await _users.GetUsersInRoleAsync(RoleNames.Manager);
        return managers.Where(m => m.IsActive).OrderBy(m => m.FullName).Select(m => new UserOption(m.Id, m.FullName, RoleNames.Manager)).ToList();
    }

    public async Task<IReadOnlyList<(string Role, string Description, int Users)>> RolesAsync()
    {
        var roles = await _roles.Roles.OrderBy(r => r.Name).ToListAsync();
        var counts = await _db.UserRoles.GroupBy(ur => ur.RoleId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        return roles.Select(r => (r.Name!, r.Description, counts.FirstOrDefault(c => c.Key == r.Id)?.N ?? 0)).ToList();
    }

    private async Task ValidateRoleAndManagerAsync(UserEditInput input, Dictionary<string, string> errors, string? selfId)
    {
        if (!string.IsNullOrWhiteSpace(input.Role) && !RoleNames.All.Contains(input.Role)) errors["Role"] = "Select a valid role.";
        if (!string.IsNullOrWhiteSpace(input.ManagerId))
        {
            if (input.Role != RoleNames.SalesExecutive) errors["ManagerId"] = "Only Sales Executives report to a manager.";
            else
            {
                var m = await _users.FindByIdAsync(input.ManagerId);
                if (m is null || !m.IsActive || !await _users.IsInRoleAsync(m, RoleNames.Manager)) errors["ManagerId"] = "Select a valid active manager.";
            }
        }
    }

    private Task<int> ActiveAdminCountAsync() => (from ur in _db.UserRoles join r in _db.Roles on ur.RoleId equals r.Id
                                                  join u in _db.Users on ur.UserId equals u.Id
                                                  where r.Name == RoleNames.Admin && u.IsActive select u.Id).CountAsync();

    private static Dictionary<string, string> IdentityErrors(IdentityResult r)
    {
        var d = new Dictionary<string, string>();
        foreach (var e in r.Errors)
        {
            var key = e.Code.Contains("Password") ? "Password" : e.Code.Contains("Email") || e.Code.Contains("UserName") ? "Email" : "";
            var msg = e.Code is "DuplicateUserName" or "DuplicateEmail" ? "An account with this email already exists." : e.Description;
            d[key] = d.TryGetValue(key, out var prev) ? prev + " " + msg : msg;
        }
        return d;
    }

    private static UserDto Map(ApplicationUser u, string role) => new(u.Id, u.FullName, u.Email ?? "", role, u.IsActive,
        u.LockoutEnd is { } e && e > DateTimeOffset.UtcNow, u.LockoutEnd, u.AccessFailedCount, u.ManagerId, u.Manager?.FullName, u.CreatedDate, u.LastLoginDate);
}
