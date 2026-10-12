using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Security;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using AcxiomCRM.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var env = builder.Environment;

// ------------------------------------------------------------------ options & secrets
builder.Services.Configure<SecurityOptions>(cfg.GetSection(SecurityOptions.Section));
var securityOpts = cfg.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
var jwtOpts = cfg.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
var appData = Path.Combine(env.ContentRootPath, "App_Data");
Directory.CreateDirectory(appData);
if (string.IsNullOrWhiteSpace(jwtOpts.Key))
{
    if (!env.IsDevelopment()) throw new InvalidOperationException("Jwt:Key must be configured (environment variable Jwt__Key) outside Development.");
    // Development convenience only: a random per-machine key kept out of source control.
    var keyFile = Path.Combine(appData, "jwt-dev.key");
    if (!File.Exists(keyFile)) File.WriteAllText(keyFile, JwtTokenService.GenerateKey());
    jwtOpts.Key = File.ReadAllText(keyFile).Trim();
}
if (jwtOpts.Key.Length < 32) throw new InvalidOperationException("Jwt:Key must be at least 32 characters.");
builder.Services.Configure<JwtOptions>(o => { o.Issuer = jwtOpts.Issuer; o.Audience = jwtOpts.Audience; o.ExpiryMinutes = jwtOpts.ExpiryMinutes; o.Key = jwtOpts.Key; });

if (cfg.GetValue<bool>("Seed:DemoData") && !env.IsDevelopment() && !env.IsEnvironment("Testing"))
    throw new InvalidOperationException("Seed:DemoData is only allowed in Development.");
builder.WebHost.ConfigureKestrel(o => { o.AddServerHeader = false; o.Limits.MaxRequestBodySize = 1_048_576; });

// ------------------------------------------------------------------ data
var connection = new SqliteConnectionStringBuilder(cfg.GetConnectionString("Default") ?? "Data Source=App_Data/acxiomcrm.db");
if (!Path.IsPathRooted(connection.DataSource)) connection.DataSource = Path.GetFullPath(Path.Combine(env.ContentRootPath, connection.DataSource));
Directory.CreateDirectory(Path.GetDirectoryName(connection.DataSource)!);
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection.ToString()));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

builder.Services.AddDataProtection()
    .SetApplicationName("AcxiomCRM")
    .PersistKeysToFileSystem(new DirectoryInfo(cfg["DataProtection:KeyPath"] ?? Path.Combine(appData, "keys")));

// ------------------------------------------------------------------ identity
builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(o =>
{
    var p = cfg.GetSection("Security:Password");
    o.Password.RequiredLength = p.GetValue("RequiredLength", 8);
    o.Password.RequireDigit = p.GetValue("RequireDigit", true);
    o.Password.RequireLowercase = p.GetValue("RequireLowercase", true);
    o.Password.RequireUppercase = p.GetValue("RequireUppercase", true);
    o.Password.RequireNonAlphanumeric = p.GetValue("RequireNonAlphanumeric", true);
    o.Password.RequiredUniqueChars = p.GetValue("RequiredUniqueChars", 4);
    o.Lockout.AllowedForNewUsers = true;
    o.Lockout.MaxFailedAccessAttempts = cfg.GetValue("Security:Lockout:MaxFailedAttempts", 5);
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(cfg.GetValue("Security:Lockout:Minutes", 15));
    o.User.RequireUniqueEmail = true;
    o.SignIn.RequireConfirmedAccount = false;
    o.Tokens.PasswordResetTokenProvider = TokenOptions.DefaultProvider;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders()
.AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
.AddPasswordValidator<PasswordReuseValidator>();

builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(securityOpts.ResetTokenHours));
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero); // deactivation/role change takes effect immediately

var devLike = env.IsDevelopment() || env.IsEnvironment("Testing");
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "AcxiomCRM.Antiforgery";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = devLike ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "AcxiomCRM.Auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = devLike ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(cfg.GetValue("Security:SessionMinutes", 30));
    o.SlidingExpiration = true;
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AccessDenied";
});

builder.Services.AddAuthentication()
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwtOpts.Issuer,
            ValidateAudience = true, ValidAudience = jwtOpts.Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(jwtOpts.Key)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name", RoleClaimType = "role",
        };
        o.Events = new JwtBearerEvents
        {
            // A valid signature is not enough: the account must still be active, unlocked, on the same security stamp and not logged out.
            OnTokenValidated = async ctx =>
            {
                var sp = ctx.HttpContext.RequestServices;
                var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(ctx.Principal?.FindFirst("sub")?.Value ?? "");
                var jti = ctx.Principal?.FindFirst("jti")?.Value ?? "";
                if (user is null || !user.IsActive || await users.IsLockedOutAsync(user)
                    || user.SecurityStamp != ctx.Principal?.FindFirst("stamp")?.Value
                    || await sp.GetRequiredService<IJwtTokenService>().IsRevokedAsync(jti))
                    ctx.Fail("The token is no longer valid.");
            },
        };
    });

// ------------------------------------------------------------------ authorization
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(); // anonymous access must be explicit
    o.AddPolicy(PolicyNames.AdminOnly, p => p.RequireRole(RoleNames.Admin));
    o.AddPolicy(PolicyNames.ManagerOrAdmin, p => p.RequireRole(RoleNames.Admin, RoleNames.Manager));
    o.AddPolicy(PolicyNames.UsersView, p => p.RequireAssertion(c => c.User.CanViewUsers(securityOpts)));
    o.AddPolicy(PolicyNames.AuditView, p => p.RequireAssertion(c => c.User.CanViewAudit(securityOpts)));
});

