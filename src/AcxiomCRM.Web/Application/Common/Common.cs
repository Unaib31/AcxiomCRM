using System.Security.Claims;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Entities;

namespace AcxiomCRM.Application.Common;

public enum ResultStatus { Ok, Created, Invalid, NotFound, Forbidden, Conflict }

public class ServiceResult
{
    public ResultStatus Status { get; protected init; }
    public Dictionary<string, string[]> Errors { get; protected init; } = new();
    public string? Message { get; protected init; }
    public bool Succeeded => Status is ResultStatus.Ok or ResultStatus.Created;

    public static ServiceResult Ok() => new() { Status = ResultStatus.Ok };
    public static ServiceResult NotFound(string? msg = null) => new() { Status = ResultStatus.NotFound, Message = msg ?? "The requested record was not found." };
    public static ServiceResult Forbidden(string? msg = null) => new() { Status = ResultStatus.Forbidden, Message = msg ?? "You do not have permission to perform this action." };
    public static ServiceResult Conflict(string msg, string? field = null) => new()
    {
        Status = ResultStatus.Conflict, Message = msg,
        Errors = field is null ? new() : new() { [field] = new[] { msg } }
    };
    public static ServiceResult Conflict(IDictionary<string, string> errors) => new()
    {
        Status = ResultStatus.Conflict, Message = errors.Values.First(),
        Errors = errors.ToDictionary(e => e.Key, e => new[] { e.Value })
    };
    public static ServiceResult Invalid(string field, string msg) => new()
    {
        Status = ResultStatus.Invalid, Message = msg, Errors = new() { [field] = new[] { msg } }
    };
    public static ServiceResult Invalid(IDictionary<string, string> errors) => new()
    {
        Status = ResultStatus.Invalid, Message = "One or more validation errors occurred.",
        Errors = errors.ToDictionary(e => e.Key, e => new[] { e.Value })
    };
}

public sealed class ServiceResult<T> : ServiceResult
{
    public T? Value { get; private init; }
    public static ServiceResult<T> Ok(T value) => new() { Status = ResultStatus.Ok, Value = value };
    public static ServiceResult<T> Created(T value) => new() { Status = ResultStatus.Created, Value = value };
    public new static ServiceResult<T> NotFound(string? msg = null) => new() { Status = ResultStatus.NotFound, Message = msg ?? "The requested record was not found." };
    public new static ServiceResult<T> Forbidden(string? msg = null) => new() { Status = ResultStatus.Forbidden, Message = msg ?? "You do not have permission to perform this action." };
    public new static ServiceResult<T> Conflict(string msg, string? field = null) => new()
    {
        Status = ResultStatus.Conflict, Message = msg,
        Errors = field is null ? new() : new() { [field] = new[] { msg } }
    };
    public new static ServiceResult<T> Conflict(IDictionary<string, string> errors) => new()
    {
        Status = ResultStatus.Conflict, Message = errors.Values.First(),
        Errors = errors.ToDictionary(e => e.Key, e => new[] { e.Value })
    };
    public new static ServiceResult<T> Invalid(string field, string msg) => new()
    {
        Status = ResultStatus.Invalid, Message = msg, Errors = new() { [field] = new[] { msg } }
    };
    public new static ServiceResult<T> Invalid(IDictionary<string, string> errors) => new()
    {
        Status = ResultStatus.Invalid, Message = "One or more validation errors occurred.",
        Errors = errors.ToDictionary(e => e.Key, e => new[] { e.Value })
    };
    public static ServiceResult<T> From(ServiceResult r) => new() { Status = r.Status, Message = r.Message, Errors = r.Errors };
}

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>Common paging/sorting parameters for every list endpoint and page.</summary>
public class ListQuery
{
    public const int MaxPageSize = 100;
    private int _page = 1, _pageSize = 10;
    public string? Search { get; set; }
    public string? Sort { get; set; }
    public bool Desc { get; set; }
    public int Page { get => _page; set => _page = Math.Max(1, value); }
    public int PageSize { get => _pageSize; set => _pageSize = Math.Clamp(value, 1, MaxPageSize); }
    public string? AssignedTo { get; set; }
}

public interface IClock
{
    DateTime UtcNow { get; }
    DateOnly Today { get; }
    DateTime ToLocal(DateTime utc);
    /// <summary>UTC instant at which the given local calendar day starts.</summary>
    DateTime StartOfDayUtc(DateOnly localDate);
}

public sealed class SystemClock : IClock
{
    private readonly TimeZoneInfo _tz;
    public SystemClock(Microsoft.Extensions.Configuration.IConfiguration cfg)
    {
        var id = cfg["Clock:TimeZoneId"];
        try { _tz = string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { _tz = TimeZoneInfo.Utc; }
    }
    public DateTime UtcNow => DateTime.UtcNow;
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz));
    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _tz);
    public DateTime StartOfDayUtc(DateOnly d) => TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(TimeOnly.MinValue), _tz);
}

public interface ICurrentUser
{
    string? UserId { get; }
    string? UserName { get; }
    string? IpAddress { get; }
    bool IsAuthenticated { get; }
    string? Role { get; }
    bool IsAdmin { get; }
    bool IsManager { get; }
}

public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;
    public HttpCurrentUser(IHttpContextAccessor http) => _http = http;
    private ClaimsPrincipal? P => _http.HttpContext?.User;
    public bool IsAuthenticated => P?.Identity?.IsAuthenticated == true;
    public string? UserId => P is { Identity.IsAuthenticated: true } p ? (p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub")) : null;
    public string? UserName => P is { Identity.IsAuthenticated: true } p ? (p.FindFirstValue(ClaimTypes.Name) ?? p.FindFirstValue("name")) : null;
    public string? IpAddress => _http.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? Role => RoleNames.All.FirstOrDefault(r => P?.IsInRole(r) == true);
    public bool IsAdmin => P?.IsInRole(RoleNames.Admin) == true;
    public bool IsManager => P?.IsInRole(RoleNames.Manager) == true;
}

/// <summary>Used for background work (seeding, tests) where there is no HTTP user.</summary>
public sealed class SystemUser : ICurrentUser
{
    public string? UserId => null;
    public string? UserName => "system";
    public string? IpAddress => null;
    public bool IsAuthenticated => false;
    public string? Role => null;
    public bool IsAdmin => false;
    public bool IsManager => false;
}

public sealed record ScopeInfo(string UserId, string Role, bool IsAdmin, IReadOnlyList<string> OwnerIds)
{
    public bool IsManager => Role == RoleNames.Manager;
    public bool CanSee(string ownerId) => IsAdmin || OwnerIds.Contains(ownerId);
}

public sealed record UserOption(string Id, string Name, string Role);

public interface IAccessScope
{
    Task<ScopeInfo> GetAsync();
    Task<IQueryable<T>> ApplyAsync<T>(IQueryable<T> query) where T : IOwnedRecord;
    Task<IReadOnlyList<UserOption>> AssignableUsersAsync();
    /// <summary>Resolves the owner for a new/updated record: defaults to the caller, rejects users outside their authority.</summary>
    Task<(string? OwnerId, string? Error)> ResolveAssigneeAsync(string? requested);
}
