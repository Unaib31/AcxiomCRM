using System.Net;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Tests;

/// <summary>Drives the Razor UI end to end (forms, anti-forgery, redirects), not just the services.</summary>
public class UiFlowTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public UiFlowTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Lead_goes_from_creation_to_conversion_through_the_ui()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var name = $"UI Lead {Seq.Next()}";
        var create = await web.PostFormAsync("/Leads/Create", new Dictionary<string, string?>
            { ["LeadName"] = name, ["Email"] = $"ui{Seq.Next()}@example.com", ["Phone"] = Seq.Phone(), ["CompanyName"] = "UI Leads Ltd", ["Source"] = "Referral", ["Status"] = "New", ["Priority"] = "High", ["ExpectedValue"] = "90000" }, "/Leads/Create");
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());

        // the details page only offers valid next steps
        var details = await web.GetStringAsync($"/Leads/Details/{id}");
        Assert.Contains(name, details);
        Assert.DoesNotContain("value=\"Qualified\"", details);
        Assert.DoesNotContain("value=\"Converted\"", details);

        // New -> Qualified via a forged request is refused, a legal step works
        var forged = await web.PostFormAsync($"/Leads/ChangeStatus/{id}", new Dictionary<string, string?> { ["status"] = "Qualified" }, $"/Leads/Details/{id}");
        Assert.Equal(HttpStatusCode.Redirect, forged.StatusCode);
        Assert.Equal("New", await _f.WithDbAsync(async (db, _) => (await db.Leads.FindAsync(id))!.Status.ToString()));
        await web.PostFormAsync($"/Leads/ChangeStatus/{id}", new Dictionary<string, string?> { ["status"] = "Contacted" }, $"/Leads/Details/{id}");
        await web.PostFormAsync($"/Leads/ChangeStatus/{id}", new Dictionary<string, string?> { ["status"] = "Qualified" }, $"/Leads/Details/{id}");
        Assert.Contains("Convert", await web.GetStringAsync($"/Leads/Details/{id}"));

        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync($"/Leads/Convert/{id}")).StatusCode);
        var bad = await web.PostFormAsync($"/Leads/Convert/{id}", new Dictionary<string, string?> { ["CreateOpportunity"] = "true", ["OpportunityName"] = "Deal", ["Amount"] = "0", ["Probability"] = "101" }, $"/Leads/Convert/{id}");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var ok = await web.PostFormAsync($"/Leads/Convert/{id}", new Dictionary<string, string?>
            { ["CreateOpportunity"] = "true", ["OpportunityName"] = "UI Converted Deal", ["Amount"] = "90000", ["Probability"] = "30", ["ExpectedCloseDate"] = _f.Today.AddDays(14).ToString("yyyy-MM-dd") }, $"/Leads/Convert/{id}");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains("/Opportunities/Details/", ok.Headers.Location!.OriginalString);

        var oppId = int.Parse(ok.Headers.Location.OriginalString.Split('/').Last());
        // move through the pipeline from the board and close it
        Assert.Equal(HttpStatusCode.OK, (await web.Http.GetAsync("/Opportunities/Pipeline")).StatusCode);
        await web.PostFormAsync($"/Opportunities/ChangeStage/{oppId}", new Dictionary<string, string?> { ["Stage"] = "Proposal", ["returnTo"] = "pipeline" }, "/Opportunities/Pipeline");
        Assert.Equal("Proposal", await _f.WithDbAsync(async (db, _) => (await db.Opportunities.FindAsync(oppId))!.Stage.ToString()));
        await web.PostFormAsync($"/Opportunities/ChangeStage/{oppId}", new Dictionary<string, string?> { ["Stage"] = "Won", ["OutcomeNotes"] = "Closed in UI" }, $"/Opportunities/Details/{oppId}");
        var won = await web.GetStringAsync($"/Opportunities/Details/{oppId}");
        Assert.Contains("Closed in UI", won);
        Assert.Equal(HttpStatusCode.Redirect, (await web.Http.GetAsync($"/Opportunities/Edit/{oppId}")).StatusCode); // closed deals cannot be edited
    }

    [Fact]
    public async Task Followup_can_be_scheduled_rescheduled_and_completed_through_the_ui()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Priya);
        var cid = await _f.WithDbAsync(async (db, _) => await (from c in db.Customers join u in db.Users on c.AssignedTo equals u.Id where u.Email == Creds.Priya select c.CustomerId).FirstAsync());
        var subject = $"UI follow-up {Seq.Next()}";
        Dictionary<string, string?> Form(DateOnly d) => new() { ["RelatedType"] = "Customer", ["RelatedId"] = cid.ToString(), ["FollowUpDate"] = d.ToString("yyyy-MM-dd"), ["Subject"] = subject, ["FollowUpType"] = "Meeting", ["Status"] = "Planned" };

        var past = await web.PostFormAsync("/FollowUps/Create", Form(_f.Today.AddDays(-1)), "/FollowUps/Create");
        Assert.Equal(HttpStatusCode.OK, past.StatusCode);
        Assert.Contains("Follow-up date cannot be earlier than today.", await past.Content.ReadAsStringAsync());

        var ok = await web.PostFormAsync("/FollowUps/Create", Form(_f.Today.AddDays(3)), "/FollowUps/Create");
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var id = await _f.WithDbAsync((db, _) => db.FollowUps.Where(f => f.Subject == subject).Select(f => f.FollowUpId).SingleAsync());
        Assert.Contains(subject, await web.GetStringAsync("/FollowUps?view=upcoming"));

        var move = await web.PostFormAsync($"/FollowUps/Edit/{id}", Form(_f.Today.AddDays(6)), $"/FollowUps/Edit/{id}");
        Assert.Equal(HttpStatusCode.Redirect, move.StatusCode);
        Assert.Equal(_f.Today.AddDays(6), await _f.WithDbAsync(async (db, _) => (await db.FollowUps.FindAsync(id))!.FollowUpDate));

        var done = await web.PostFormAsync($"/FollowUps/Complete/{id}", new Dictionary<string, string?> { ["remarks"] = "All good", ["returnUrl"] = "/FollowUps" }, "/FollowUps");
        Assert.Equal("/FollowUps", done.Headers.Location!.OriginalString);
        Assert.Equal("Completed", await _f.WithDbAsync(async (db, _) => (await db.FollowUps.FindAsync(id))!.Status.ToString()));
        Assert.DoesNotContain(subject, await web.GetStringAsync("/FollowUps?view=upcoming"));
        // an off-site returnUrl is ignored
        var evil = await web.PostFormAsync($"/FollowUps/Cancel/{id}", new Dictionary<string, string?> { ["returnUrl"] = "https://evil.example" }, "/FollowUps");
        Assert.DoesNotContain("evil.example", evil.Headers.Location?.OriginalString ?? "");
    }

    [Fact]
    public async Task Notifications_bell_counts_the_users_own_due_and_overdue_followups()
    {
        await _f.WithDbAsync(async (db, clock) =>
        {
            var u = await db.Users.FirstAsync(x => x.Email == Creds.Rahul);
            var c = new AcxiomCRM.Domain.Entities.Customer { CustomerName = "Bell Customer", Email = $"bell{Seq.Next()}@example.com", Phone = Seq.Phone(), AssignedTo = u.Id, CreatedBy = u.Id };
            db.Customers.Add(c); await db.SaveChangesAsync();
            db.FollowUps.Add(new AcxiomCRM.Domain.Entities.FollowUp { CustomerId = c.CustomerId, Subject = "Bell overdue", FollowUpDate = clock.Today.AddDays(-2), AssignedTo = u.Id, CreatedBy = u.Id });
            db.FollowUps.Add(new AcxiomCRM.Domain.Entities.FollowUp { CustomerId = c.CustomerId, Subject = "Bell today", FollowUpDate = clock.Today, AssignedTo = u.Id, CreatedBy = u.Id });
            db.FollowUps.Add(new AcxiomCRM.Domain.Entities.FollowUp { CustomerId = c.CustomerId, Subject = "Bell later", FollowUpDate = clock.Today.AddDays(9), AssignedTo = u.Id, CreatedBy = u.Id });
            await db.SaveChangesAsync(); return 0;
        });
        static string Bell(string page) { var i = page.IndexOf("Follow-up reminders", StringComparison.Ordinal); Assert.True(i > 0); return page.Substring(i, Math.Min(2500, page.Length - i)); }
        var html = Bell(await (await _f.NewWeb().SignedInAsync(Creds.Rahul)).GetStringAsync("/Customers"));
        Assert.Contains("Bell overdue", html); Assert.Contains("Bell today", html); Assert.DoesNotContain("Bell later", html);
        var other = Bell(await (await _f.NewWeb().SignedInAsync(Creds.Priya)).GetStringAsync("/Customers"));
        Assert.DoesNotContain("Bell overdue", other);
    }
}
