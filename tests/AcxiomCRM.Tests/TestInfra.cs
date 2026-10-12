using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcxiomCRM.Application.Common;
using AcxiomCRM.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

public static class Creds
{
    public const string Admin = "admin@acxiom.local", AdminPw = "Welcome#2026x";
    public const string Manager = "manager@acxiom.local", Arjun = "arjun@acxiom.local", Priya = "priya@acxiom.local", Rahul = "rahul@acxiom.local";
    public const string DemoPw = "Crm#Demo2026";
}

/// <summary>Boots the real application on an isolated, freshly migrated and seeded SQLite database.</summary>
public class AppFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "acxiom-tests", Guid.NewGuid().ToString("N"));
    public AppFactory() { Directory.CreateDirectory(_dir); }
    protected virtual int AuthLimit => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Testing");
        b.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_dir, "test.db")}");
        b.UseSetting("DataProtection:KeyPath", Path.Combine(_dir, "keys"));
        b.UseSetting("Jwt:Key", "unit-test-signing-key-unit-test-signing-key-0123456789");
        b.UseSetting("Seed:AdminEmail", Creds.Admin);
        b.UseSetting("Seed:AdminPassword", Creds.AdminPw);
        b.UseSetting("Seed:DemoData", "true");
        b.UseSetting("Seed:DemoPassword", Creds.DemoPw);
        b.UseSetting("Security:AuthRateLimitPerMinute", AuthLimit.ToString());
        b.UseSetting("Logging:LogLevel:Default", "Warning");
    }

    public WebClient NewWeb() => new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
    public ApiClient NewApi() => new(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false }));

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, IClock, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IClock>());
    }

    public DateOnly Today { get { using var s = Services.CreateScope(); return s.ServiceProvider.GetRequiredService<IClock>().Today; } }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); } catch { /* temp files */ }
    }
}

/// <summary>Cookie-based browser-like client that handles anti-forgery tokens.</summary>
public sealed class WebClient
{
    public HttpClient Http { get; }
    public WebClient(HttpClient http) => Http = http;

    private static readonly Regex Token = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    public async Task<string> TokenAsync(string pageUrl)
    {
        var html = await (await Http.GetAsync(pageUrl)).Content.ReadAsStringAsync();
        var m = Token.Match(html);
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException($"No anti-forgery token on {pageUrl}");
    }

    public async Task<HttpResponseMessage> PostFormAsync(string url, IDictionary<string, string?> fields, string? tokenPage = null, bool withToken = true)
    {
        var data = fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value!);
        if (withToken) data["__RequestVerificationToken"] = await TokenAsync(tokenPage ?? url);
        return await Http.PostAsync(url, new FormUrlEncodedContent(data));
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        var r = await PostFormAsync("/Account/Login", new Dictionary<string, string?> { ["UserName"] = email, ["Password"] = password });
        return r;
    }

    public async Task<WebClient> SignedInAsync(string email, string password = Creds.DemoPw)
    {
        var r = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return this;
    }

    public async Task<string> GetStringAsync(string url) => await Http.GetStringAsync(url);
}

public sealed class ApiClient
{
    public HttpClient Http { get; }
    public ApiClient(HttpClient http) => Http = http;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> LoginAsync(string email, string password = Creds.DemoPw)
    {
        var r = await Http.PostAsJsonAsync("/api/auth/login", new { userName = email, password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var token = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }

    public async Task<ApiClient> AsAsync(string email, string password = Creds.DemoPw) { await LoginAsync(email, password); return this; }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod m, string url, object? body = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        var r = await Http.SendAsync(req);
        var text = await r.Content.ReadAsStringAsync();
        JsonElement el = default;
        if (!string.IsNullOrWhiteSpace(text)) { try { el = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { } }
        return (r.StatusCode, el);
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url) => SendAsync(HttpMethod.Get, url);
    public Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string url, object? body) => SendAsync(HttpMethod.Post, url, body);
    public Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(string url, object? body) => SendAsync(HttpMethod.Put, url, body);
    public Task<(HttpStatusCode Status, JsonElement Body)> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url);

    public static string Errors(JsonElement body) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("errors", out var e) ? e.ToString() : body.ToString();
}

public static class Seq
{
    private static int _n = 100000;
    public static int Next() => Interlocked.Increment(ref _n);
    /// <summary>Unique valid Indian mobile number.</summary>
    public static string Phone() => "9" + Next().ToString("D9");
    public static object Customer(string? name = null, string? assignedTo = null) => new
    {
        customerName = name ?? $"Test Customer {Next()}", email = $"cust{Next()}@example.com", phone = Phone(),
        companyName = $"Co {Next()}", status = "Active", assignedTo,
    };
}

public sealed class ThrottledAppFactory : AppFactory { protected override int AuthLimit => 3; }
