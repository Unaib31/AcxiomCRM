using System.Net;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Tests;

public class ValidationTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public ValidationTests(AppFactory f) => _f = f;

    private async Task<ApiClient> Arjun() => await _f.NewApi().AsAsync(Creds.Arjun);
    private async Task<int> FirstCustomerOf(ApiClient api)
    {
        var (_, list) = await api.GetAsync("/api/customers?pageSize=1");
        return list.GetProperty("items")[0].GetProperty("customerId").GetInt32();
    }

    private static object Opp(int customerId, object? amount = null, object? prob = null, string? close = null, string stage = "Proposal") => new
    {
        opportunityName = $"Deal {Seq.Next()}", customerId, amount = amount ?? 50000, stage, probability = prob ?? 50, expectedCloseDate = close ?? DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"),
    };

    // ---------------------------------------------------------------- opportunity rules (spec 5.4)
    [Fact]
    public async Task Opportunity_amount_zero_is_rejected_with_the_specified_message()
    {
        var api = await Arjun(); var c = await FirstCustomerOf(api);
        var (s, b) = await api.PostAsync("/api/opportunities", Opp(c, amount: 0));
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("Opportunity Amount must be greater than 0.", ApiClient.Errors(b));
    }

    [Fact]
    public async Task Opportunity_negative_amount_is_rejected()
    {
        var api = await Arjun(); var c = await FirstCustomerOf(api);
        var (s, b) = await api.PostAsync("/api/opportunities", Opp(c, amount: -10));
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("Amount", ApiClient.Errors(b));
    }

    [Theory] [InlineData(101)] [InlineData(-1)]
    public async Task Opportunity_probability_outside_0_to_100_is_rejected(int p)
    {
        var api = await Arjun(); var c = await FirstCustomerOf(api);
        var (s, b) = await api.PostAsync("/api/opportunities", Opp(c, prob: p));
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("Probability must be between 0 and 100.", ApiClient.Errors(b));
    }

    [Theory] [InlineData(0)] [InlineData(100)]
    public async Task Opportunity_probability_boundaries_are_accepted(int p)
    {
        var api = await Arjun(); var c = await FirstCustomerOf(api);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/opportunities", Opp(c, prob: p))).Status);
    }

    [Fact]
    public async Task Opportunity_close_date_in_the_past_is_rejected_but_today_is_fine()
    {
        var api = await Arjun(); var c = await FirstCustomerOf(api);
        var yesterday = _f.Today.AddDays(-1).ToString("yyyy-MM-dd");
        var (s, b) = await api.PostAsync("/api/opportunities", Opp(c, close: yesterday));
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("Expected Close Date cannot be in the past.", ApiClient.Errors(b));
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/opportunities", Opp(c, close: _f.Today.ToString("yyyy-MM-dd")))).Status);
    }

    [Fact]
    public async Task Opportunity_required_fields_and_foreign_keys_are_validated()
    {
        var api = await Arjun();
        var (s, b) = await api.PostAsync("/api/opportunities", new { });
        Assert.Equal(HttpStatusCode.BadRequest, s);
        var e = ApiClient.Errors(b);
        Assert.Contains("Opportunity Name is required.", e); Assert.Contains("Customer is required.", e); Assert.Contains("Expected Close Date is required.", e);
        var (s2, b2) = await api.PostAsync("/api/opportunities", Opp(999999));
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("Select a valid customer.", ApiClient.Errors(b2));
    }

    [Fact]
    public async Task Opportunity_for_a_customer_outside_the_users_scope_is_rejected()
    {
        var priyaCustomer = await _f.WithDbAsync(async (db, _) => await (from c in db.Customers join u in db.Users on c.AssignedTo equals u.Id where u.Email == Creds.Priya select c.CustomerId).FirstAsync());
        var (s, _) = await (await Arjun()).PostAsync("/api/opportunities", Opp(priyaCustomer));
        Assert.Equal(HttpStatusCode.BadRequest, s);
    }

    [Fact]
    public async Task Opportunity_stage_value_must_be_a_real_stage_and_strings_only()
    {
        var api = await Arjun(); var c = await FirstCustomerOf(api);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/opportunities", Opp(c, stage: "Bogus"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/opportunities", new { opportunityName = "n", customerId = c, amount = 1, stage = 99, probability = 1, expectedCloseDate = _f.Today.ToString("yyyy-MM-dd") })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/opportunities", Opp(c, stage: "Won"))).Status); // cannot start closed
    }

    // ---------------------------------------------------------------- follow-up rules
    [Fact]
    public async Task Followup_dated_before_today_is_rejected_but_today_and_future_are_fine()
    {
        var api = await Arjun(); var c = await FirstCustomerOf(api);
        object Body(DateOnly d) => new { relatedType = "Customer", relatedId = c, followUpDate = d.ToString("yyyy-MM-dd"), subject = "Call back", followUpType = "Call", status = "Planned" };
        var (s, b) = await api.PostAsync("/api/followups", Body(_f.Today.AddDays(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("Follow-up date cannot be earlier than today.", ApiClient.Errors(b));
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/followups", Body(_f.Today))).Status);
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/followups", Body(_f.Today.AddDays(7)))).Status);
    }

    [Fact]
    public async Task Followup_requires_subject_valid_type_and_related_record()
    {
        var api = await Arjun(); var c = await FirstCustomerOf(api);
        var (s, b) = await api.PostAsync("/api/followups", new { });
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("Subject is required.", ApiClient.Errors(b));
        var (s2, _) = await api.PostAsync("/api/followups", new { relatedType = "Customer", relatedId = 987654, followUpDate = _f.Today.ToString("yyyy-MM-dd"), subject = "x", followUpType = "Call" });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        var (s3, _) = await api.PostAsync("/api/followups", new { relatedType = "Customer", relatedId = c, followUpDate = _f.Today.ToString("yyyy-MM-dd"), subject = new string('x', 201), followUpType = "Call" });
        Assert.Equal(HttpStatusCode.BadRequest, s3);
    }

    // ---------------------------------------------------------------- customer rules
    [Theory]
    [InlineData("user@", "Enter a valid email address.")] [InlineData("not-an-email", "Enter a valid email address.")] [InlineData("a b@c.com", "Enter a valid email address.")]
    public async Task Customer_invalid_email_is_rejected(string email, string message)
    {
        var (s, b) = await (await Arjun()).PostAsync("/api/customers", new { customerName = "X", email, phone = Seq.Phone(), status = "Active" });
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains(message, ApiClient.Errors(b));
    }

    [Theory] [InlineData("12345")] [InlineData("abcdefghij")] [InlineData("5876543210")] [InlineData("98765432101234")]
    public async Task Customer_invalid_phone_is_rejected(string phone)
    {
        var (s, b) = await (await Arjun()).PostAsync("/api/customers", new { customerName = "X", email = $"p{Seq.Next()}@example.com", phone, status = "Active" });
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.Contains("Enter a valid phone number.", ApiClient.Errors(b));
    }

    [Fact]
    public async Task Customer_required_and_length_rules()
    {
        var api = await Arjun();
        var (s, b) = await api.PostAsync("/api/customers", new { });
        Assert.Equal(HttpStatusCode.BadRequest, s);
        var e = ApiClient.Errors(b);
        Assert.Contains("Customer Name is required.", e); Assert.Contains("Email is required.", e); Assert.Contains("Phone is required.", e);
        var (s2, b2) = await api.PostAsync("/api/customers", new { customerName = new string('A', 151), email = $"l{Seq.Next()}@example.com", phone = Seq.Phone(), status = "Active" });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("cannot exceed 150 characters", ApiClient.Errors(b2));
        var (s3, _) = await api.PostAsync("/api/customers", new { customerName = "Ok", email = $"l{Seq.Next()}@example.com", phone = Seq.Phone(), status = "Active", notes = new string('n', 2001) });
        Assert.Equal(HttpStatusCode.BadRequest, s3);
    }

    [Fact]
    public async Task Duplicate_customers_are_rejected_by_email_phone_and_name_plus_company()
    {
        var api = await Arjun();
        var email = $"dup{Seq.Next()}@example.com"; var phone = Seq.Phone(); var name = $"Dup Person {Seq.Next()}";
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync("/api/customers", new { customerName = name, email, phone, companyName = "DupCo", status = "Active" })).Status);

        var (s1, b1) = await api.PostAsync("/api/customers", new { customerName = "Other", email = email.ToUpperInvariant(), phone = Seq.Phone(), status = "Active" });
        Assert.Equal(HttpStatusCode.Conflict, s1); Assert.Contains("email already exists", ApiClient.Errors(b1));
        var (s2, b2) = await api.PostAsync("/api/customers", new { customerName = "Other", email = $"x{Seq.Next()}@example.com", phone = "+91" + phone, status = "Active" });
        Assert.Equal(HttpStatusCode.Conflict, s2); Assert.Contains("phone number already exists", ApiClient.Errors(b2));
        var (s3, b3) = await api.PostAsync("/api/customers", new { customerName = name.ToUpperInvariant(), email = $"y{Seq.Next()}@example.com", phone = Seq.Phone(), companyName = "dupco", status = "Active" });
        Assert.Equal(HttpStatusCode.Conflict, s3); Assert.Contains("same name and company", ApiClient.Errors(b3));
        // duplicates are detected across the whole customer base, not just the caller's visible records
        var existingElsewhere = await _f.WithDbAsync(async (db, _) => await db.Customers.Where(c => c.AssignedTo != db.Users.Where(u => u.Email == Creds.Arjun).Select(u => u.Id).First()).Select(c => c.Email).FirstAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync("/api/customers", new { customerName = "Sneaky", email = existingElsewhere, phone = Seq.Phone(), status = "Active" })).Status);
    }

    [Fact]
    public async Task Customer_input_is_normalised_on_save()
    {
        var api = await Arjun();
        var (s, b) = await api.PostAsync("/api/customers", new { customerName = "  Spaces Person  ", email = $"  MiXed{Seq.Next()}@Example.COM ", phone = "+91-9" + Seq.Next().ToString("D9"), status = "Active" });
        Assert.Equal(HttpStatusCode.Created, s);
        Assert.Equal("Spaces Person", b.GetProperty("customerName").GetString());
        Assert.Equal(b.GetProperty("email").GetString(), b.GetProperty("email").GetString()!.ToLowerInvariant());
        Assert.Matches("^[6-9][0-9]{9}$", b.GetProperty("phone").GetString()!);
        Assert.StartsWith("CUS-", b.GetProperty("customerCode").GetString());
    }

    [Fact]
    public async Task Api_responses_never_expose_security_fields_or_internals()
    {
        var api = await Arjun();
        var (_, b) = await api.GetAsync("/api/customers?pageSize=5");
        var text = b.ToString();
        foreach (var banned in new[] { "passwordHash", "securityStamp", "concurrencyStamp", "isDeleted", "createdBy", "owner" })
            Assert.DoesNotContain(banned, text, StringComparison.OrdinalIgnoreCase);
        var (s, e) = await api.GetAsync("/api/customers/abc");
        Assert.Equal(HttpStatusCode.NotFound, s);
        Assert.DoesNotContain("Exception", e.ToString());
        Assert.DoesNotContain("   at ", e.ToString());
    }

    // ---------------------------------------------------------------- server side re-validation of crafted MVC requests
    [Fact]
    public async Task Crafted_browser_post_cannot_bypass_server_validation()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var name = $"Crafted {Seq.Next()}";
        var r = await web.PostFormAsync("/Customers/Create", new Dictionary<string, string?>
            { ["CustomerName"] = name, ["Email"] = "bad-email", ["Phone"] = "123", ["Status"] = "Active" }, "/Customers/Create");
        var html = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Enter a valid email address.", html);
        Assert.Contains("Enter a valid phone number.", html);
        Assert.False(await _f.WithDbAsync((db, _) => db.Customers.AnyAsync(c => c.CustomerName == name)));

        var status = await web.PostFormAsync("/Customers/Create", new Dictionary<string, string?>
            { ["CustomerName"] = name, ["Email"] = $"c{Seq.Next()}@example.com", ["Phone"] = Seq.Phone(), ["Status"] = "99" }, "/Customers/Create");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.False(await _f.WithDbAsync((db, _) => db.Customers.AnyAsync(c => c.CustomerName == name)));
    }

    [Fact]
    public async Task Crafted_browser_post_for_an_opportunity_is_rejected_by_business_rules()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var cid = await _f.WithDbAsync(async (db, _) => await (from c in db.Customers join u in db.Users on c.AssignedTo equals u.Id where u.Email == Creds.Arjun select c.CustomerId).FirstAsync());
        var name = $"Crafted Opp {Seq.Next()}";
        var r = await web.PostFormAsync("/Opportunities/Create", new Dictionary<string, string?>
        {
            ["OpportunityName"] = name, ["CustomerId"] = cid.ToString(), ["Amount"] = "0", ["Stage"] = "Proposal", ["Probability"] = "50",
            ["ExpectedCloseDate"] = _f.Today.AddDays(-3).ToString("yyyy-MM-dd"),
        }, "/Opportunities/Create");
        var html = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Opportunity Amount must be greater than 0.", html);
        Assert.Contains("Expected Close Date cannot be in the past.", html);
        Assert.False(await _f.WithDbAsync((db, _) => db.Opportunities.AnyAsync(o => o.OpportunityName == name)));
    }

    [Fact]
    public async Task Client_side_validation_attributes_are_rendered_on_forms()
    {
        var web = await _f.NewWeb().SignedInAsync(Creds.Arjun);
        var cust = await web.GetStringAsync("/Customers/Create");
        Assert.Contains("data-val-required=\"Customer Name is required.\"", cust);
        Assert.Contains("data-val-regex=\"Enter a valid email address.\"", cust);
        Assert.Contains("data-val-regex=\"Enter a valid phone number.\"", cust);
        Assert.Contains("data-val-length-max=\"150\"", cust);
        var opp = await web.GetStringAsync("/Opportunities/Create");
        Assert.Contains("data-val-crmpositive=\"Opportunity Amount must be greater than 0.\"", opp);
        Assert.Contains("data-val-range=\"Probability must be between 0 and 100.\"", opp);
        Assert.Contains("data-val-crmnotpast=\"Expected Close Date cannot be in the past.\"", opp);
        Assert.Contains($"data-val-crmnotpast-today=\"{_f.Today:yyyy-MM-dd}\"", opp);
        var fu = await web.GetStringAsync("/FollowUps/Create");
        Assert.Contains("data-val-crmnotpast=\"Follow-up date cannot be earlier than today.\"", fu);
    }

    [Fact]
    public async Task Paging_and_sorting_parameters_are_clamped_and_whitelisted()
    {
        var api = await _f.NewApi().AsAsync(Creds.Admin, Creds.AdminPw);
        var (s, b) = await api.GetAsync("/api/customers?pageSize=100000&page=-5&sort=DROP%20TABLE&desc=true");
        Assert.Equal(HttpStatusCode.OK, s);
        Assert.Equal(100, b.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, b.GetProperty("page").GetInt32());
        // LIKE wildcards in search terms are escaped rather than interpreted
        var (_, all) = await api.GetAsync("/api/customers?search=%25&pageSize=100");
        Assert.Equal(0, all.GetProperty("totalCount").GetInt32());
        var (_, injection) = await api.GetAsync("/api/customers?search=%27%20OR%201%3D1%20--");
        Assert.Equal(0, injection.GetProperty("totalCount").GetInt32());
    }
}

public class SingleResponseErrorTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public SingleResponseErrorTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Api_reports_format_and_business_errors_in_one_response()
    {
        var api = await _f.NewApi().AsAsync(Creds.Arjun);
        var (s, b) = await api.PostAsync("/api/opportunities", new { opportunityName = "", customerId = 1, amount = 0, stage = "Proposal", probability = 101, expectedCloseDate = _f.Today.AddDays(-1).ToString("yyyy-MM-dd") });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, s);
        var e = ApiClient.Errors(b);
        foreach (var m in new[] { "Opportunity Name is required.", "Opportunity Amount must be greater than 0.", "Probability must be between 0 and 100.", "Expected Close Date cannot be in the past." })
            Assert.Contains(m, e);
        var (s2, b2) = await api.PostAsync("/api/followups", new { relatedType = "Customer", relatedId = 1, followUpDate = _f.Today.AddDays(-1).ToString("yyyy-MM-dd"), subject = "", followUpType = "Call" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, s2);
        Assert.Contains("Subject is required.", ApiClient.Errors(b2));
        Assert.Contains("Follow-up date cannot be earlier than today.", ApiClient.Errors(b2));
    }
}
