using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Application.Services;

namespace AcxiomCRM.ViewModels;

public class ListViewModel<TItem, TQuery>
{
    public PagedResult<TItem> Result { get; set; } = new();
    public TQuery Query { get; set; } = default!;
    public IReadOnlyList<UserOption> Owners { get; set; } = Array.Empty<UserOption>();
}

public class LoginViewModel
{
    [Required(ErrorMessage = "Email or username is required."), StringLength(256), Display(Name = "Email")]
    public string? UserName { get; set; }
    [Required(ErrorMessage = "Password is required."), StringLength(128), DataType(DataType.Password)]
    public string? Password { get; set; }
    public string? ReturnUrl { get; set; }
}

public class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Current password is required."), DataType(DataType.Password), StringLength(128), Display(Name = "Current Password")]
    public string? CurrentPassword { get; set; }
    [Required(ErrorMessage = "New password is required."), DataType(DataType.Password), StringLength(128), Display(Name = "New Password")]
    public string? NewPassword { get; set; }
    [Required(ErrorMessage = "Please confirm the new password."), DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match."), Display(Name = "Confirm New Password")]
    public string? ConfirmPassword { get; set; }
}

public class ResetPasswordViewModel
{
    [Required] public string? UserId { get; set; }
    [Required] public string? Token { get; set; }
    [Required(ErrorMessage = "New password is required."), DataType(DataType.Password), StringLength(128), Display(Name = "New Password")]
    public string? NewPassword { get; set; }
    [Required(ErrorMessage = "Please confirm the new password."), DataType(DataType.Password), Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match."), Display(Name = "Confirm New Password")]
    public string? ConfirmPassword { get; set; }
}

public class CustomerDetailsViewModel
{
    public CustomerDto Customer { get; set; } = default!;
    public IReadOnlyList<OpportunityDto> Opportunities { get; set; } = Array.Empty<OpportunityDto>();
    public IReadOnlyList<FollowUpDto> FollowUps { get; set; } = Array.Empty<FollowUpDto>();
    public IReadOnlyList<ActivityDto> Activities { get; set; } = Array.Empty<ActivityDto>();
    public IReadOnlyList<LeadDto> Leads { get; set; } = Array.Empty<LeadDto>();
    public IReadOnlyList<HistoryItem> History { get; set; } = Array.Empty<HistoryItem>();
    public bool CanDelete { get; set; }
}

public class LeadDetailsViewModel
{
    public LeadDto Lead { get; set; } = default!;
    public IReadOnlyList<FollowUpDto> FollowUps { get; set; } = Array.Empty<FollowUpDto>();
    public IReadOnlyList<ActivityDto> Activities { get; set; } = Array.Empty<ActivityDto>();
    public IReadOnlyList<HistoryItem> History { get; set; } = Array.Empty<HistoryItem>();
    public IReadOnlyList<Domain.LeadStatus> NextStatuses { get; set; } = Array.Empty<Domain.LeadStatus>();
    public bool CanDelete { get; set; }
}

public class OpportunityDetailsViewModel
{
    public OpportunityDto Opportunity { get; set; } = default!;
    public IReadOnlyList<FollowUpDto> FollowUps { get; set; } = Array.Empty<FollowUpDto>();
    public IReadOnlyList<HistoryItem> History { get; set; } = Array.Empty<HistoryItem>();
    public bool CanDelete { get; set; }
}

public class PipelineBoardViewModel
{
    public IReadOnlyList<OpportunityDto> Opportunities { get; set; } = Array.Empty<OpportunityDto>();
    public IReadOnlyList<UserOption> Owners { get; set; } = Array.Empty<UserOption>();
    public string? AssignedTo { get; set; }
}

public class FollowUpsViewModel : ListViewModel<FollowUpDto, FollowUpQuery>
{
    public int OverdueCount { get; set; }
    public int TodayCount { get; set; }
    public int UpcomingCount { get; set; }
}

public class ReportIndexViewModel
{
    public IReadOnlyList<ReportDefinition> Reports { get; set; } = Array.Empty<ReportDefinition>();
}

public class ReportRunViewModel
{
    public ReportResult Result { get; set; } = default!;
    public ReportQuery Query { get; set; } = default!;
    public IReadOnlyList<UserOption> Owners { get; set; } = Array.Empty<UserOption>();
}

public class AuditViewModel : ListViewModel<AuditDto, AuditQuery>
{
    public IReadOnlyList<string> Modules { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Actions { get; set; } = Array.Empty<string>();
    public IReadOnlyList<UserOption> Users { get; set; } = Array.Empty<UserOption>();
}

public class UserFormViewModel
{
    public string? Id { get; set; }
    public IReadOnlyList<UserOption> Managers { get; set; } = Array.Empty<UserOption>();
}

public class UserDetailsViewModel
{
    public UserDto User { get; set; } = default!;
    public bool CanAdminister { get; set; }
    public bool IsSelf { get; set; }
}

public class RolesViewModel
{
    public IReadOnlyList<(string Role, string Description, int Users)> Roles { get; set; } = Array.Empty<(string, string, int)>();
}

public class SearchViewModel
{
    public string Query { get; set; } = "";
    public IReadOnlyList<SearchHit> Hits { get; set; } = Array.Empty<SearchHit>();
}

public class ErrorViewModel
{
    public int Code { get; set; } = 500;
    public string Title { get; set; } = "Something went wrong";
    public string Message { get; set; } = "An unexpected error occurred. Please try again.";
    public string? TraceId { get; set; }
}
