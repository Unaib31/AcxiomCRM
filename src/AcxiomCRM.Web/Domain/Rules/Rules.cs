using System.Text.RegularExpressions;

namespace AcxiomCRM.Domain.Rules;

/// <summary>Central definition of the shared validation rules (client side mirrors these patterns).</summary>
public static partial class ValidationRules
{
    public const string EmailPattern = @"^\s*[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}\s*$";
    /// <summary>Indian mobile: 10 digits starting 6-9, optional +91 / 91 / 0 prefix.</summary>
    public const string PhonePattern = @"^\s*(\+91[\- ]?|91[\- ]?|0)?[6-9][0-9]{9}\s*$";

    public const string EmailMessage = "Enter a valid email address.";
    public const string PhoneMessage = "Enter a valid phone number.";

    [GeneratedRegex(EmailPattern)] private static partial Regex EmailRegex();
    [GeneratedRegex(PhonePattern)] private static partial Regex PhoneRegex();

    public static bool IsValidEmail(string? v) => !string.IsNullOrWhiteSpace(v) && v.Length <= 254 && EmailRegex().IsMatch(v.Trim());
    public static bool IsValidPhone(string? v) => !string.IsNullOrWhiteSpace(v) && PhoneRegex().IsMatch(v.Trim());

    public static string NormalizeEmail(string v) => v.Trim().ToLowerInvariant();

    /// <summary>Canonical stored form: the 10 national digits.</summary>
    public static string NormalizePhone(string v)
    {
        var digits = new string(v.Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    public static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

public static class LeadWorkflow
{
    private static readonly Dictionary<LeadStatus, LeadStatus[]> Transitions = new()
    {
        [LeadStatus.New] = new[] { LeadStatus.Contacted, LeadStatus.Unqualified, LeadStatus.Lost },
        [LeadStatus.Contacted] = new[] { LeadStatus.Qualified, LeadStatus.Unqualified, LeadStatus.Lost },
        [LeadStatus.Qualified] = new[] { LeadStatus.Contacted, LeadStatus.Converted, LeadStatus.Unqualified, LeadStatus.Lost },
        [LeadStatus.Unqualified] = new[] { LeadStatus.Contacted, LeadStatus.Lost },
        [LeadStatus.Converted] = Array.Empty<LeadStatus>(),
        [LeadStatus.Lost] = Array.Empty<LeadStatus>(),
    };

    public static bool CanTransition(LeadStatus from, LeadStatus to) => from == to || Transitions[from].Contains(to);

    /// <summary>Statuses selectable manually. Converted is only reachable through the conversion workflow.</summary>
    public static IEnumerable<LeadStatus> Allowed(LeadStatus from) =>
        new[] { from }.Concat(Transitions[from].Where(s => s != LeadStatus.Converted)).Distinct();

    public static bool IsOpen(LeadStatus s) => s is LeadStatus.New or LeadStatus.Contacted or LeadStatus.Qualified;
    public static bool IsClosed(LeadStatus s) => !IsOpen(s);
}

public static class OpportunityRules
{
    public const decimal MaxAmount = 9_999_999_999_999m;
    public static bool IsActive(OpportunityStage s) => s is not (OpportunityStage.Won or OpportunityStage.Lost);

    public static int DefaultProbability(OpportunityStage s) => s switch
    {
        OpportunityStage.Qualification => 20,
        OpportunityStage.Proposal => 50,
        OpportunityStage.Negotiation => 75,
        OpportunityStage.Won => 100,
        _ => 0,
    };

    /// <summary>Won/Lost are final. Active stages may move freely among themselves or close.</summary>
    public static bool CanTransition(OpportunityStage from, OpportunityStage to) =>
        from == to || IsActive(from);

    public static decimal Weighted(decimal amount, int probability) => Math.Round(amount * probability / 100m, 2);

    /// <summary>Returns field → message for each violated rule.</summary>
    public static Dictionary<string, string> Validate(decimal amount, int probability, DateOnly closeDate, OpportunityStage stage, DateOnly today)
    {
        var errors = new Dictionary<string, string>();
        if (amount < 0) errors["Amount"] = "Opportunity Amount cannot be negative.";
        else if ((IsActive(stage) || stage == OpportunityStage.Won) && amount <= 0) errors["Amount"] = "Opportunity Amount must be greater than 0.";
        else if (amount > MaxAmount) errors["Amount"] = "Opportunity Amount is too large.";
        if (probability is < 0 or > 100) errors["Probability"] = "Probability must be between 0 and 100.";
        if (IsActive(stage) && closeDate < today) errors["ExpectedCloseDate"] = "Expected Close Date cannot be in the past.";
        return errors;
    }
}

public static class FollowUpRules
{
    public static bool IsOverdue(FollowUpStatus status, DateOnly date, DateOnly today) => status == FollowUpStatus.Planned && date < today;

    public static bool CanTransition(FollowUpStatus from, FollowUpStatus to) =>
        from == to || from == FollowUpStatus.Planned || (from == FollowUpStatus.Missed && to is FollowUpStatus.Planned or FollowUpStatus.Completed);
}
