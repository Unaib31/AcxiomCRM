using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider sp, IConfiguration cfg, ILogger logger)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var roles = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        var defs = new[]
        {
            (RoleNames.Admin, "Full application administration, users, roles, audit logs, configuration, all CRM records and reports."),
            (RoleNames.Manager, "Team customer, lead, opportunity and follow-up management and management reports; no security administration."),
            (RoleNames.SalesExecutive, "Assigned customers, leads, opportunities, follow-ups and activities; permitted sales dashboards and reports."),
        };
        foreach (var (name, desc) in defs)
            if (!await roles.RoleExistsAsync(name)) await roles.CreateAsync(new ApplicationRole(name, desc));

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var adminEmail = cfg["Seed:AdminEmail"];
        var adminPassword = cfg["Seed:AdminPassword"];
        if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
        {
            await EnsureUserAsync(users, db, adminEmail, cfg["Seed:AdminName"] ?? "System Administrator", adminPassword, RoleNames.Admin, null, logger);
        }
        else if (!(await users.GetUsersInRoleAsync(RoleNames.Admin)).Any())
        {
            logger.LogWarning("No administrator exists. Set Seed:AdminEmail and Seed:AdminPassword (environment variables) for the first start.");
        }

        if (cfg.GetValue<bool>("Seed:DemoData") && !await db.Customers.IgnoreQueryFilters().AnyAsync())
            await DemoData.SeedAsync(sp, cfg, logger);
    }

    public static async Task<ApplicationUser> EnsureUserAsync(UserManager<ApplicationUser> users, AppDbContext db, string email, string fullName,
        string password, string role, string? managerId, ILogger logger)
    {
        email = email.Trim().ToLowerInvariant();
        var user = await users.FindByEmailAsync(email);
        if (user is not null) return user;
        user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = fullName, IsActive = true, ManagerId = managerId, CreatedDate = DateTime.UtcNow };
        var r = await users.CreateAsync(user, password);
        if (!r.Succeeded) throw new InvalidOperationException($"Could not seed user {email}: {string.Join("; ", r.Errors.Select(e => e.Description))}");
        await users.AddToRoleAsync(user, role);
        db.PasswordHistories.Add(new PasswordHistory { UserId = user.Id, PasswordHash = user.PasswordHash!, CreatedDate = DateTime.UtcNow });
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Role} account {Email}", role, email);
        return user;
    }
}

/// <summary>Development-only sample data so every screen has something to show.</summary>
public static class DemoData
{
    public static async Task SeedAsync(IServiceProvider sp, IConfiguration cfg, ILogger logger)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var pwd = cfg["Seed:DemoPassword"];
        if (string.IsNullOrWhiteSpace(pwd)) return;

        var manager = await DbInitializer.EnsureUserAsync(users, db, "manager@acxiom.local", "Meera Kapoor", pwd, RoleNames.Manager, null, logger);
        var s1 = await DbInitializer.EnsureUserAsync(users, db, "arjun@acxiom.local", "Arjun Mehta", pwd, RoleNames.SalesExecutive, manager.Id, logger);
        var s2 = await DbInitializer.EnsureUserAsync(users, db, "priya@acxiom.local", "Priya Nair", pwd, RoleNames.SalesExecutive, manager.Id, logger);
        var outsider = await DbInitializer.EnsureUserAsync(users, db, "rahul@acxiom.local", "Rahul Verma", pwd, RoleNames.SalesExecutive, null, logger);

        var admin = (await users.GetUsersInRoleAsync(RoleNames.Admin)).First();
        var clock = sp.GetRequiredService<AcxiomCRM.Application.Common.IClock>();
        var today = clock.Today;
        var now = DateTime.UtcNow;
        var owners = new[] { s1.Id, s2.Id, manager.Id, outsider.Id };
        var rnd = new Random(42);