// ------------------------------------------------------------------ MVC / API
builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()); // every state-changing MVC request needs an anti-forgery token
    var m = o.ModelBindingMessageProvider;
    m.SetValueMustBeANumberAccessor(f => $"{f} must be a valid number.");
    m.SetAttemptedValueIsInvalidAccessor((v, f) => $"The value is not valid for {f}.");
    m.SetValueIsInvalidAccessor(f => $"The value is not valid for {f}.");
    m.SetMissingBindRequiredValueAccessor(f => $"{f} is required.");
    m.SetMissingKeyOrValueAccessor(() => "A value is required.");
    m.SetNonPropertyValueMustBeANumberAccessor(() => "A valid number is required.");
    m.SetUnknownValueIsInvalidAccessor(f => $"The value is not valid for {f}.");
})
.AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(null, allowIntegerValues: false)))
.ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = ctx =>
{
    var errors = ctx.ModelState.Where(e => e.Value?.Errors.Count > 0)
        .ToDictionary(e => e.Key, e => e.Value!.Errors.Select(x => string.IsNullOrWhiteSpace(x.ErrorMessage) ? "The value is invalid." : x.ErrorMessage).ToArray());
    var pd = new ValidationProblemDetails(errors) { Status = 400, Title = "One or more validation errors occurred." };
    pd.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    return new BadRequestObjectResult(pd) { ContentTypes = { "application/problem+json" } };
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.GetValue("Security:AuthRateLimitPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        if (ctx.HttpContext.Request.Path.StartsWithSegments("/api"))
            await ProblemFactory.Problem(ctx.HttpContext, 429, ProblemFactory.DefaultTitle(429)).ExecuteAsync(ctx.HttpContext);
        else
        {
            ctx.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.HttpContext.Response.WriteAsync("Too many attempts. Please wait a minute and try again.", ct);
        }
    };
});

builder.Services.AddScoped<IAccessScope, AccessScopeService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IAuditQueryService, AuditQueryService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<ILeadService, LeadService>();
builder.Services.AddScoped<IOpportunityService, OpportunityService>();
builder.Services.AddScoped<IFollowUpService, FollowUpService>();
builder.Services.AddScoped<IActivityService, ActivityService>();
builder.Services.AddScoped<IUserAdminService, UserAdminService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

var swaggerEnabled = cfg.GetValue("Swagger:Enabled", env.IsDevelopment());
if (swaggerEnabled)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new OpenApiInfo { Title = "AcxiomCRM API", Version = "v1", Description = "Secured REST API for customers, leads, opportunities, follow-ups and reports. Obtain a token from POST /api/auth/login and send it as 'Authorization: Bearer <token>'." });
        o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header, Description = "JWT from /api/auth/login" });
        o.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>() });
        var xml = Path.Combine(AppContext.BaseDirectory, "AcxiomCRM.Web.xml");
        if (File.Exists(xml)) o.IncludeXmlComments(xml);
    });
}

if (cfg.GetValue<bool>("Security:UseForwardedHeaders"))
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear(); o.KnownProxies.Clear(); // trust is limited by network policy in front of the app; see README
    });

if (int.TryParse(cfg["Security:HttpsPort"], out var httpsPort)) builder.Services.AddHttpsRedirection(o => o.HttpsPort = httpsPort);
builder.Services.AddHealthChecks().AddCheck<DbHealthCheck>("database");

// ------------------------------------------------------------------ pipeline
var app = builder.Build();

if (cfg.GetValue<bool>("Security:UseForwardedHeaders")) app.UseForwardedHeaders();
app.UseExceptionHandler("/Home/Error");
if (cfg.GetValue<bool>("Security:RequireHttps"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
    // If a redirect was not possible (no HTTPS port known) refuse the request rather than serve secrets over plain HTTP.
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.IsHttps && !ctx.Request.Path.StartsWithSegments("/health"))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("HTTPS is required.");
            return;
        }
        await next();
    });
}
app.UseMiddleware<SecurityHeadersMiddleware>();

// Friendly error pages for the UI, consistent problem+json for the API.
app.UseWhen(c => c.Request.Path.StartsWithSegments("/api"), b => b.UseStatusCodePages(async ctx =>
{
    var status = ctx.HttpContext.Response.StatusCode;
    if (ctx.HttpContext.Response.HasStarted || ctx.HttpContext.Response.ContentLength > 0) return;
    await ProblemFactory.Problem(ctx.HttpContext, status, ProblemFactory.DefaultTitle(status)).ExecuteAsync(ctx.HttpContext);
}));
app.UseWhen(c => !c.Request.Path.StartsWithSegments("/api"), b => b.UseStatusCodePagesWithReExecute("/Home/StatusCode", "?code={0}"));

if (swaggerEnabled) { app.UseSwagger(); app.UseSwaggerUI(o => o.DocumentTitle = "AcxiomCRM API"); }

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
// Unknown API paths get a JSON 404 instead of being treated as a page that needs a login redirect.
app.MapFallback("/api/{**path}", (HttpContext ctx) => ProblemFactory.Problem(ctx, 404, ProblemFactory.DefaultTitle(404))).AllowAnonymous();
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");

await using (var scope = app.Services.CreateAsyncScope())
    await DbInitializer.InitializeAsync(scope.ServiceProvider, cfg, scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"));

app.Run();

public partial class Program { }
