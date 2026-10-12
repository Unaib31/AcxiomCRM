using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Tests;

public class AuthTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AuthTests(AppFactory f) => _f = f;

    [Theory]
    [InlineData("/")] [InlineData("/Customers")] [InlineData("/Leads/Details/1")] [InlineData("/Opportunities")] [InlineData("/FollowUps")]
    [InlineData("/Users")] [InlineData("/Audit")] [InlineData("/Reports")] [InlineData("/Dashboard")]
    public async Task Anonymous_users_are_sent_to_login(string url)
    {
        var r = await _f.NewWeb().Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Anonymous_api_calls_get_401_problem_json()
    {
        var r = await _f.NewApi().Http.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Valid_login_reaches_the_dashboard()
    {
        var web = _f.NewWeb();
        var r = await web.LoginAsync(Creds.Arjun, Creds.DemoPw);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location!.OriginalString);
        var page = await web.GetStringAsync("/");
        Assert.Contains("Dashboard", page);
        Assert.Contains("Arjun Mehta", page);
    }

    [Fact]
    public async Task Login_ignores_external_return_urls()
    {
        var web = _f.NewWeb();
        var r = await web.PostFormAsync("/Account/Login", new Dictionary<string, string?> { ["UserName"] = Creds.Priya, ["Password"] = Creds.DemoPw, ["ReturnUrl"] = "https://evil.example/phish" }, "/Account/Login");
        Assert.Equal("/", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Invalid_credentials_are_rejected_with_a_generic_message_and_audited()
    {
        var web = _f.NewWeb();
        var r = await web.LoginAsync(Creds.Arjun, "Wrong#Password1");
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Invalid email or password.", body);
        Assert.DoesNotContain("Wrong#Password1", body);
        Assert.Equal(HttpStatusCode.Redirect, (await web.Http.GetAsync("/Customers")).StatusCode);

        var unknown = await _f.NewWeb().LoginAsync("nobody@example.com", "Whatever#123");
        Assert.Contains("Invalid email or password.", await unknown.Content.ReadAsStringAsync());

        var failures = await _f.WithDbAsync((db, _) => db.AuditLogs.CountAsync(a => a.Action == "LoginFailed" && a.Result == "Failure"));
        Assert.True(failures >= 2);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_and_admin_can_unlock()
    {
        var email = $"lock{Seq.Next()}@example.com";
        var reg = _f.NewWeb();
        Assert.Equal(HttpStatusCode.Redirect, (await reg.PostFormAsync("/Account/Register", new Dictionary<string, string?>
            { ["FullName"] = "Lock Test", ["Email"] = email, ["Password"] = "Str0ng#Passw0rd", ["ConfirmPassword"] = "Str0ng#Passw0rd" })).StatusCode);

        var web = _f.NewWeb();
        for (var i = 0; i < 5; i++) await web.LoginAsync(email, "Bad#Password" + i);
        var locked = await web.LoginAsync(email, "Str0ng#Passw0rd"); // even the right password is refused now
        var html = await locked.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Contains("locked", html, StringComparison.OrdinalIgnoreCase);

        Assert.True(await _f.WithDbAsync((db, _) => db.AuditLogs.AnyAsync(a => a.Action == "AccountLockedOut")));

        var admin = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var id = await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync());
        var unlock = await admin.PostFormAsync($"/Users/Unlock/{id}", new Dictionary<string, string?>(), $"/Users/Details/{id}");
        Assert.Equal(HttpStatusCode.Redirect, unlock.StatusCode);
        Assert.True(await _f.WithDbAsync((db, _) => db.AuditLogs.AnyAsync(a => a.Action == "AccountUnlocked")));
        Assert.Equal(HttpStatusCode.Redirect, (await _f.NewWeb().LoginAsync(email, "Str0ng#Passw0rd")).StatusCode);
    }

    [Fact]
    public async Task Registration_always_creates_a_sales_executive_even_if_a_role_is_posted()
    {
        var email = $"reg{Seq.Next()}@example.com";
        var web = _f.NewWeb();
        var r = await web.PostFormAsync("/Account/Register", new Dictionary<string, string?>
            { ["FullName"] = "New Person", ["Email"] = email, ["Password"] = "Str0ng#Passw0rd", ["ConfirmPassword"] = "Str0ng#Passw0rd", ["Role"] = "Admin", ["IsActive"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/")).StatusCode); // signed in
        var role = await _f.WithDbAsync(async (db, _) => await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join ro in db.Roles on ur.RoleId equals ro.Id where u.Email == email select ro.Name).SingleAsync());
        Assert.Equal("SalesExecutive", role);
        var users = await web.Http.GetAsync("/Users");
        Assert.Equal(HttpStatusCode.Redirect, users.StatusCode);
        Assert.Contains("AccessDenied", users.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("alllowercase1!", "no uppercase")]
    [InlineData("ALLUPPERCASE1!", "no lowercase")]
    [InlineData("NoDigitsHere!!", "no digit")]
    [InlineData("NoSymbol12345A", "no symbol")]
    [InlineData("Ab1!", "too short")]
    public async Task Weak_passwords_are_rejected_at_registration(string password, string _)
    {
        var email = $"weak{Seq.Next()}@example.com";
        var r = await _f.NewWeb().PostFormAsync("/Account/Register", new Dictionary<string, string?>
            { ["FullName"] = "Weak Pw", ["Email"] = email, ["Password"] = password, ["ConfirmPassword"] = password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(await _f.WithDbAsync((db, _) => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task Duplicate_registration_is_rejected()
    {
        var r = await _f.NewWeb().PostFormAsync("/Account/Register", new Dictionary<string, string?>
            { ["FullName"] = "Dup", ["Email"] = Creds.Arjun, ["Password"] = "Str0ng#Passw0rd", ["ConfirmPassword"] = "Str0ng#Passw0rd" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("already exists", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Passwords_are_stored_hashed_never_in_plain_text()
    {
        var all = await _f.WithDbAsync((db, _) => db.Users.Select(u => u.PasswordHash).ToListAsync());
        Assert.All(all, h => { Assert.NotNull(h); Assert.DoesNotContain(Creds.DemoPw, h!); Assert.NotEqual(Creds.AdminPw, h); Assert.StartsWith("AQAAAA", h); });
    }

    [Fact]
    public async Task Change_password_enforces_policy_and_blocks_reuse()
    {
        var email = $"chg{Seq.Next()}@example.com";
        var web = _f.NewWeb();
        await web.PostFormAsync("/Account/Register", new Dictionary<string, string?> { ["FullName"] = "Change Me", ["Email"] = email, ["Password"] = "First#Passw0rd1", ["ConfirmPassword"] = "First#Passw0rd1" });

        var weak = await web.PostFormAsync("/Account/ChangePassword", new Dictionary<string, string?> { ["CurrentPassword"] = "First#Passw0rd1", ["NewPassword"] = "weak", ["ConfirmPassword"] = "weak" });
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);

        var reuse = await web.PostFormAsync("/Account/ChangePassword", new Dictionary<string, string?> { ["CurrentPassword"] = "First#Passw0rd1", ["NewPassword"] = "First#Passw0rd1", ["ConfirmPassword"] = "First#Passw0rd1" });
        Assert.Contains("cannot reuse", await reuse.Content.ReadAsStringAsync());

        var ok = await web.PostFormAsync("/Account/ChangePassword", new Dictionary<string, string?> { ["CurrentPassword"] = "First#Passw0rd1", ["NewPassword"] = "Second#Passw0rd2", ["ConfirmPassword"] = "Second#Passw0rd2" });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await _f.NewWeb().LoginAsync(email, "Second#Passw0rd2")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.NewWeb().LoginAsync(email, "First#Passw0rd1")).StatusCode); // old password no longer works (form re-rendered)

        var back = await web.PostFormAsync("/Account/ChangePassword", new Dictionary<string, string?> { ["CurrentPassword"] = "Second#Passw0rd2", ["NewPassword"] = "First#Passw0rd1", ["ConfirmPassword"] = "First#Passw0rd1" });
        Assert.Contains("cannot reuse", await back.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task State_changing_posts_without_an_antiforgery_token_are_rejected()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var r = await web.PostFormAsync("/Customers/Create", new Dictionary<string, string?> { ["CustomerName"] = "Forged", ["Email"] = "forged@example.com", ["Phone"] = Seq.Phone() }, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(await _f.WithDbAsync((db, _) => db.Customers.AnyAsync(c => c.CustomerName == "Forged")));
        var login = await _f.NewWeb().PostFormAsync("/Account/Login", new Dictionary<string, string?> { ["UserName"] = Creds.Arjun, ["Password"] = Creds.DemoPw }, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session_and_is_audited()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Priya);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/Customers")).StatusCode);
        var r = await web.PostFormAsync("/Account/Logout", new Dictionary<string, string?>(), "/Customers");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await web.Http.GetAsync("/Customers")).StatusCode);
        Assert.True(await _f.WithDbAsync((db, _) => db.AuditLogs.AnyAsync(a => a.Action == "Logout" && a.UserName == Creds.Priya)));
        // logout is POST-only: a GET (e.g. an <img> tag) cannot sign a user out
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await (await _f.NewWeb().SignedInAsync(Creds.Priya)).Http.GetAsync("/Account/Logout")).StatusCode);
    }

    [Fact]
    public async Task Deactivated_users_lose_their_session_and_token_immediately()
    {
        var email = $"gone{Seq.Next()}@example.com";
        var user = _f.NewWeb();
        await user.PostFormAsync("/Account/Register", new Dictionary<string, string?> { ["FullName"] = "Soon Gone", ["Email"] = email, ["Password"] = "Str0ng#Passw0rd", ["ConfirmPassword"] = "Str0ng#Passw0rd" });
        var api = _f.NewApi();
        await api.LoginAsync(email, "Str0ng#Passw0rd");
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/Customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/customers")).Status);

        var admin = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var id = await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync());
        var r = await admin.PostFormAsync($"/Users/SetActive/{id}", new Dictionary<string, string?> { ["active"] = "false" }, $"/Users/Details/{id}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await user.Http.GetAsync("/Customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/customers")).Status);
        Assert.Equal(HttpStatusCode.OK, (await _f.NewWeb().LoginAsync(email, "Str0ng#Passw0rd")).StatusCode); // login refused (form shown again)
    }

    [Fact]
    public async Task Api_login_and_logout_revoke_the_token()
    {
        var api = _f.NewApi();
        var login = await api.Http.PostAsJsonAsync("/api/auth/login", new { userName = Creds.Manager, password = Creds.DemoPw });
        var body = await login.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        await api.LoginAsync(Creds.Manager);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/customers")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await api.PostAsync("/api/auth/logout", null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/customers")).Status);
    }

    [Fact]
    public async Task Api_login_rejects_bad_credentials_and_bad_payloads()
    {
        var api = _f.NewApi();
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostAsync("/api/auth/login", new { userName = Creds.Admin, password = "nope" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/auth/login", new { userName = "", password = "" })).Status);
        var tampered = api.Http.DefaultRequestHeaders; tampered.Authorization = new("Bearer", "eyJhbGciOiJIUzI1NiJ9.e30.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/customers")).Status);
    }

    [Fact]
    public async Task Admin_reset_link_sets_a_new_password_once()
    {
        var email = $"rst{Seq.Next()}@example.com";
        await _f.NewWeb().PostFormAsync("/Account/Register", new Dictionary<string, string?> { ["FullName"] = "Reset Me", ["Email"] = email, ["Password"] = "Old#Passw0rd123", ["ConfirmPassword"] = "Old#Passw0rd123" });
        var admin = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var id = await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync());
        var page = await (await admin.PostFormAsync($"/Users/ResetLink/{id}", new Dictionary<string, string?>(), $"/Users/Details/{id}")).Content.ReadAsStringAsync();
        var link = System.Text.RegularExpressions.Regex.Match(page, "value=\"(http[^\"]*ResetPassword[^\"]*)\"").Groups[1].Value.Replace("&amp;", "&");
        Assert.False(string.IsNullOrEmpty(link));
        var uri = new Uri(link);

        var user = _f.NewWeb();
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync(uri.PathAndQuery)).StatusCode);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var fields = new Dictionary<string, string?> { ["UserId"] = q["userId"], ["Token"] = q["token"], ["NewPassword"] = "Brand#NewPass99", ["ConfirmPassword"] = "Brand#NewPass99" };
        Assert.Equal(HttpStatusCode.Redirect, (await user.PostFormAsync("/Account/ResetPassword", fields, uri.PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await _f.NewWeb().LoginAsync(email, "Brand#NewPass99")).StatusCode);
        // the token is single use
        fields["NewPassword"] = fields["ConfirmPassword"] = "Another#Pass123";
        Assert.Equal(HttpStatusCode.OK, (await user.PostFormAsync("/Account/ResetPassword", fields, uri.PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.NewWeb().LoginAsync(email, "Another#Pass123")).StatusCode);
        // neither the token nor any password appears in the audit trail
        var dump = await _f.WithDbAsync(async (db, _) => string.Join("|", await db.AuditLogs.Select(a => a.Details + a.OldValue + a.NewValue).ToListAsync()));
        Assert.DoesNotContain(q["token"]!, dump);
        Assert.DoesNotContain("Brand#NewPass99", dump);
    }
}

public class RateLimitTests
{
    [Fact]
    public async Task Login_attempts_are_throttled_per_client()
    {
        using var f = new ThrottledAppFactory();
        var api = f.NewApi();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await api.PostAsync("/api/auth/login", new { userName = "x@example.com", password = "bad" })).Status);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.Unauthorized, codes[0]);
    }
}
