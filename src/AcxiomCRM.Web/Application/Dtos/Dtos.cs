using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Rules;
using AcxiomCRM.Infrastructure;

namespace AcxiomCRM.Application.Dtos;

// ---------------------------------------------------------------- customers
/// <summary>Create/update payload. Used by both the MVC forms and the REST API.</summary>
public class CustomerInput
{
    [Required(ErrorMessage = "Customer Name is required."), StringLength(150, ErrorMessage = "Customer Name cannot exceed 150 characters."), Display(Name = "Customer Name")]
    public string? CustomerName { get; set; }

    [Required(ErrorMessage = "Email is required."), StringLength(254, ErrorMessage = "Email cannot exceed 254 characters."),
     RegularExpression(ValidationRules.EmailPattern, ErrorMessage = ValidationRules.EmailMessage)]
    public string? Email { get; set; }

    [Required(ErrorMessage = "Phone is required."), StringLength(20, ErrorMessage = "Phone cannot exceed 20 characters."),
     RegularExpression(ValidationRules.PhonePattern, ErrorMessage = ValidationRules.PhoneMessage)]
    public string? Phone { get; set; }

    [StringLength(150, ErrorMessage = "Company Name cannot exceed 150 characters."), Display(Name = "Company")]
    public string? CompanyName { get; set; }
    [StringLength(300, ErrorMessage = "Address cannot exceed 300 characters.")]
    public string? Address { get; set; }
    [StringLength(100, ErrorMessage = "City cannot exceed 100 characters.")]
    public string? City { get; set; }
    [StringLength(100, ErrorMessage = "State cannot exceed 100 characters.")]
    public string? State { get; set; }

    [EnumDataType(typeof(CustomerStatus), ErrorMessage = "Select a valid status.")]
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters.")]
    public string? Notes { get; set; }

    [Display(Name = "Assigned To")]
    public string? AssignedTo { get; set; }
}

public record CustomerDto(int CustomerId, string CustomerCode, string CustomerName, string Email, string Phone,
    string? CompanyName, string? Address, string? City, string? State, string Status, string? Notes,
    string AssignedTo, string? AssignedToName, DateTime CreatedDate, DateTime ModifiedDate);

// ---------------------------------------------------------------- leads
public class LeadInput
{
    [Required(ErrorMessage = "Lead Name is required."), StringLength(150, ErrorMessage = "Lead Name cannot exceed 150 characters."), Display(Name = "Lead Name")]
    public string? LeadName { get; set; }

    [StringLength(254, ErrorMessage = "Email cannot exceed 254 characters."),
     RegularExpression(ValidationRules.EmailPattern, ErrorMessage = ValidationRules.EmailMessage)]
    public string? Email { get; set; }

    [StringLength(20, ErrorMessage = "Phone cannot exceed 20 characters."),
     RegularExpression(ValidationRules.PhonePattern, ErrorMessage = ValidationRules.PhoneMessage)]
    public string? Phone { get; set; }

    [StringLength(150, ErrorMessage = "Company Name cannot exceed 150 characters."), Display(Name = "Company")]
    public string? CompanyName { get; set; }

    [EnumDataType(typeof(LeadSource), ErrorMessage = "Select a valid lead source.")]
    public LeadSource Source { get; set; } = LeadSource.Website;

    [EnumDataType(typeof(LeadStatus), ErrorMessage = "Select a valid lead status.")]
    public LeadStatus Status { get; set; } = LeadStatus.New;

    [EnumDataType(typeof(LeadPriority), ErrorMessage = "Select a valid priority.")]
    public LeadPriority Priority { get; set; } = LeadPriority.Medium;

    [Range(typeof(decimal), "0", "9999999999", ErrorMessage = "Expected Value must be between 0 and 9,999,999,999."), Display(Name = "Expected Value")]
    public decimal ExpectedValue { get; set; }

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters.")]
    public string? Notes { get; set; }

    [Display(Name = "Assigned To")]
    public string? AssignedTo { get; set; }
}

public record LeadDto(int LeadId, string LeadCode, string LeadName, string? Email, string? Phone, string? CompanyName,
    string Source, string Status, string Priority, decimal ExpectedValue, string? Notes, string AssignedTo, string? AssignedToName,
    DateTime CreatedDate, int? ConvertedCustomerId, int? ConvertedOpportunityId, DateTime? ConvertedDate);

public class LeadStatusInput
{
    [EnumDataType(typeof(LeadStatus), ErrorMessage = "Select a valid lead status.")]
    public LeadStatus Status { get; set; }
}

public class LeadConversionInput
{
    /// <summary>When set, the lead is linked to this existing customer instead of creating a new one.</summary>
    public int? ExistingCustomerId { get; set; }
    public bool CreateOpportunity { get; set; } = true;

    [StringLength(200, ErrorMessage = "Opportunity Name cannot exceed 200 characters."), Display(Name = "Opportunity Name")]
    public string? OpportunityName { get; set; }

    [Range(typeof(decimal), "0", "9999999999999", ErrorMessage = "Opportunity Amount must be greater than 0.")]
    public decimal? Amount { get; set; }

    [Range(0, 100, ErrorMessage = "Probability must be between 0 and 100.")]
    public int? Probability { get; set; }

    [Display(Name = "Expected Close Date")]
    public DateOnly? ExpectedCloseDate { get; set; }
}

// ---------------------------------------------------------------- opportunities
public class OpportunityInput
{
    [Required(ErrorMessage = "Opportunity Name is required."), StringLength(200, ErrorMessage = "Opportunity Name cannot exceed 200 characters."), Display(Name = "Opportunity Name")]
    public string? OpportunityName { get; set; }

    [Required(ErrorMessage = "Customer is required."), Display(Name = "Customer")]
    public int? CustomerId { get; set; }