        var customerData = new (string Name, string Company, string City, string State)[]
        {
            ("Ananya Sharma", "Sharma Textiles", "Mumbai", "Maharashtra"), ("Vikram Singh", "Singh Logistics", "Delhi", "Delhi"),
            ("Neha Gupta", "BrightPath Edu", "Bengaluru", "Karnataka"), ("Rohan Desai", "Desai Foods", "Pune", "Maharashtra"),
            ("Kavita Rao", "Rao Healthcare", "Hyderabad", "Telangana"), ("Sanjay Iyer", "Iyer Motors", "Chennai", "Tamil Nadu"),
            ("Pooja Bhatt", "Bhatt Interiors", "Ahmedabad", "Gujarat"), ("Amit Joshi", "Joshi Pharma", "Nagpur", "Maharashtra"),
            ("Divya Menon", "Menon Hospitality", "Kochi", "Kerala"), ("Karan Malhotra", "Malhotra Realty", "Gurugram", "Haryana"),
            ("Sneha Kulkarni", "Kulkarni Agro", "Nashik", "Maharashtra"), ("Manish Agarwal", "Agarwal Steel", "Kolkata", "West Bengal"),
        };
        var customers = new List<Customer>();
        for (var i = 0; i < customerData.Length; i++)
        {
            var c = customerData[i];
            customers.Add(new Customer
            {
                CustomerCode = "TMP-" + Guid.NewGuid().ToString("N"), CustomerName = c.Name, CompanyName = c.Company, City = c.City, State = c.State,
                Email = $"{c.Name.Split(' ')[0].ToLower()}@{c.Company.Replace(" ", "").ToLower()}.example.com",
                Phone = $"9{rnd.Next(100000000, 999999999)}", Address = $"{rnd.Next(1, 99)}, MG Road",
                Status = i % 5 == 4 ? CustomerStatus.Prospect : CustomerStatus.Active, AssignedTo = owners[i % 3], CreatedBy = admin.Id,
                CreatedDate = now.AddDays(-rnd.Next(5, 150)), ModifiedDate = now.AddDays(-rnd.Next(0, 5)),
                Notes = i % 3 == 0 ? "Key account. Prefers email communication." : null,
            });
        }
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();

        var leadData = new (string Name, string Company, LeadStatus Status, LeadSource Src, LeadPriority P, decimal V)[]
        {
            ("Ishaan Verma", "Verma Exports", LeadStatus.New, LeadSource.Website, LeadPriority.High, 450000), ("Tanya Bose", "Bose Analytics", LeadStatus.New, LeadSource.Referral, LeadPriority.Medium, 220000),
            ("Harsh Vardhan", "HV Constructions", LeadStatus.Contacted, LeadSource.ColdCall, LeadPriority.Medium, 780000), ("Meghna Roy", "Roy Pharma", LeadStatus.Contacted, LeadSource.Event, LeadPriority.High, 1200000),
            ("Zoya Khan", "Khan Fashion", LeadStatus.Qualified, LeadSource.SocialMedia, LeadPriority.High, 640000), ("Dev Patel", "Patel Plastics", LeadStatus.Qualified, LeadSource.EmailCampaign, LeadPriority.Medium, 310000),
            ("Rhea Chopra", "Chopra Wellness", LeadStatus.Unqualified, LeadSource.Website, LeadPriority.Low, 90000), ("Nikhil Jain", "Jain Traders", LeadStatus.Lost, LeadSource.Partner, LeadPriority.Low, 150000),
            ("Aditi Sen", "Sen Publishing", LeadStatus.Converted, LeadSource.Referral, LeadPriority.Medium, 380000), ("Gaurav Mishra", "Mishra Tech", LeadStatus.Converted, LeadSource.Website, LeadPriority.High, 900000),
            ("Lakshmi Pillai", "Pillai Spices", LeadStatus.New, LeadSource.Event, LeadPriority.Medium, 175000), ("Farhan Ali", "Ali Auto Parts", LeadStatus.Contacted, LeadSource.Partner, LeadPriority.Low, 260000),
            ("Simran Kaur", "Kaur Dairy", LeadStatus.Qualified, LeadSource.Referral, LeadPriority.High, 540000), ("Yash Thakur", "Thakur Logistics", LeadStatus.New, LeadSource.ColdCall, LeadPriority.Medium, 330000),
        };
        var leads = new List<Lead>();
        for (var i = 0; i < leadData.Length; i++)
        {
            var l = leadData[i];
            leads.Add(new Lead
            {
                LeadCode = "TMP-" + Guid.NewGuid().ToString("N"), LeadName = l.Name, CompanyName = l.Company, Status = l.Status, Source = l.Src, Priority = l.P, ExpectedValue = l.V,
                Email = $"{l.Name.Split(' ')[0].ToLower()}@{l.Company.Replace(" ", "").ToLower()}.example.com", Phone = $"8{rnd.Next(100000000, 999999999)}",
                AssignedTo = owners[i % 3], CreatedBy = admin.Id, CreatedDate = now.AddDays(-rnd.Next(2, 120)), ModifiedDate = now.AddDays(-rnd.Next(0, 4)),
                ConvertedDate = l.Status == LeadStatus.Converted ? now.AddDays(-rnd.Next(5, 40)) : null,
                ConvertedCustomerId = l.Status == LeadStatus.Converted ? customers[i % customers.Count].CustomerId : null,
            });
        }
        db.Leads.AddRange(leads);
        await db.SaveChangesAsync();

