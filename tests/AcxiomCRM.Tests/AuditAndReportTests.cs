using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Data;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Rules;
using AcxiomCRM.Infrastructure;
using AcxiomCRM.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Tests;

public class AuditTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AuditTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Create_update_delete_write_audit_entries_with_user_values_and_ip()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (_, c) = await api.PostAsync("/api/customers", Seq.Customer(name: "Audit Subject"));
        var id = c.GetProperty("customerId").GetInt32();
        await api.PutAsync($"/api/customers/{id}", new { customerName = "Audit Subject Renamed", email = c.GetProperty("email").GetString(), phone = c.GetProperty("phone").GetString(), status = "Prospect" });
        await (await _f.NewApi().AsAsync(Creds.Manager)).DeleteAsync($"/api/customers/{id}");

        var rows = await _f.WithDbAsync((db, _) => db.AuditLogs.Where(a => a.EntityName == "Customer" && a.RecordId == id.ToString()).OrderBy(a => a.AuditLogId).ToListAsync());
        Assert.Equal(new[] { "Create", "StatusChange", "Delete" }, rows.Select(r => r.Action));
        Assert.Equal(new[] { Creds.Arjun, Creds.Arjun, Creds.Manager }, rows.Select(r => r.UserName));
        Assert.All(rows, r => { Assert.Equal("Customers", r.Module); Assert.Equal("Success", r.Result); Assert.NotNull(r.UserId); Assert.True(r.CreatedDate > DateTime.UtcNow.AddMinutes(-5)); });
        Assert.Contains("Audit Subject", rows[0].NewValue);
        Assert.Contains("Status: Active → Prospect", rows[1].Details);
        Assert.Contains("\"CustomerName\":\"Audit Subject\"", rows[1].OldValue);
        Assert.Contains("\"CustomerName\":\"Audit Subject Renamed\"", rows[1].NewValue);
        Assert.Contains("\"Status\":\"Prospect\"", rows[1].NewValue);
        Assert.Contains("CUS-", rows[0].NewValue);   // the generated business code is part of the creation record
    }

    [Fact]
    public async Task Status_changes_are_recorded_with_before_and_after()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (_, l) = await api.PostAsync("/api/leads", new { leadName = "Audit Lead", source = "Website", status = "New", priority = "Low", expectedValue = 10 });
        var id = l.GetProperty("leadId").GetInt32();
        await api.PostAsync($"/api/leads/{id}/status", new { status = "Contacted" });
        var change = await _f.WithDbAsync((db, _) => db.AuditLogs.SingleAsync(a => a.EntityName == "Lead" && a.RecordId == id.ToString() && a.Action == "StatusChange"));
        Assert.Contains("Status: New → Contacted", change.Details);
    }

    [Fact]
    public async Task Role_changes_and_lockouts_are_audited_by_admin_actions()
    {
        var admin = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var id = await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == Creds.Rahul).Select(u => u.Id).FirstAsync());
        var r = await admin.PostFormAsync($"/Users/Edit/{id}", new Dictionary<string, string?> { ["FullName"] = "Rahul Verma", ["Email"] = Creds.Rahul, ["Role"] = "Manager", ["IsActive"] = "true" }, $"/Users/Edit/{id}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var e = await _f.WithDbAsync((db, _) => db.AuditLogs.SingleAsync(a => a.Action == "RoleChange" && a.RecordId == id));
        Assert.Equal("SalesExecutive", e.OldValue); Assert.Equal("Manager", e.NewValue); Assert.Equal(Creds.Admin, e.UserName);
        // put it back so other tests in this class keep their assumptions
        await admin.PostFormAsync($"/Users/Edit/{id}", new Dictionary<string, string?> { ["FullName"] = "Rahul Verma", ["Email"] = Creds.Rahul, ["Role"] = "SalesExecutive", ["IsActive"] = "true" }, $"/Users/Edit/{id}");
    }

    [Fact]
    public async Task Admin_cannot_demote_or_deactivate_themselves_or_the_last_admin()
    {
        var admin = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var id = await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == Creds.Admin).Select(u => u.Id).FirstAsync());
        var r = await admin.PostFormAsync($"/Users/Edit/{id}", new Dictionary<string, string?> { ["FullName"] = "System Administrator", ["Email"] = Creds.Admin, ["Role"] = "SalesExecutive", ["IsActive"] = "false" }, $"/Users/Edit/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("cannot change your own role", await r.Content.ReadAsStringAsync());
        var stillAdmin = await _f.WithDbAsync(async (db, _) => await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join ro in db.Roles on ur.RoleId equals ro.Id where u.Id == id select ro.Name).SingleAsync());
        Assert.Equal("Admin", stillAdmin);
    }

    [Fact]
    public async Task Admin_can_create_users_and_assign_roles_with_policy_enforced()
    {
        var admin = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var email = $"made{Seq.Next()}@example.com";
        var weak = await admin.PostFormAsync("/Users/Create", new Dictionary<string, string?> { ["FullName"] = "Made User", ["Email"] = email, ["Role"] = "Manager", ["IsActive"] = "true", ["Password"] = "weak", ["ConfirmPassword"] = "weak" }, "/Users/Create");
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        var badRole = await admin.PostFormAsync("/Users/Create", new Dictionary<string, string?> { ["FullName"] = "Made User", ["Email"] = email, ["Role"] = "SuperAdmin", ["IsActive"] = "true", ["Password"] = "Str0ng#Passw0rd", ["ConfirmPassword"] = "Str0ng#Passw0rd" }, "/Users/Create");
        Assert.Equal(HttpStatusCode.OK, badRole.StatusCode);
        Assert.False(await _f.WithDbAsync((db, _) => db.Users.AnyAsync(u => u.Email == email)));
        var ok = await admin.PostFormAsync("/Users/Create", new Dictionary<string, string?> { ["FullName"] = "Made User", ["Email"] = email, ["Role"] = "Manager", ["IsActive"] = "true", ["Password"] = "Str0ng#Passw0rd", ["ConfirmPassword"] = "Str0ng#Passw0rd" }, "/Users/Create");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await _f.NewWeb().LoginAsync(email, "Str0ng#Passw0rd")).StatusCode);
        Assert.True(await _f.WithDbAsync((db, _) => db.AuditLogs.AnyAsync(a => a.Action == "UserCreated" && a.Details!.Contains(email))));
        // roles: a sales executive cannot be given a manager outside the manager role
        var bad = await admin.PostFormAsync("/Users/Create", new Dictionary<string, string?> { ["FullName"] = "Under Nobody", ["Email"] = $"un{Seq.Next()}@example.com", ["Role"] = "SalesExecutive", ["ManagerId"] = await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == Creds.Arjun).Select(u => u.Id).FirstAsync()), ["IsActive"] = "true", ["Password"] = "Str0ng#Passw0rd", ["ConfirmPassword"] = "Str0ng#Passw0rd" }, "/Users/Create");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("valid active manager", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_secret_ever_reaches_the_audit_trail()
    {
        var web = _f.NewWeb();
        await web.LoginAsync(Creds.Arjun, "Leak#Check12345");
        await (await _f.NewWeb().SignedInAsync(Creds.Priya)).PostFormAsync("/Account/ChangePassword", new Dictionary<string, string?> { ["CurrentPassword"] = Creds.DemoPw, ["NewPassword"] = "Leak#Check98765", ["ConfirmPassword"] = "Leak#Check98765" });
        var dump = await _f.WithDbAsync(async (db, _) => string.Join("|", await db.AuditLogs.Select(a => a.Details + "|" + a.OldValue + "|" + a.NewValue + "|" + a.UserName).ToListAsync()));
        foreach (var secret in new[] { "Leak#Check12345", "Leak#Check98765", Creds.DemoPw, Creds.AdminPw, "AQAAAA", "PasswordHash", "SecurityStamp" })
            Assert.DoesNotContain(secret, dump);
    }

    [Fact]
    public async Task Audit_log_is_append_only_in_code_and_in_the_database()
    {
        await _f.NewApi().AsAsync(Creds.Arjun); // guarantees at least one login row
        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.WithDbAsync(async (db, _) =>
        {
            var row = await db.AuditLogs.FirstAsync(); row.Details = "tampered"; await db.SaveChangesAsync(); return 0;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.WithDbAsync(async (db, _) =>
        {
            db.AuditLogs.Remove(await db.AuditLogs.FirstAsync()); await db.SaveChangesAsync(); return 0;
        }));
        // direct SQL against the table is stopped by the database triggers
        await Assert.ThrowsAnyAsync<Exception>(() => _f.WithDbAsync(async (db, _) => await db.Database.ExecuteSqlRawAsync("UPDATE AuditLogs SET Details = 'x'")));
        await Assert.ThrowsAnyAsync<Exception>(() => _f.WithDbAsync(async (db, _) => await db.Database.ExecuteSqlRawAsync("DELETE FROM AuditLogs")));
        Assert.True(await _f.WithDbAsync((db, _) => db.AuditLogs.AnyAsync()));
    }

    [Fact]
    public async Task Audit_viewer_filters_and_scopes_by_role()
    {
        await _f.NewApi().AsAsync(Creds.Arjun);
        var admin = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var all = await admin.GetStringAsync("/Audit?pageSize=100");
        Assert.Contains("Login", all);
        var filtered = await admin.GetStringAsync("/Audit?module=Authentication&action=Login&pageSize=100");
        Assert.DoesNotContain("<td class=\"fw-semibold\">Create</td>", filtered);
        Assert.Contains("<td class=\"fw-semibold\">Login</td>", filtered);
        var byUser = await admin.GetStringAsync($"/Audit?userId={await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == Creds.Priya).Select(u => u.Id).FirstAsync())}&pageSize=100");
        Assert.DoesNotContain(Creds.Arjun, byUser);
        var dated = await admin.GetStringAsync($"/Audit?from={_f.Today.AddDays(1):yyyy-MM-dd}");
        Assert.Contains("No audit events match", dated);

        // the manager's limited view never shows authentication or user-administration events, nor IP addresses
        var mgr = await _f.NewWeb().SignedInAsync(Creds.Manager);
        var m = await mgr.GetStringAsync("/Audit?pageSize=100");
        Assert.DoesNotContain("LoginFailed", m); Assert.DoesNotContain("UserCreated", m); Assert.DoesNotContain("<th>IP</th>", m);
        var csv = await admin.Http.GetAsync("/Audit/Export");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
    }
}

public class ReportAndDashboardTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public ReportAndDashboardTests(AppFactory f) => _f = f;

    [Theory]
    [InlineData("customers")] [InlineData("leads")] [InlineData("followups")] [InlineData("opportunities")] [InlineData("pipeline")] [InlineData("conversion")]
    public async Task Every_role_can_run_the_shared_reports(string key)
    {
        foreach (var (u, pw) in new[] { (Creds.Admin, Creds.AdminPw), (Creds.Manager, Creds.DemoPw), (Creds.Arjun, Creds.DemoPw) })
        {
            var web = await _f.NewWeb().SignedInAsync(u, pw);
            Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync($"/Reports/Run/{key}")).StatusCode);
            var csv = await web.Http.GetAsync($"/Reports/Export/{key}");
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task Management_reports_are_hidden_from_sales_executives_but_not_from_managers()
    {
        var exec = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        foreach (var k in new[] { "useractivity", "audit" }) { Assert.Equal(HttpStatusCode.NotFound, (await exec.Http.GetAsync($"/Reports/Run/{k}")).StatusCode); Assert.Equal(HttpStatusCode.NotFound, (await exec.Http.GetAsync($"/Reports/Export/{k}")).StatusCode); }
        var mgr = await _f.NewWeb().SignedInAsync(Creds.Manager);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Http.GetAsync("/Reports/Run/useractivity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.Http.GetAsync("/Reports/Run/audit")).StatusCode);
        var index = await exec.GetStringAsync("/Reports");
        Assert.DoesNotContain("User Activity Report", index); Assert.DoesNotContain("Audit Report", index);
    }

    [Fact]
    public async Task Report_filters_and_sorting_work_and_respect_visibility()
    {
        var admin = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var all = await admin.GetStringAsync("/Reports/Run/leads?pageSize=100");
        Assert.Contains("<td class=\"\">Converted</td>", all);
        var onlyNew = await admin.GetStringAsync("/Reports/Run/leads?status=New&pageSize=100");
        Assert.Contains("<td class=\"\">New</td>", onlyNew);
        Assert.DoesNotContain("<td class=\"\">Converted</td>", onlyNew);
        var owner = await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == Creds.Priya).Select(u => u.Id).FirstAsync());
        var byOwner = await admin.GetStringAsync($"/Reports/Run/customers?assignedTo={owner}&pageSize=100");
        Assert.DoesNotContain("<td class=\"\">Arjun Mehta</td>", byOwner);
        Assert.Contains("<td class=\"\">Priya Nair</td>", byOwner);
        // a sales executive supplying someone else's id still only sees their own scope
        var exec = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var attempt = await exec.GetStringAsync($"/Reports/Run/customers?assignedTo={owner}&pageSize=100");
        Assert.DoesNotContain("Priya Nair</td>", attempt);
        Assert.Contains("No data for the selected filters.", attempt); // the owner filter can only narrow what the role may see
        var paged = await admin.GetStringAsync("/Reports/Run/customers?pageSize=5&page=2");
        Assert.Contains("Showing 6–10 of", paged);
        var empty = await admin.GetStringAsync($"/Reports/Run/opportunities?from={_f.Today.AddYears(5):yyyy-MM-dd}");
        Assert.Contains("No data for the selected filters.", empty);
    }

    [Fact]
    public async Task Csv_export_neutralises_spreadsheet_formulas()
    {
        var api = await _f.NewApi().AsAsync(Creds.Admin, Creds.AdminPw);
        var (s, _) = await api.PostAsync("/api/customers", new { customerName = "=HYPERLINK(\"http://evil\",\"x\")", email = $"csv{Seq.Next()}@example.com", phone = Seq.Phone(), status = "Active", companyName = "@SUM(1+1)" });
        Assert.Equal(HttpStatusCode.Created, s);
        var csv = await (await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw)).GetStringAsync("/Reports/Export/customers");
        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.Contains("\"'@SUM(1+1)\"", csv);
        Assert.DoesNotContain(",\"=HYPERLINK", csv);
    }

    [Fact]
    public async Task User_supplied_html_is_encoded_in_views()
    {
        var api = await _f.NewApi().AsAsync(Creds.Admin, Creds.AdminPw);
        var (_, c) = await api.PostAsync("/api/customers", new { customerName = "<script>alert(1)</script>", email = $"xss{Seq.Next()}@example.com", phone = Seq.Phone(), status = "Active", notes = "<img src=x onerror=alert(2)>" });
        var id = c.GetProperty("customerId").GetInt32();
        var web = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        foreach (var url in new[] { $"/Customers/Details/{id}", "/Customers?search=script", "/Search?q=script", $"/Customers/Edit/{id}" })
        {
            var html = await web.GetStringAsync(url);
            Assert.DoesNotContain("<script>alert(1)", html);
            Assert.DoesNotContain("<img src=x", html);
        }
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", await web.GetStringAsync($"/Customers/Details/{id}"));
    }

    [Fact]
    public async Task Dashboard_shows_the_required_cards_charts_and_date_filters()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        var html = await web.GetStringAsync("/");
        foreach (var card in new[] { "Total Customers", "Total Leads", "Open Leads", "Total Opportunities", "Open Opportunities", "Won Opportunities", "Lost Opportunities", "Total Pipeline Value", "Weighted pipeline", "Pending follow-ups", "Overdue follow-ups" })
            Assert.Contains(card, html);
        Assert.Equal(8, Regex.Matches(html, "class=\"card kpi ").Count);
        Assert.DoesNotContain("Opportunity outcomes", html);
        Assert.Equal(3, Regex.Matches(html, "<canvas data-chart=").Count);
        Assert.Contains("&quot;labels&quot;:[&quot;New&quot;,&quot;Contacted&quot;,&quot;Qualified&quot;", html);
        Assert.Contains("Qualification", html); Assert.Contains("Negotiation", html); Assert.Contains("Monthly sales outcomes", html);
        foreach (var range in new[] { "Today", "This week", "This month", "All time" }) Assert.Contains(range, html);
        var custom = await web.GetStringAsync($"/Dashboard?range=custom&from={_f.Today.AddDays(-1):yyyy-MM-dd}&to={_f.Today.AddDays(1):yyyy-MM-dd}");
        Assert.Contains(_f.Today.AddDays(-1).ToString("dd MMM yyyy"), custom);
        // an inverted/invalid custom range falls back safely
        Assert.Contains("All time", await web.GetStringAsync($"/Dashboard?range=custom&from={_f.Today:yyyy-MM-dd}&to={_f.Today.AddDays(-9):yyyy-MM-dd}"));
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/Dashboard?range=custom&from=garbage&to=nonsense")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_numbers_match_the_data_each_role_is_allowed_to_see()
    {
        foreach (var (email, password) in new[] { (Creds.Admin, Creds.AdminPw), (Creds.Manager, Creds.DemoPw), (Creds.Arjun, Creds.DemoPw) })
        {
            var expected = await _f.WithDbAsync(async (db, _) =>
            {
                var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
                var ownerIds = new List<string> { userId };
                if (email == Creds.Manager)
                    ownerIds.AddRange(await db.Users.Where(u => u.ManagerId == userId).Select(u => u.Id).ToListAsync());
                var customers = db.Customers.Where(c => email == Creds.Admin || ownerIds.Contains(c.AssignedTo));
                var leads = await db.Leads.Where(l => email == Creds.Admin || ownerIds.Contains(l.AssignedTo)).Select(l => l.Status).ToListAsync();
                var opportunities = await db.Opportunities.Where(o => email == Creds.Admin || ownerIds.Contains(o.AssignedTo)).ToListAsync();
                return new Dictionary<string, string>
                {
                    ["Total Customers"] = (await customers.CountAsync()).ToString("N0"),
                    ["Total Leads"] = leads.Count.ToString("N0"),
                    ["Open Leads"] = leads.Count(LeadWorkflow.IsOpen).ToString("N0"),
                    ["Total Opportunities"] = opportunities.Count.ToString("N0"),
                    ["Open Opportunities"] = opportunities.Count(o => OpportunityRules.IsActive(o.Stage)).ToString("N0"),
                    ["Won Opportunities"] = opportunities.Count(o => o.Stage == OpportunityStage.Won).ToString("N0"),
                    ["Lost Opportunities"] = opportunities.Count(o => o.Stage == OpportunityStage.Lost).ToString("N0"),
                    ["Total Pipeline Value"] = Ui.Money(opportunities.Where(o => OpportunityRules.IsActive(o.Stage)).Sum(o => o.Amount)),
                };
            });
            var web = await _f.NewWeb().SignedInAsync(email, password);
            var response = await web.Http.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            foreach (var (label, value) in expected)
                Assert.Matches($@"{Regex.Escape(label)}</span>\s*<span class=""value[^""]*"">{Regex.Escape(value)}</span>", html);
            Assert.Equal(email == Creds.Admin, html.Contains("Security &amp; activity") || html.Contains("Security & activity"));
            Assert.Equal(email == Creds.Manager, html.Contains("Team performance"));
        }
    }

    [Fact]
    public async Task Search_finds_accessible_records_and_ignores_short_or_hostile_terms()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        Assert.Contains("Sharma Textiles", await web.GetStringAsync("/Search?q=sharma"));
        Assert.Contains("Type at least 2 characters", await web.GetStringAsync("/Search?q=a"));
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/Search?q=%27%3B%20DROP%20TABLE%20Customers%3B--")).StatusCode);
        Assert.True(await _f.WithDbAsync((db, _) => db.Customers.AnyAsync()));
    }

    [Fact]
    public async Task Security_headers_and_error_pages_do_not_leak_internals()
    {
        var web = _f.NewWeb();
        var login = await web.Http.GetAsync("/Account/Login");
        Assert.Contains("default-src 'self'", login.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("unsafe-inline", login.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", login.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", login.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-store", login.Headers.GetValues("Cache-Control").Single());
        var cookies = login.Headers.GetValues("Set-Cookie").ToList();
        Assert.All(cookies, c => Assert.Contains("httponly", c, StringComparison.OrdinalIgnoreCase));
        var signed = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var missing = await signed.Http.GetAsync("/Customers/Details/9999999");
        var body = await missing.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("Page not found", body);
        Assert.DoesNotContain("Exception", body); Assert.DoesNotContain("SELECT", body);
        // the application's own auth cookie is HttpOnly + Strict
        var r = await _f.NewWeb().LoginAsync(Creds.Arjun, Creds.DemoPw);
        var auth = r.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("AcxiomCRM.Auth"));
        Assert.Contains("httponly", auth, StringComparison.OrdinalIgnoreCase); Assert.Contains("samesite=strict", auth, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Static_assets_are_served_locally_without_any_cdn_reference()
    {
        var web = _f.NewWeb();
        foreach (var url in new[] { "/css/site.css", "/js/site.js", "/lib/bootstrap/bootstrap.min.css", "/lib/chartjs/chart.umd.min.js", "/lib/jquery/jquery.min.js", "/lib/bootstrap-icons/fonts/bootstrap-icons.woff2" })
            Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync(url)).StatusCode);
        var page = await (await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw)).GetStringAsync("/");
        Assert.DoesNotMatch("https?://(?!localhost)", Regex.Replace(page, "data-chart=\"[^\"]*\"", ""));
        Assert.DoesNotContain(" style=\"", page); // inline styles would be blocked by the CSP
    }
}

public class ReportSortingTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public ReportSortingTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Report_columns_can_be_sorted_in_both_directions()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        static List<string> Names(string html) => Regex.Matches(html, "<td class=\"\">CUS-\\d+</td>\\s*<td class=\"\">([^<]+)</td>").Select(m => m.Groups[1].Value).ToList();
        var asc = await web.GetStringAsync("/Reports/Run/customers?sort=name&desc=false&pageSize=100");
        var desc = await web.GetStringAsync("/Reports/Run/customers?sort=name&desc=true&pageSize=100");
        var a = Names(asc); var d = Names(desc);
        Assert.True(a.Count >= 10);
        Assert.Equal(a.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), a);
        Assert.Equal(a.AsEnumerable().Reverse().ToList(), d);
        Assert.Contains("sort=name", asc); // header links keep the sort state
        var money = await web.GetStringAsync("/Reports/Run/opportunities?sort=amount&desc=true&pageSize=100");
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/Reports/Run/opportunities?sort=;DROP TABLE Customers&desc=maybe")).StatusCode);
        Assert.Contains("aria-sort=\"descending\"", money);
    }
}