    [Required(ErrorMessage = "Opportunity Amount is required."), Display(Name = "Amount")]
    [ClientPositive("Opportunity Amount must be greater than 0.", UnlessField = nameof(Stage), UnlessValues = "Won,Lost")]
    public decimal? Amount { get; set; }

    [EnumDataType(typeof(OpportunityStage), ErrorMessage = "Select a valid stage.")]
    public OpportunityStage Stage { get; set; } = OpportunityStage.Qualification;

    [Required(ErrorMessage = "Probability is required."), Range(0, 100, ErrorMessage = "Probability must be between 0 and 100.")]
    public int? Probability { get; set; }

    [Required(ErrorMessage = "Expected Close Date is required."), Display(Name = "Expected Close Date")]
    [ClientNotInPast("Expected Close Date cannot be in the past.", UnlessField = nameof(Stage), UnlessValues = "Won,Lost")]
    public DateOnly? ExpectedCloseDate { get; set; }

    [EnumDataType(typeof(LeadSource), ErrorMessage = "Select a valid source.")]
    public LeadSource? Source { get; set; }

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters.")]
    public string? Notes { get; set; }

    [StringLength(1000, ErrorMessage = "Outcome notes cannot exceed 1000 characters."), Display(Name = "Outcome Notes")]
    public string? OutcomeNotes { get; set; }

    [Display(Name = "Owner")]
    public string? AssignedTo { get; set; }
}

public record OpportunityDto(int OpportunityId, string OpportunityCode, string OpportunityName, int CustomerId, string? CustomerName,
    int? LeadId, decimal Amount, string Stage, int Probability, decimal WeightedAmount, DateOnly ExpectedCloseDate,
    string? Source, string? Notes, string? OutcomeNotes, DateTime? ClosedDate, string AssignedTo, string? AssignedToName, DateTime CreatedDate);

public class StageChangeInput
{
    [EnumDataType(typeof(OpportunityStage), ErrorMessage = "Select a valid stage.")]
    public OpportunityStage Stage { get; set; }
    [StringLength(1000, ErrorMessage = "Outcome notes cannot exceed 1000 characters.")]
    public string? OutcomeNotes { get; set; }
}

// ---------------------------------------------------------------- follow-ups
public class FollowUpInput
{
    [Required(ErrorMessage = "Related record type is required."), EnumDataType(typeof(RelatedType), ErrorMessage = "Select a valid related record type.")]
    public RelatedType? RelatedType { get; set; }

    [Required(ErrorMessage = "Related record is required."), Display(Name = "Related Record")]
    public int? RelatedId { get; set; }

    [Required(ErrorMessage = "Follow-Up Date is required."), Display(Name = "Follow-Up Date")]
    [ClientNotInPast("Follow-up date cannot be earlier than today.", UnlessField = nameof(Status), UnlessValues = "Completed,Cancelled")]
    public DateOnly? FollowUpDate { get; set; }

    [Required(ErrorMessage = "Subject is required."), StringLength(200, ErrorMessage = "Subject cannot exceed 200 characters.")]
    public string? Subject { get; set; }

    [EnumDataType(typeof(FollowUpType), ErrorMessage = "Select a valid follow-up type."), Display(Name = "Type")]
    public FollowUpType FollowUpType { get; set; } = FollowUpType.Call;

    [StringLength(2000, ErrorMessage = "Remarks cannot exceed 2000 characters.")]
    public string? Remarks { get; set; }

    [EnumDataType(typeof(FollowUpStatus), ErrorMessage = "Select a valid status.")]
    public FollowUpStatus Status { get; set; } = FollowUpStatus.Planned;

    [Display(Name = "Assigned To")]
    public string? AssignedTo { get; set; }
}

public record FollowUpDto(int FollowUpId, string RelatedType, int RelatedId, string? RelatedName, DateOnly FollowUpDate, string Subject,
    string FollowUpType, string? Remarks, string Status, bool IsOverdue, string AssignedTo, string? AssignedToName, DateTime? CompletedDate, DateTime CreatedDate);

// ---------------------------------------------------------------- activities
public class ActivityInput
{
    [EnumDataType(typeof(ActivityType), ErrorMessage = "Select a valid activity type."), Display(Name = "Type")]
    public ActivityType ActivityType { get; set; } = ActivityType.Call;

    [Required(ErrorMessage = "Subject is required."), StringLength(200, ErrorMessage = "Subject cannot exceed 200 characters.")]
    public string? Subject { get; set; }

    [StringLength(2000, ErrorMessage = "Description cannot exceed 2000 characters.")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Activity Date is required."), Display(Name = "Activity Date")]
    public DateOnly? ActivityDate { get; set; }

    [Display(Name = "Customer")]
    public int? CustomerId { get; set; }
    [Display(Name = "Lead")]
    public int? LeadId { get; set; }

    [EnumDataType(typeof(ActivityStatus), ErrorMessage = "Select a valid status.")]
    public ActivityStatus Status { get; set; } = ActivityStatus.Planned;

    [Display(Name = "Assigned To")]
    public string? AssignedTo { get; set; }
}

public record ActivityDto(int ActivityId, string ActivityType, string Subject, string? Description, DateOnly ActivityDate,
    int? CustomerId, string? CustomerName, int? LeadId, string? LeadName, string Status, string AssignedTo, string? AssignedToName);

// ---------------------------------------------------------------- misc
public class LoginRequest
{
    [Required(ErrorMessage = "Email or username is required."), StringLength(256)]
    public string? UserName { get; set; }
    [Required(ErrorMessage = "Password is required."), StringLength(128)]
    public string? Password { get; set; }
}

public record LoginResponse(string AccessToken, string TokenType, DateTime ExpiresUtc, string UserName, string FullName, string Role);
