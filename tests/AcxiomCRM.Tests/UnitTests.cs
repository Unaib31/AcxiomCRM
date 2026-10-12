using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Rules;
using AcxiomCRM.Infrastructure;

namespace AcxiomCRM.Tests;

public class DomainRuleTests
{
    [Theory]
    [InlineData("9876543210", true)] [InlineData(" 9876543210 ", true)] [InlineData("+919876543210", true)] [InlineData("+91-9876543210", true)] [InlineData("09876543210", true)]
    [InlineData("5876543210", false)] [InlineData("98765", false)] [InlineData("98765432101", false)] [InlineData("abcdefghij", false)] [InlineData("", false)]
    [InlineData("98765 43210", false)]
    public void Phone_rule(string v, bool ok) => Assert.Equal(ok, ValidationRules.IsValidPhone(v));

    [Theory]
    [InlineData("user@example.com", true)] [InlineData("  user@example.com ", true)] [InlineData("a.b+c@sub.example.co.in", true)]
    [InlineData("user@example", false)] [InlineData("user@", false)] [InlineData("@example.com", false)] [InlineData("us er@example.com", false)] [InlineData("", false)]
    public void Email_rule(string v, bool ok) => Assert.Equal(ok, ValidationRules.IsValidEmail(v));

    [Fact] public void Phone_is_normalised_to_ten_digits() => Assert.Equal("9876543210", ValidationRules.NormalizePhone("+91-9876543210"));
    [Fact] public void Email_is_normalised() => Assert.Equal("a@b.com", ValidationRules.NormalizeEmail("  A@B.com "));

    [Theory]
    [InlineData(LeadStatus.New, LeadStatus.Contacted, true)] [InlineData(LeadStatus.New, LeadStatus.Qualified, false)]
    [InlineData(LeadStatus.New, LeadStatus.Converted, false)] [InlineData(LeadStatus.Contacted, LeadStatus.Qualified, true)]
    [InlineData(LeadStatus.Qualified, LeadStatus.Converted, true)] [InlineData(LeadStatus.Converted, LeadStatus.New, false)]
    [InlineData(LeadStatus.Lost, LeadStatus.Contacted, false)] [InlineData(LeadStatus.Unqualified, LeadStatus.Contacted, true)]
    public void Lead_workflow(LeadStatus from, LeadStatus to, bool ok) => Assert.Equal(ok, LeadWorkflow.CanTransition(from, to));

    [Fact] public void Converted_is_never_manually_selectable() => Assert.DoesNotContain(LeadStatus.Converted, LeadWorkflow.Allowed(LeadStatus.Qualified));

    private static readonly DateOnly Today = new(2026, 10, 12);

    [Fact] public void Amount_zero_rejected_for_active_opportunity()
        => Assert.Equal("Opportunity Amount must be greater than 0.", OpportunityRules.Validate(0, 50, Today, OpportunityStage.Proposal, Today)["Amount"]);
    [Fact] public void Negative_amount_rejected_even_when_closed()
        => Assert.True(OpportunityRules.Validate(-1, 0, Today, OpportunityStage.Lost, Today).ContainsKey("Amount"));
    [Fact] public void Won_requires_positive_amount()
        => Assert.True(OpportunityRules.Validate(0, 100, Today, OpportunityStage.Won, Today).ContainsKey("Amount"));
    [Theory] [InlineData(-1)] [InlineData(101)] public void Probability_out_of_range(int p)
        => Assert.Equal("Probability must be between 0 and 100.", OpportunityRules.Validate(10, p, Today, OpportunityStage.Proposal, Today)["Probability"]);
    [Theory] [InlineData(0)] [InlineData(100)] public void Probability_bounds_inclusive(int p)
        => Assert.Empty(OpportunityRules.Validate(10, p, Today, OpportunityStage.Proposal, Today));
    [Fact] public void Past_close_date_rejected_when_active()
        => Assert.Equal("Expected Close Date cannot be in the past.", OpportunityRules.Validate(10, 10, Today.AddDays(-1), OpportunityStage.Negotiation, Today)["ExpectedCloseDate"]);
    [Fact] public void Past_close_date_allowed_when_closed()
        => Assert.Empty(OpportunityRules.Validate(10, 100, Today.AddDays(-30), OpportunityStage.Won, Today));
    [Fact] public void Today_close_date_is_allowed()
        => Assert.Empty(OpportunityRules.Validate(10, 10, Today, OpportunityStage.Proposal, Today));
    [Fact] public void Weighted_amount_formula() => Assert.Equal(25000m, OpportunityRules.Weighted(100000m, 25));
    [Fact] public void Closed_opportunities_are_final()
    {
        Assert.False(OpportunityRules.CanTransition(OpportunityStage.Won, OpportunityStage.Proposal));
        Assert.True(OpportunityRules.CanTransition(OpportunityStage.Proposal, OpportunityStage.Won));
    }

    [Fact] public void Followup_overdue_only_when_planned_and_past()
    {
        Assert.True(FollowUpRules.IsOverdue(FollowUpStatus.Planned, Today.AddDays(-1), Today));
        Assert.False(FollowUpRules.IsOverdue(FollowUpStatus.Planned, Today, Today));
        Assert.False(FollowUpRules.IsOverdue(FollowUpStatus.Completed, Today.AddDays(-5), Today));
    }
}

public class PresentationTests
{
    [Theory]
    [InlineData(450000, "₹4,50,000")] [InlineData(3610000, "₹36,10,000")] [InlineData(999, "₹999")] [InlineData(12345678, "₹1,23,45,678")] [InlineData(0, "₹0")]
    public void Indian_grouping(decimal v, string expected) => Assert.Equal(expected, Ui.Money(v));

    [Theory]
    [InlineData("hello", "\"hello\"")] [InlineData("=1+1", "\"'=1+1\"")] [InlineData("+cmd", "\"'+cmd\"")] [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    public void Csv_cells_are_quoted_and_formula_safe(string v, string expected) => Assert.Equal(expected, ReportService.Csv(v));
}
