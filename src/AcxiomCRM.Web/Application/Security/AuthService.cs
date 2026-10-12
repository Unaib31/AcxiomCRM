using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AcxiomCRM.Application.Security;

public class SecurityOptions
{
    public const string Section = "Security";
    public int PasswordHistoryCount { get; set; } = 5;
    public bool AllowSelfRegistration { get; set; } = true;
    public bool ManagerCanViewUsers { get; set; } = true;
    public bool ManagerCanViewAudit { get; set; } = true;
    public int ResetTokenHours { get; set; } = 2;
}

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "AcxiomCRM";
    public string Audience { get; set; } = "AcxiomCRM.Api";
    public int ExpiryMinutes { get; set; } = 60;
    public string Key { get; set; } = string.Empty;
}

public enum AuthStatus { Success, InvalidCredentials, LockedOut, Inactive }
public record AuthResult(AuthStatus Status, ApplicationUser? User = null, string? Role = null, DateTimeOffset? LockoutEnd = null);

public class RegisterInput
{
    [Required(ErrorMessage = "Full name is required."), StringLength(150, ErrorMessage = "Full name cannot exceed 150 characters."), Display(Name = "Full Name")]
    public string? FullName { get; set; }

    [Required(ErrorMessage = "Email is required."), StringLength(254, ErrorMessage = "Email cannot exceed 254 characters."),
     RegularExpression(Domain.Rules.ValidationRules.EmailPattern, ErrorMessage = Domain.Rules.ValidationRules.EmailMessage)]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Password is required."), StringLength(128, ErrorMessage = "Password cannot exceed 128 characters."), DataType(DataType.Password)]
    public string? Password { get; set; }

    [Required(ErrorMessage = "Please confirm the password."), DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match."), Display(Name = "Confirm Password")]
    public string? ConfirmPassword { get; set; }
}

public interface IAuthService
{
    Task<AuthResult> AuthenticateAsync(string userName, string password, string channel);
    Task<IdentityResult> RegisterAsync(RegisterInput input);
    Task RecordPasswordAsync(ApplicationUser user);
}

public sealed class AuthService : IAuthService
{
    private static readonly ApplicationUser Dummy = new() { UserName = "dummy" };
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IAuditService _audit;
    private readonly IPasswordHasher<ApplicationUser> _hasher;
    private readonly AppDbContext _db;
    private readonly string _dummyHash;

    public AuthService(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IAuditService audit,
        IPasswordHasher<ApplicationUser> hasher, AppDbContext db)
    {
        _users = users; _signIn = signIn; _audit = audit; _hasher = hasher; _db = db;
        _dummyHash = hasher.HashPassword(Dummy, "Dummy-Password-1!");
    }

    public async Task<AuthResult> AuthenticateAsync(string userName, string password, string channel)
    {
        userName = userName.Trim();
        var user = await _users.FindByNameAsync(userName) ?? await _users.FindByEmailAsync(userName);
        if (user is null)
        {
            _hasher.VerifyHashedPassword(Dummy, _dummyHash, password); // keep timing similar to a real check
            await _audit.LogAsync("Authentication", "LoginFailed", details: $"Unknown account via {channel}", result: "Failure", userName: Truncate(userName));
            return new AuthResult(AuthStatus.InvalidCredentials);
        }

        var wasLocked = await _users.IsLockedOutAsync(user);
        var result = await _signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            if (!wasLocked)
                await _audit.LogAsync("Authentication", "AccountLockedOut", nameof(ApplicationUser), user.Id, $"Locked after repeated failed logins via {channel}", "Failure", user.Id, user.UserName);
            else
                await _audit.LogAsync("Authentication", "LoginFailed", nameof(ApplicationUser), user.Id, $"Attempt while locked out via {channel}", "Failure", user.Id, user.UserName);
            return new AuthResult(AuthStatus.LockedOut, LockoutEnd: user.LockoutEnd);
        }
        if (!result.Succeeded)
        {
            await _audit.LogAsync("Authentication", "LoginFailed", nameof(ApplicationUser), user.Id, $"Invalid password via {channel}", "Failure", user.Id, user.UserName);
            return new AuthResult(AuthStatus.InvalidCredentials);
        }
        if (!user.IsActive)
        {
            await _audit.LogAsync("Authentication", "LoginFailed", nameof(ApplicationUser), user.Id, $"Disabled account via {channel}", "Failure", user.Id, user.UserName);
            return new AuthResult(AuthStatus.Inactive);
        }

        user.LastLoginDate = DateTime.UtcNow;
        await _users.UpdateAsync(user);
        var role = (await _users.GetRolesAsync(user)).FirstOrDefault() ?? RoleNames.SalesExecutive;
        await _audit.LogAsync("Authentication", "Login", nameof(ApplicationUser), user.Id, $"Signed in via {channel}", "Success", user.Id, user.UserName);
        return new AuthResult(AuthStatus.Success, user, role);
    }

