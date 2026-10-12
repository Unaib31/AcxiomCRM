using Microsoft.AspNetCore.Identity;

namespace AcxiomCRM.Domain.Entities;

/// <summary>Record that belongs to one sales user. Drives role/ownership based scoping.</summary>
public interface IOwnedRecord { string AssignedTo { get; set; } }

/// <summary>Entities whose create/update/delete operations are written to the audit log automatically.</summary>
public interface IAuditable { }

/// <summary>Entities with a human readable code (CUS-000001) derived from the key after insert.</summary>
public interface IHasCode { string Code { get; set; } }

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    /// <summary>Team lead. Managers see records owned by users that report to them.</summary>
    public string? ManagerId { get; set; }
    public ApplicationUser? Manager { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginDate { get; set; }
}

public class ApplicationRole : IdentityRole
{
    public ApplicationRole() { }
    public ApplicationRole(string name, string description) : base(name) { Description = description; }
    public string Description { get; set; } = string.Empty;
}

public class Customer : IOwnedRecord, IAuditable, IHasCode
{
    public int CustomerId { get; set; }
    public string CustomerCode { get; set; } = string.Empty;
    string IHasCode.Code { get => CustomerCode; set => CustomerCode = value; }
    public string CustomerName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;
    public string? Notes { get; set; }
    public string AssignedTo { get; set; } = string.Empty;
    public ApplicationUser? Owner { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedDate { get; set; }
}

public class Lead : IOwnedRecord, IAuditable, IHasCode
{
    public int LeadId { get; set; }
    public string LeadCode { get; set; } = string.Empty;
    string IHasCode.Code { get => LeadCode; set => LeadCode = value; }
    public string LeadName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? CompanyName { get; set; }
    public LeadSource Source { get; set; } = LeadSource.Website;
    public LeadStatus Status { get; set; } = LeadStatus.New;
    public LeadPriority Priority { get; set; } = LeadPriority.Medium;
    public decimal ExpectedValue { get; set; }
    public string? Notes { get; set; }
    public string AssignedTo { get; set; } = string.Empty;
    public ApplicationUser? Owner { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public int? ConvertedCustomerId { get; set; }
    public Customer? ConvertedCustomer { get; set; }
    public int? ConvertedOpportunityId { get; set; }
    public DateTime? ConvertedDate { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedDate { get; set; }
}

public class Opportunity : IOwnedRecord, IAuditable, IHasCode
{
    public int OpportunityId { get; set; }
    public string OpportunityCode { get; set; } = string.Empty;
    string IHasCode.Code { get => OpportunityCode; set => OpportunityCode = value; }
    public string OpportunityName { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    public decimal Amount { get; set; }
    public OpportunityStage Stage { get; set; } = OpportunityStage.Qualification;
    public int Probability { get; set; }
    public DateOnly ExpectedCloseDate { get; set; }
    public LeadSource? Source { get; set; }
    public string? Notes { get; set; }
    /// <summary>Final outcome note captured when the opportunity is Won or Lost.</summary>
    public string? OutcomeNotes { get; set; }
    public DateTime? ClosedDate { get; set; }
    public string AssignedTo { get; set; } = string.Empty;
    public ApplicationUser? Owner { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedDate { get; set; }

    public bool IsActive => Stage is not (OpportunityStage.Won or OpportunityStage.Lost);
    public decimal WeightedAmount => Math.Round(Amount * Probability / 100m, 2);
}

public class FollowUp : IOwnedRecord, IAuditable
{
    public int FollowUpId { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    public int? OpportunityId { get; set; }
    public Opportunity? Opportunity { get; set; }
    public DateOnly FollowUpDate { get; set; }
    public string Subject { get; set; } = string.Empty;
    public FollowUpType FollowUpType { get; set; } = FollowUpType.Call;
    public string? Remarks { get; set; }
    public FollowUpStatus Status { get; set; } = FollowUpStatus.Planned;
    /// <summary>The assigned user (spec: AssignedTo / AssignedUserId).</summary>
    public string AssignedTo { get; set; } = string.Empty;
    public ApplicationUser? Owner { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? CompletedDate { get; set; }
    public int RescheduleCount { get; set; }
}

public class Activity : IOwnedRecord, IAuditable
{
    public int ActivityId { get; set; }
    public ActivityType ActivityType { get; set; } = ActivityType.Call;
    public string Subject { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly ActivityDate { get; set; }
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    public ActivityStatus Status { get; set; } = ActivityStatus.Planned;
    public string AssignedTo { get; set; } = string.Empty;
    public ApplicationUser? Owner { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>Append-only audit trail. Updates and deletes are rejected by the DbContext and by database triggers.</summary>
public class AuditLog
{
    public long AuditLogId { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string Module { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? EntityName { get; set; }
    public string? RecordId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Result { get; set; } = "Success";
    public string? Details { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
}

/// <summary>Hashes of recent passwords so reuse can be blocked. Stores hashes only.</summary>
public class PasswordHistory
{
    public int PasswordHistoryId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}

/// <summary>JWT ids revoked at logout.</summary>
public class RevokedToken
{
    public string TokenId { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
}
