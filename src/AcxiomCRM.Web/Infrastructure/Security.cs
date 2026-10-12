using System.Security.Claims;
using System.Text.Json;
using AcxiomCRM.Application.Security;
using AcxiomCRM.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.Infrastructure;

public static class PolicyNames
{
    public const string AdminOnly = "AdminOnly";
    public const string ManagerOrAdmin = "ManagerOrAdmin";
    public const string UsersView = "UsersView";
    public const string AuditView = "AuditView";
}

public static class PrincipalExtensions
{
    public static bool CanViewUsers(this ClaimsPrincipal u, SecurityOptions o) =>
        u.IsInRole(RoleNames.Admin) || (u.IsInRole(RoleNames.Manager) && o.ManagerCanViewUsers);
    public static bool CanViewAudit(this ClaimsPrincipal u, SecurityOptions o) =>
        u.IsInRole(RoleNames.Admin) || (u.IsInRole(RoleNames.Manager) && o.ManagerCanViewAudit);
}

/// <summary>Adds defensive HTTP headers and a strict CSP (no inline script/style, same-origin only).</summary>
public sealed class SecurityHeadersMiddleware
{
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
                               "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            if (!ctx.Request.Path.StartsWithSegments("/swagger")) h["Content-Security-Policy"] = Csp;
            // Authenticated pages and API responses must never be served from a shared/back-button cache.
            var path = ctx.Request.Path;
            if (!path.StartsWithSegments("/lib") && !path.StartsWithSegments("/css") && !path.StartsWithSegments("/js"))
            {
                h["Cache-Control"] = "no-store";
                h["Pragma"] = "no-cache";
            }
            return Task.CompletedTask;
        });
        return _next(ctx);
    }
}

public static class ProblemFactory
{
    /// <summary>RFC 7807 problem body. Never contains stack traces or database details.</summary>
    public static IResult Problem(HttpContext ctx, int status, string title, string? detail = null, IDictionary<string, string[]>? errors = null)
    {
        var body = errors is { Count: > 0 }
            ? new ValidationProblemDetails(errors) { Status = status, Title = title, Detail = detail }
            : new ProblemDetails { Status = status, Title = title, Detail = detail };
        body.Extensions["traceId"] = ctx.TraceIdentifier;
        return Results.Json(body, new JsonSerializerOptions(JsonSerializerDefaults.Web), "application/problem+json", status);
    }

    public static string DefaultTitle(int status) => status switch
    {
        400 => "The request is invalid.", 401 => "Authentication is required.", 403 => "You do not have permission to perform this action.",
        404 => "The requested resource was not found.", 409 => "The request conflicts with existing data.", 429 => "Too many requests. Please try again later.",
        _ => status >= 500 ? "An unexpected error occurred." : "The request could not be completed."
    };
}

public sealed class DbHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly AcxiomCRM.Data.AppDbContext _db;
    public DbHealthCheck(AcxiomCRM.Data.AppDbContext db) => _db = db;
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken ct = default)
    {
        try { return await _db.Database.CanConnectAsync(ct) ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy() : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(); }
        catch (Exception) { return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(); }
    }
}

/// <summary>Adds the display name to the auth cookie so the shell can show it without a database call.</summary>
public sealed class AppClaimsPrincipalFactory : Microsoft.AspNetCore.Identity.UserClaimsPrincipalFactory<AcxiomCRM.Domain.Entities.ApplicationUser, AcxiomCRM.Domain.Entities.ApplicationRole>
{
    public AppClaimsPrincipalFactory(Microsoft.AspNetCore.Identity.UserManager<AcxiomCRM.Domain.Entities.ApplicationUser> um,
        Microsoft.AspNetCore.Identity.RoleManager<AcxiomCRM.Domain.Entities.ApplicationRole> rm,
        Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Identity.IdentityOptions> o) : base(um, rm, o) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AcxiomCRM.Domain.Entities.ApplicationUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        id.AddClaim(new Claim("FullName", user.FullName));
        return id;
    }
}
