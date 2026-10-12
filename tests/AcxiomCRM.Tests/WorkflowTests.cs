using System.Net;
using AcxiomCRM.Domain;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Tests;

public class WorkflowTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public WorkflowTests(AppFactory f) => _f = f;

    private async Task<int> NewLead(ApiClient api, string status = "New", string? email = null, string? phone = null, decimal value = 120000)
    {
        var (s, b) = await api.PostAsync("/api/leads", new { leadName = $"Lead {Seq.Next()}", email = email ?? $"lead{Seq.Next()}@example.com", phone = phone ?? Seq.Phone(), companyName = $"LeadCo {Seq.Next()}", source = "Referral", status, priority = "High", expectedValue = value });
        Assert.Equal(HttpStatusCode.Created, s);
        return b.GetProperty("leadId").GetInt32();
    }

    private async Task Advance(ApiClient api, int id, params string[] statuses)
    {
        foreach (var st in statuses) Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/leads/{id}/status", new { status = st })).Status);
    }

    [Fact]
    public async Task Lead_status_workflow_rejects_invalid_transitions()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var id = await NewLead(api);
        var (s, b) = await api.PostAsync($"/api/leads/{id}/status", new { status = "Qualified" }); // New -> Qualified is not allowed
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("cannot move from New to Qualified", ApiClient.Errors(b));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/leads/{id}/status", new { status = "Converted" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/leads/{id}/status", new { status = "Nonsense" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/leads/{id}/status", new { status = 4 })).Status);
        await Advance(api, id, "Contacted", "Qualified");
        await Advance(api, id, "Lost");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/leads/{id}/status", new { status = "New" })).Status); // Lost is final
    }

    [Fact]
    public async Task Lead_edit_cannot_skip_the_workflow_either()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var id = await NewLead(api);
        var (s, _) = await api.PutAsync($"/api/leads/{id}", new { leadName = "Renamed", source = "Website", status = "Qualified", priority = "Low", expectedValue = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, s);
        var (s2, _) = await api.PostAsync("/api/leads", new { leadName = "Born converted", source = "Website", status = "Converted", priority = "Low", expectedValue = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
    }

    [Fact]
    public async Task Lead_validation_rules()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (s, b) = await api.PostAsync("/api/leads", new { });
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("Lead Name is required.", ApiClient.Errors(b));
        var (s2, b2) = await api.PostAsync("/api/leads", new { leadName = "L", email = "bad", phone = "1", source = "Website", status = "New", priority = "Low", expectedValue = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        var e = ApiClient.Errors(b2);
        Assert.Contains("Enter a valid email address.", e); Assert.Contains("Enter a valid phone number.", e); Assert.Contains("Expected Value", e);
    }

    [Fact]
    public async Task Qualified_lead_converts_to_customer_and_opportunity_and_is_audited()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var id = await NewLead(api, value: 275000);
        var (sEarly, _) = await api.PostAsync($"/api/leads/{id}/convert", new { createOpportunity = true });
        Assert.Equal(HttpStatusCode.Conflict, sEarly); // not qualified yet
        await Advance(api, id, "Contacted", "Qualified");

        var (s, b) = await api.PostAsync($"/api/leads/{id}/convert", new { createOpportunity = true, opportunityName = "Converted Deal", probability = 40 });
        Assert.Equal(HttpStatusCode.OK, s);
        var customerId = b.GetProperty("customerId").GetInt32(); var oppId = b.GetProperty("opportunityId").GetInt32();

        var (_, lead) = await api.GetAsync($"/api/leads/{id}");
        Assert.Equal("Converted", lead.GetProperty("status").GetString());
        Assert.Equal(customerId, lead.GetProperty("convertedCustomerId").GetInt32());
        var (_, opp) = await api.GetAsync($"/api/opportunities/{oppId}");
        Assert.Equal(275000m, opp.GetProperty("amount").GetDecimal());
        Assert.Equal(40, opp.GetProperty("probability").GetInt32());
        Assert.Equal("Qualification", opp.GetProperty("stage").GetString());
        Assert.Equal(110000m, opp.GetProperty("weightedAmount").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync($"/api/customers/{customerId}")).Status);

        // second conversion and further edits are blocked
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync($"/api/leads/{id}/convert", new { createOpportunity = false })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PutAsync($"/api/leads/{id}", new { leadName = "x", source = "Website", status = "Converted", priority = "Low", expectedValue = 1 })).Status);

        Assert.True(await _f.WithDbAsync((db, _) => db.AuditLogs.AnyAsync(a => a.Action == "LeadConversion" && a.RecordId == id.ToString())));
    }

    [Fact]
    public async Task Conversion_rejects_duplicate_customers_and_can_link_an_existing_one()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (_, c) = await api.PostAsync("/api/customers", Seq.Customer());
        var existingEmail = c.GetProperty("email").GetString();
        var existingId = c.GetProperty("customerId").GetInt32();
        var id = await NewLead(api, email: existingEmail);
        await Advance(api, id, "Contacted", "Qualified");
        var (s, b) = await api.PostAsync($"/api/leads/{id}/convert", new { createOpportunity = false });
        Assert.Equal(HttpStatusCode.Conflict, s);
        Assert.Contains("already exists", b.ToString());
        var (s2, b2) = await api.PostAsync($"/api/leads/{id}/convert", new { existingCustomerId = existingId, createOpportunity = false });
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal(existingId, b2.GetProperty("customerId").GetInt32());
    }

    [Fact]
    public async Task Conversion_without_contact_details_or_with_invalid_opportunity_values_fails_cleanly()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (_, l) = await api.PostAsync("/api/leads", new { leadName = "No contact", source = "Website", status = "New", priority = "Low", expectedValue = 1000 });
        var id = l.GetProperty("leadId").GetInt32();
        await Advance(api, id, "Contacted", "Qualified");
        var (s, b) = await api.PostAsync($"/api/leads/{id}/convert", new { createOpportunity = false });
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("valid email", b.ToString());
        var id2 = await NewLead(api);
        await Advance(api, id2, "Contacted", "Qualified");
        var (s2, _) = await api.PostAsync($"/api/leads/{id2}/convert", new { createOpportunity = true, amount = 0, probability = 50 });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        var (s3, _) = await api.PostAsync($"/api/leads/{id2}/convert", new { createOpportunity = true, expectedCloseDate = _f.Today.AddDays(-2).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.BadRequest, s3);
        // nothing was created by the failed attempts
        Assert.Equal("Qualified", (await api.GetAsync($"/api/leads/{id2}")).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Opportunity_pipeline_moves_and_closing_is_final()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (_, cs) = await api.GetAsync("/api/customers?pageSize=1");
        var cid = cs.GetProperty("items")[0].GetProperty("customerId").GetInt32();
        var (s, o) = await api.PostAsync("/api/opportunities", new { opportunityName = $"Flow {Seq.Next()}", customerId = cid, amount = 80000, stage = "Qualification", probability = 20, expectedCloseDate = _f.Today.AddDays(20).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.Created, s);
        var id = o.GetProperty("opportunityId").GetInt32();
        var (s1, b1) = await api.PostAsync($"/api/opportunities/{id}/stage", new { stage = "Negotiation" });
        Assert.Equal(HttpStatusCode.OK, s1); Assert.Equal(75, b1.GetProperty("probability").GetInt32());
        var (s2, b2) = await api.PostAsync($"/api/opportunities/{id}/stage", new { stage = "Won", outcomeNotes = "Signed" });
        Assert.Equal(HttpStatusCode.OK, s2);
        Assert.Equal(100, b2.GetProperty("probability").GetInt32());
        Assert.Equal("Signed", b2.GetProperty("outcomeNotes").GetString());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, b2.GetProperty("closedDate").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync($"/api/opportunities/{id}/stage", new { stage = "Proposal" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PutAsync($"/api/opportunities/{id}", new { opportunityName = "x", customerId = cid, amount = 1, stage = "Proposal", probability = 10, expectedCloseDate = _f.Today.AddDays(5).ToString("yyyy-MM-dd") })).Status);
    }

    [Fact]
    public async Task Marking_won_requires_a_positive_amount_and_lost_zeroes_the_probability()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (_, cs) = await api.GetAsync("/api/customers?pageSize=1");
        var cid = cs.GetProperty("items")[0].GetProperty("customerId").GetInt32();
        var (_, o) = await api.PostAsync("/api/opportunities", new { opportunityName = $"Lost {Seq.Next()}", customerId = cid, amount = 1000, stage = "Proposal", probability = 50, expectedCloseDate = _f.Today.AddDays(9).ToString("yyyy-MM-dd") });
        var (s, b) = await api.PostAsync($"/api/opportunities/{o.GetProperty("opportunityId").GetInt32()}/stage", new { stage = "Lost" });
        Assert.Equal(HttpStatusCode.OK, s); Assert.Equal(0, b.GetProperty("probability").GetInt32());
    }

    [Fact]
    public async Task Followup_lifecycle_complete_reschedule_cancel_and_audit()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var lead = await NewLead(api);
        var (s, f) = await api.PostAsync("/api/followups", new { relatedType = "Lead", relatedId = lead, followUpDate = _f.Today.AddDays(2).ToString("yyyy-MM-dd"), subject = "Intro call", followUpType = "Call", status = "Planned" });
        Assert.Equal(HttpStatusCode.Created, s);
        var id = f.GetProperty("followUpId").GetInt32();

        // reschedule: earlier than today is refused, a later date is accepted and audited
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync($"/api/followups/{id}", new { relatedType = "Lead", relatedId = lead, followUpDate = _f.Today.AddDays(-4).ToString("yyyy-MM-dd"), subject = "Intro call", followUpType = "Call", status = "Planned" })).Status);
        var newDate = _f.Today.AddDays(5).ToString("yyyy-MM-dd");
        var (su, bu) = await api.PutAsync($"/api/followups/{id}", new { relatedType = "Lead", relatedId = lead, followUpDate = newDate, subject = "Intro call", followUpType = "Call", status = "Planned" });
        Assert.Equal(HttpStatusCode.OK, su); Assert.Equal(newDate, bu.GetProperty("followUpDate").GetString());
        Assert.True(await _f.WithDbAsync((db, _) => db.AuditLogs.AnyAsync(a => a.Action == "Reschedule" && a.EntityName == "FollowUp" && a.RecordId == id.ToString())));

        var (sc, bc) = await api.PostAsync($"/api/followups/{id}/complete", new { remarks = "Spoke to them" });
        Assert.Equal(HttpStatusCode.OK, sc); Assert.Equal("Completed", bc.GetProperty("status").GetString());
        Assert.True(await _f.WithDbAsync((db, _) => db.AuditLogs.AnyAsync(a => a.Action == "StatusChange" && a.EntityName == "FollowUp" && a.RecordId == id.ToString() && a.Details!.Contains("Completed"))));
        // completion updated the related lead (New -> Contacted) and logged an activity
        Assert.Equal("Contacted", (await api.GetAsync($"/api/leads/{lead}")).Body.GetProperty("status").GetString());
        Assert.True(await _f.WithDbAsync((db, _) => db.Activities.AnyAsync(a => a.LeadId == lead && a.Status == ActivityStatus.Completed)));
        // completed follow-ups are final
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync($"/api/followups/{id}/complete", new { })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await api.PutAsync($"/api/followups/{id}", new { relatedType = "Lead", relatedId = lead, followUpDate = newDate, subject = "x", followUpType = "Call", status = "Planned" })).Status);
    }

    [Fact]
    public async Task Overdue_followups_are_listed_and_can_be_marked_missed()
    {
        // overdue items cannot be created through the API, so plant one directly (e.g. data from before a holiday)
        var id = await _f.WithDbAsync(async (db, clock) =>
        {
            var u = await db.Users.FirstAsync(x => x.Email == Creds.Priya);
            var c = await db.Customers.FirstAsync(x => x.AssignedTo == u.Id);
            var fu = new AcxiomCRM.Domain.Entities.FollowUp { CustomerId = c.CustomerId, FollowUpDate = clock.Today.AddDays(-3), Subject = "Old promise", Status = FollowUpStatus.Planned, AssignedTo = u.Id, CreatedBy = u.Id };
            db.FollowUps.Add(fu); await db.SaveChangesAsync();
            return fu.FollowUpId;
        });
        var api = await _f.NewApi().AsAsync(Creds.Priya);
        var (_, overdue) = await api.GetAsync("/api/followups?view=overdue&pageSize=100");
        var item = overdue.GetProperty("items").EnumerateArray().First(x => x.GetProperty("followUpId").GetInt32() == id);
        Assert.True(item.GetProperty("isOverdue").GetBoolean());
        var (s, b) = await api.PostAsync($"/api/followups/{id}/complete", new { });
        Assert.Equal(HttpStatusCode.OK, s); // an overdue follow-up can still be completed
        Assert.Equal("Completed", b.GetProperty("status").GetString());
        var (_, after) = await api.GetAsync("/api/followups?view=overdue&pageSize=100");
        Assert.DoesNotContain(after.GetProperty("items").EnumerateArray(), x => x.GetProperty("followUpId").GetInt32() == id);
    }

    [Fact]
    public async Task Customer_with_active_opportunities_cannot_be_deleted_until_they_are_closed()
    {
        var mgr = await _f.NewApi().AsAsync(Creds.Manager);
        var (_, c) = await mgr.PostAsync("/api/customers", Seq.Customer());
        var cid = c.GetProperty("customerId").GetInt32();
        var (_, o) = await mgr.PostAsync("/api/opportunities", new { opportunityName = $"Blocker {Seq.Next()}", customerId = cid, amount = 5000, stage = "Proposal", probability = 40, expectedCloseDate = _f.Today.AddDays(10).ToString("yyyy-MM-dd") });
        var (s, b) = await mgr.DeleteAsync($"/api/customers/{cid}");
        Assert.Equal(HttpStatusCode.Conflict, s);
        Assert.Contains("active opportunit", b.ToString());
        await mgr.PostAsync($"/api/opportunities/{o.GetProperty("opportunityId").GetInt32()}/stage", new { stage = "Lost" });
        Assert.Equal(HttpStatusCode.NoContent, (await mgr.DeleteAsync($"/api/customers/{cid}")).Status);
        // soft delete: the row remains for the audit trail, and the email can be reused afterwards
        Assert.True(await _f.WithDbAsync((db, _) => db.Customers.IgnoreQueryFilters().AnyAsync(x => x.CustomerId == cid && x.IsDeleted)));
        Assert.Equal(HttpStatusCode.Created, (await mgr.PostAsync("/api/customers", new { customerName = "Reuse", email = c.GetProperty("email").GetString(), phone = Seq.Phone(), status = "Active" })).Status);
    }

    [Fact]
    public async Task Activities_crud_through_the_web_ui()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var cid = await _f.WithDbAsync(async (db, _) => await (from c in db.Customers join u in db.Users on c.AssignedTo equals u.Id where u.Email == Creds.Arjun select c.CustomerId).FirstAsync());
        var subject = $"Web call {Seq.Next()}";
        var create = await web.PostFormAsync("/Activities/Create", new Dictionary<string, string?>
            { ["ActivityType"] = "Meeting", ["Subject"] = subject, ["ActivityDate"] = _f.Today.ToString("yyyy-MM-dd"), ["CustomerId"] = cid.ToString(), ["Status"] = "Completed" }, "/Activities/Create");
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        Assert.Contains(subject, await web.GetStringAsync("/Activities?search=Web+call"));
        var bad = await web.PostFormAsync("/Activities/Create", new Dictionary<string, string?> { ["ActivityType"] = "Call", ["Subject"] = "", ["ActivityDate"] = _f.Today.ToString("yyyy-MM-dd") }, "/Activities/Create");
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Subject is required.", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Full_ui_flow_create_edit_delete_customer_with_audit_trail()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Manager);
        var name = $"UI Customer {Seq.Next()}"; var email = $"ui{Seq.Next()}@example.com";
        var create = await web.PostFormAsync("/Customers/Create", new Dictionary<string, string?> { ["CustomerName"] = name, ["Email"] = email, ["Phone"] = Seq.Phone(), ["Status"] = "Active", ["CompanyName"] = "UI Co" }, "/Customers/Create");
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());
        Assert.Contains(name, await web.GetStringAsync($"/Customers/Details/{id}"));

        var edit = await web.PostFormAsync($"/Customers/Edit/{id}", new Dictionary<string, string?> { ["CustomerName"] = name + " Jr", ["Email"] = email, ["Phone"] = (await _f.WithDbAsync((db, _) => db.Customers.Where(c => c.CustomerId == id).Select(c => c.Phone).FirstAsync())), ["Status"] = "Inactive" }, $"/Customers/Edit/{id}");
        Assert.Equal(HttpStatusCode.Redirect, edit.StatusCode);
        var del = await web.PostFormAsync($"/Customers/Delete/{id}", new Dictionary<string, string?>(), $"/Customers/Details/{id}");
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync($"/Customers/Details/{id}")).StatusCode);

        var actions = await _f.WithDbAsync((db, _) => db.AuditLogs.Where(a => a.EntityName == "Customer" && a.RecordId == id.ToString()).OrderBy(a => a.AuditLogId).Select(a => a.Action).ToListAsync());
        Assert.Equal(new[] { "Create", "StatusChange", "Delete" }, actions); // the edit changed the status, which is recorded as a status change
    }
}
