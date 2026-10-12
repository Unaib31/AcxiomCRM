using System.Net;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Tests;

public class AuthorizationTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AuthorizationTests(AppFactory f) => _f = f;

    private Task<string> IdOf(string email) => _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync());

    private async Task<int> CreateCustomerAs(string email, string? assignedTo = null)
    {
        var api = await _f.NewApi().AsAsync(email);
        var (s, b) = await api.PostAsync("/api/customers", Seq.Customer(assignedTo: assignedTo));
        Assert.Equal(HttpStatusCode.Created, s);
        return b.GetProperty("customerId").GetInt32();
    }

    [Theory]
    [InlineData("/Users")] [InlineData("/Users/Create")] [InlineData("/Users/Roles")] [InlineData("/Audit")] [InlineData("/Audit/Export")]
    public async Task Sales_executive_cannot_open_administration(string url)
    {
        var r = await (await _f.NewWeb().SignedInAsync(Creds.Arjun)).Http.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("AccessDenied", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Manager_can_view_but_not_administer_users()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Manager);
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/Users")).StatusCode);
        foreach (var url in new[] { "/Users/Create", "/Users/Roles" })
            Assert.Contains("AccessDenied", (await web.Http.GetAsync(url)).Headers.Location!.ToString());
        var id = await IdOf(Creds.Arjun);
        Assert.Contains("AccessDenied", (await web.Http.GetAsync($"/Users/Edit/{id}")).Headers.Location!.ToString());
        var post = await web.PostFormAsync($"/Users/SetActive/{id}", new Dictionary<string, string?> { ["active"] = "false" }, "/Users");
        Assert.Contains("AccessDenied", post.Headers.Location!.ToString());
        Assert.True(await _f.WithDbAsync((db, _) => db.Users.Where(u => u.Email == Creds.Arjun).Select(u => u.IsActive).FirstAsync()));
        // managers only see their team in the user list; users outside the team are invisible
        var list = await web.GetStringAsync("/Users");
        Assert.Contains("Arjun Mehta", list);
        Assert.DoesNotContain("Rahul Verma", list);
        Assert.DoesNotContain("System Administrator", list);
    }

    [Fact]
    public async Task Admin_can_open_all_administration_pages()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Admin, Creds.AdminPw);
        foreach (var url in new[] { "/Users", "/Users/Create", "/Users/Roles", "/Audit", "/Reports/Run/audit", "/Reports/Run/useractivity" })
            Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Records_of_other_users_are_invisible_and_untouchable_by_url_tampering()
    {
        var priyaCustomer = await CreateCustomerAs(Creds.Priya);
        var arjun = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        foreach (var url in new[] { $"/Customers/Details/{priyaCustomer}", $"/Customers/Edit/{priyaCustomer}" })
            Assert.Equal(HttpStatusCode.NotFound, (await arjun.Http.GetAsync(url)).StatusCode);

        var edit = await arjun.PostFormAsync($"/Customers/Edit/{priyaCustomer}", new Dictionary<string, string?>
            { ["CustomerName"] = "Hijacked", ["Email"] = "hijack@example.com", ["Phone"] = Seq.Phone(), ["Status"] = "Active" }, "/Customers/Create");
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        var del = await arjun.PostFormAsync($"/Customers/Delete/{priyaCustomer}", new Dictionary<string, string?>(), "/Customers/Create");
        Assert.Contains("AccessDenied", del.Headers.Location!.ToString()); // sales executives may not delete at all
        Assert.False(await _f.WithDbAsync((db, _) => db.Customers.AnyAsync(c => c.CustomerId == priyaCustomer && c.CustomerName == "Hijacked")));

        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync($"/api/customers/{priyaCustomer}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await api.PutAsync($"/api/customers/{priyaCustomer}", Seq.Customer())).Status);
        var list = await api.GetAsync("/api/customers?pageSize=100");
        Assert.DoesNotContain(list.Body.GetProperty("items").EnumerateArray(), c => c.GetProperty("customerId").GetInt32() == priyaCustomer);
    }

    [Fact]
    public async Task Manager_sees_team_records_but_not_records_outside_the_team()
    {
        var teamCustomer = await CreateCustomerAs(Creds.Arjun);       // Arjun reports to the manager
        var outsiderCustomer = await CreateCustomerAs(Creds.Rahul);   // Rahul has no manager
        var manager = await _f.NewApi().AsAsync(Creds.Manager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/customers/{teamCustomer}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/customers/{outsiderCustomer}")).Status);
        var admin = await _f.NewApi().AsAsync(Creds.Admin, Creds.AdminPw);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/customers/{outsiderCustomer}")).Status);
    }

    [Fact]
    public async Task Sales_executive_cannot_assign_records_to_someone_else()
    {
        var priya = await IdOf(Creds.Priya);
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (s, b) = await api.PostAsync("/api/customers", Seq.Customer(assignedTo: priya));
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("assign records to yourself", ApiClient.Errors(b));
        var (s2, _) = await api.PostAsync("/api/leads", new { leadName = "X", source = "Website", status = "New", priority = "Low", expectedValue = 10, assignedTo = priya });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        var (s3, _) = await api.PostAsync("/api/customers", Seq.Customer(assignedTo: "00000000-fake-id"));
        Assert.Equal(HttpStatusCode.BadRequest, s3);
    }

    [Fact]
    public async Task Manager_can_assign_to_team_members_but_not_outsiders()
    {
        var api = await _f.NewApi().AsAsync(Creds.Manager);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/customers", Seq.Customer(assignedTo: await IdOf(Creds.Priya)))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/customers", Seq.Customer(assignedTo: await IdOf(Creds.Rahul)))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/customers", Seq.Customer(assignedTo: await IdOf(Creds.Admin)))).Status);
    }

    [Fact]
    public async Task Admin_can_assign_to_any_active_user()
    {
        var api = await _f.NewApi().AsAsync(Creds.Admin, Creds.AdminPw);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/customers", Seq.Customer(assignedTo: await IdOf(Creds.Rahul)))).Status);
    }

    [Fact]
    public async Task Sales_executive_cannot_delete_but_manager_can()
    {
        var id = await CreateCustomerAs(Creds.Arjun);
        var exec = await _f.NewApi().AsAsync(Creds.Arjun);
        Assert.Equal(HttpStatusCode.Forbidden, (await exec.DeleteAsync($"/api/customers/{id}")).Status);
        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        Assert.Contains("AccessDenied", (await web.PostFormAsync($"/Customers/Delete/{id}", new Dictionary<string, string?>(), "/Customers/Create")).Headers.Location!.ToString());
        Assert.True(await _f.WithDbAsync((db, _) => db.Customers.AnyAsync(c => c.CustomerId == id)));
        var mgr = await _f.NewApi().AsAsync(Creds.Manager);
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.DeleteAsync($"/api/customers/{id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await mgr.GetAsync($"/api/customers/{id}")).Status);
    }

    [Fact]
    public async Task Pipeline_report_endpoint_is_for_managers_and_admins_only()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _f.NewApi().AsAsync(Creds.Arjun)).GetAsync("/api/reports/pipeline")).Status);
        Assert.Equal(HttpStatusCode.OK, (await (await _f.NewApi().AsAsync(Creds.Manager)).GetAsync("/api/reports/pipeline")).Status);
        Assert.Equal(HttpStatusCode.OK, (await (await _f.NewApi().AsAsync(Creds.Admin, Creds.AdminPw)).GetAsync("/api/reports/pipeline")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewApi().GetAsync("/api/reports/pipeline")).Status);
    }

    [Fact]
    public async Task Sales_executive_only_sees_own_data_in_lists_dashboard_search_and_reports()
    {
        var arjunId = await IdOf(Creds.Arjun);
        var expected = await _f.WithDbAsync((db, _) => db.Customers.CountAsync(c => c.AssignedTo == arjunId));
        var total = await _f.WithDbAsync((db, _) => db.Customers.CountAsync());
        Assert.True(expected < total);

        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (_, list) = await api.GetAsync("/api/customers?pageSize=100");
        Assert.Equal(expected, list.GetProperty("totalCount").GetInt32());
        Assert.All(list.GetProperty("items").EnumerateArray(), c => Assert.Equal(arjunId, c.GetProperty("assignedTo").GetString()));

        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var dash = await web.GetStringAsync("/");
        Assert.Contains("Your assigned records", dash);
        var report = await web.GetStringAsync("/Reports/Run/customers");
        Assert.Contains($"Customers</div><div class=\"h5 mb-0\">{expected}</div>", report);
        // reports that expose other people's activity do not exist for this role
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/Reports/Run/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/Reports/Run/useractivity")).StatusCode);
        var search = await web.GetStringAsync("/Search?q=Textiles");
        Assert.DoesNotContain("Rao Healthcare", search);
    }

    [Fact]
    public async Task Idor_on_followups_and_opportunities_is_blocked()
    {
        var (oppId, fuId) = await _f.WithDbAsync(async (db, _) =>
        {
            var priya = await db.Users.Where(u => u.Email == Creds.Priya).Select(u => u.Id).FirstAsync();
            var o = await db.Opportunities.Where(x => x.AssignedTo == priya && x.Stage == AcxiomCRM.Domain.OpportunityStage.Proposal).Select(x => x.OpportunityId).FirstOrDefaultAsync();
            var f = await db.FollowUps.Where(x => x.AssignedTo == priya && x.Status == AcxiomCRM.Domain.FollowUpStatus.Planned).Select(x => x.FollowUpId).FirstAsync();
            return (o, f);
        });
        var arjun = await _f.NewApi().AsAsync(Creds.Arjun);
        if (oppId != 0)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await arjun.GetAsync($"/api/opportunities/{oppId}")).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await arjun.PostAsync($"/api/opportunities/{oppId}/stage", new { stage = "Won" })).Status);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await arjun.PostAsync($"/api/followups/{fuId}/complete", new { remarks = "x" })).Status);
        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync($"/FollowUps/Edit/{fuId}")).StatusCode);
    }
}