        var oppNames = new[] { "ERP Rollout", "Annual Support Contract", "Cloud Migration", "Branch Expansion CRM", "Analytics Suite", "Mobile App Build", "Security Audit", "Data Warehouse", "Website Revamp", "Training Programme", "POS Integration", "Compliance Toolkit", "Inventory Automation", "Customer Portal" };
        var stages = new[] { OpportunityStage.Qualification, OpportunityStage.Proposal, OpportunityStage.Negotiation, OpportunityStage.Won, OpportunityStage.Lost,
            OpportunityStage.Proposal, OpportunityStage.Negotiation, OpportunityStage.Won, OpportunityStage.Won, OpportunityStage.Qualification, OpportunityStage.Lost, OpportunityStage.Negotiation, OpportunityStage.Won, OpportunityStage.Proposal };
        var opps = new List<Opportunity>();
        for (var i = 0; i < oppNames.Length; i++)
        {
            var cust = customers[i % customers.Count];
            var stage = stages[i];
            var active = stage is not (OpportunityStage.Won or OpportunityStage.Lost);
            var closed = active ? (DateTime?)null : now.AddDays(-rnd.Next(5, 150));
            opps.Add(new Opportunity
            {
                OpportunityCode = "TMP-" + Guid.NewGuid().ToString("N"), OpportunityName = $"{cust.CompanyName} – {oppNames[i]}", CustomerId = cust.CustomerId,
                Amount = rnd.Next(8, 120) * 10000m, Stage = stage, Probability = AcxiomCRM.Domain.Rules.OpportunityRules.DefaultProbability(stage),
                ExpectedCloseDate = active ? today.AddDays(rnd.Next(7, 90)) : DateOnly.FromDateTime(closed!.Value),
                Source = (LeadSource)(i % 8), AssignedTo = cust.AssignedTo, CreatedBy = admin.Id, ClosedDate = closed,
                CreatedDate = (closed ?? now).AddDays(-rnd.Next(10, 60)), ModifiedDate = closed ?? now.AddDays(-rnd.Next(0, 6)),
                OutcomeNotes = stage == OpportunityStage.Won ? "Signed after final commercial review." : stage == OpportunityStage.Lost ? "Lost to a competitor on price." : null,
            });
        }
        db.Opportunities.AddRange(opps);
        await db.SaveChangesAsync();

        var subjects = new[] { "Intro call", "Send proposal", "Demo walkthrough", "Contract review", "Pricing discussion", "Follow up on quote", "Onsite meeting", "Renewal check-in", "Requirement workshop", "Reference call" };
        for (var i = 0; i < subjects.Length; i++)
        {
            var status = i < 6 ? FollowUpStatus.Planned : i < 8 ? FollowUpStatus.Completed : i == 8 ? FollowUpStatus.Missed : FollowUpStatus.Cancelled;
            var date = i < 2 ? today.AddDays(-rnd.Next(1, 5)) : i < 6 ? today.AddDays(rnd.Next(0, 10)) : today.AddDays(-rnd.Next(2, 20));
            var cust = customers[i % customers.Count];
            db.FollowUps.Add(new FollowUp
            {
                CustomerId = i % 2 == 0 ? cust.CustomerId : null, LeadId = i % 2 == 1 ? leads[i % leads.Count].LeadId : null, FollowUpDate = date, Subject = subjects[i],
                FollowUpType = (FollowUpType)(i % 4), Status = status, AssignedTo = i % 2 == 0 ? cust.AssignedTo : leads[i % leads.Count].AssignedTo, CreatedBy = admin.Id,
                CreatedDate = now.AddDays(-20), ModifiedDate = now, CompletedDate = status == FollowUpStatus.Completed ? now.AddDays(-1) : null,
            });
        }
        for (var i = 0; i < 8; i++)
        {
            var cust = customers[i % customers.Count];
            db.Activities.Add(new Activity
            {
                ActivityType = (ActivityType)(i % 4), Subject = new[] { "Discovery call", "Kick-off meeting", "Sent brochure", "Prepare quotation", "Check-in call", "Product demo", "Intro email", "Prepare contract" }[i],
                Description = "Logged during demo data seeding.", ActivityDate = today.AddDays(-rnd.Next(0, 25)), CustomerId = cust.CustomerId,
                Status = i % 3 == 0 ? ActivityStatus.Planned : ActivityStatus.Completed, AssignedTo = cust.AssignedTo, CreatedBy = admin.Id, CreatedDate = now,
            });
        }
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded demo data");
    }
}
