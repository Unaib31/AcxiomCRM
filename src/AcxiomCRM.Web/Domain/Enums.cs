using System.ComponentModel.DataAnnotations;

namespace AcxiomCRM.Domain;

public static class RoleNames
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string SalesExecutive = "SalesExecutive";

    public static readonly string[] All = { Admin, Manager, SalesExecutive };

    public static string Display(string role) => role == SalesExecutive ? "Sales Executive" : role;
}

public enum CustomerStatus { Active, Inactive, Prospect }

public enum LeadStatus { New, Contacted, Qualified, Unqualified, Converted, Lost }

public enum LeadPriority { Low, Medium, High }

public enum LeadSource
{
    Website, Referral,
    [Display(Name = "Cold Call")] ColdCall,
    [Display(Name = "Email Campaign")] EmailCampaign,
    [Display(Name = "Social Media")] SocialMedia,
    Event, Partner, Other
}

public enum OpportunityStage { Qualification, Proposal, Negotiation, Won, Lost }

public enum FollowUpStatus { Planned, Completed, Missed, Cancelled }

public enum FollowUpType { Call, Meeting, Email, Task }

public enum ActivityType { Call, Meeting, Email, Task }

public enum ActivityStatus { Planned, Completed, Cancelled }

public enum RelatedType { Customer, Lead, Opportunity }