    /// <summary>Self-registration always yields the least-privileged role; a role can never be requested by the caller.</summary>
    public async Task<IdentityResult> RegisterAsync(RegisterInput input)
    {
        var email = Domain.Rules.ValidationRules.NormalizeEmail(input.Email!);
        var user = new ApplicationUser { UserName = email, Email = email, FullName = input.FullName!.Trim(), IsActive = true, CreatedDate = DateTime.UtcNow };
        var result = await _users.CreateAsync(user, input.Password!);
        if (!result.Succeeded) return result;
        await _users.AddToRoleAsync(user, RoleNames.SalesExecutive);
        await RecordPasswordAsync(user);
        await _audit.LogAsync("Authentication", "Register", nameof(ApplicationUser), user.Id, "Self-registered as Sales Executive", "Success", user.Id, user.UserName);
        return result;
    }

    public async Task RecordPasswordAsync(ApplicationUser user)
    {
        if (string.IsNullOrEmpty(user.PasswordHash)) return;
        _db.PasswordHistories.Add(new PasswordHistory { UserId = user.Id, PasswordHash = user.PasswordHash, CreatedDate = DateTime.UtcNow });
        await _db.SaveChangesAsync();
    }

    private static string Truncate(string s) => s.Length <= 100 ? s : s[..100];
}

/// <summary>Rejects reuse of the last N passwords. Only hashes are compared.</summary>
public sealed class PasswordReuseValidator : IPasswordValidator<ApplicationUser>
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<ApplicationUser> _hasher;
    private readonly SecurityOptions _opt;

    public PasswordReuseValidator(AppDbContext db, IPasswordHasher<ApplicationUser> hasher, IOptions<SecurityOptions> opt)
    { _db = db; _hasher = hasher; _opt = opt.Value; }

    public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(user.Id) || _opt.PasswordHistoryCount <= 0) return IdentityResult.Success;
        var recent = await _db.PasswordHistories.Where(h => h.UserId == user.Id)
            .OrderByDescending(h => h.CreatedDate).ThenByDescending(h => h.PasswordHistoryId)
            .Take(_opt.PasswordHistoryCount).Select(h => h.PasswordHash).ToListAsync();
        foreach (var hash in recent)
            if (_hasher.VerifyHashedPassword(user, hash, password) != PasswordVerificationResult.Failed)
                return IdentityResult.Failed(new IdentityError { Code = "PasswordReuse", Description = $"You cannot reuse any of your last {_opt.PasswordHistoryCount} passwords." });
        if (!string.IsNullOrEmpty(user.Email) && password.Contains(user.Email.Split('@')[0], StringComparison.OrdinalIgnoreCase) && user.Email.Split('@')[0].Length >= 4)
            return IdentityResult.Failed(new IdentityError { Code = "PasswordContainsName", Description = "Password must not contain your email name." });
        return IdentityResult.Success;
    }
}

public interface IJwtTokenService
{
    (string Token, DateTime ExpiresUtc) Issue(ApplicationUser user, string role);
    Task RevokeAsync(string tokenId, DateTime expiresUtc);
    Task<bool> IsRevokedAsync(string tokenId);
}

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _opt;
    private readonly AppDbContext _db;
    public JwtTokenService(IOptions<JwtOptions> opt, AppDbContext db) { _opt = opt.Value; _db = db; }

    public (string Token, DateTime ExpiresUtc) Issue(ApplicationUser user, string role)
    {
        var expires = DateTime.UtcNow.AddMinutes(_opt.ExpiryMinutes);
        var claims = new List<Claim>
        {
            new("sub", user.Id),
            new("name", user.UserName ?? user.Email ?? ""),
            new("role", role),
            new("stamp", user.SecurityStamp ?? ""),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(_opt.Key)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(_opt.Issuer, _opt.Audience, claims, DateTime.UtcNow.AddSeconds(-5), expires, creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }

    public async Task RevokeAsync(string tokenId, DateTime expiresUtc)
    {
        if (await _db.RevokedTokens.AnyAsync(t => t.TokenId == tokenId)) return;
        _db.RevokedTokens.Add(new RevokedToken { TokenId = tokenId, ExpiresUtc = expiresUtc });
        // opportunistic cleanup of tokens that have expired anyway
        var old = await _db.RevokedTokens.Where(t => t.ExpiresUtc < DateTime.UtcNow).ToListAsync();
        _db.RevokedTokens.RemoveRange(old);
        await _db.SaveChangesAsync();
    }

    public Task<bool> IsRevokedAsync(string tokenId) => _db.RevokedTokens.AnyAsync(t => t.TokenId == tokenId);

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}
