using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Application.Security;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AcxiomCRM.Controllers.Api;

/// <summary>
/// Base for all secured endpoints: JWT bearer only (so no cookie, no CSRF surface), JSON problem details for every error.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[IgnoreAntiforgeryToken]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
public abstract class ApiBase : ControllerBase
{
    protected IActionResult Fail(ServiceResult r) => r.Status switch
    {
        ResultStatus.Invalid => ValidationProblem(new ValidationProblemDetails(r.Errors) { Status = 400, Title = "One or more validation errors occurred.", Detail = r.Message }),
        ResultStatus.NotFound => Problem(statusCode: 404, title: "The requested resource was not found."),
        ResultStatus.Forbidden => Problem(statusCode: 403, title: "You do not have permission to perform this action.", detail: r.Message),
        ResultStatus.Conflict => Conflict(new ValidationProblemDetails(r.Errors) { Status = 409, Title = "The request conflicts with existing data.", Detail = r.Message }),
        _ => Problem(statusCode: 500, title: "An unexpected error occurred."),
    };

    protected IActionResult Respond<T>(ServiceResult<T> r) => r.Succeeded ? Ok(r.Value) : Fail(r);
    protected IActionResult Respond(ServiceResult r) => r.Succeeded ? NoContent() : Fail(r);
}

/// <summary>Authentication.</summary>
[Route("api/auth")]
public class AuthController : ApiBase
{
    private readonly IAuthService _auth;
    private readonly IJwtTokenService _jwt;
    public AuthController(IAuthService auth, IJwtTokenService jwt) { _auth = auth; _jwt = jwt; }

    /// <summary>Authenticate with email/username and password and receive a bearer token.</summary>
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(LoginResponse), 200)]
    [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 401)]
    [ProducesResponseType(typeof(ProblemDetails), 429)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await _auth.AuthenticateAsync(request.UserName!, request.Password!, "API");
        switch (result.Status)
        {
            case AuthStatus.Success:
                var (token, exp) = _jwt.Issue(result.User!, result.Role!);
                return Ok(new LoginResponse(token, "Bearer", exp, result.User!.UserName!, result.User.FullName, result.Role!));
            case AuthStatus.LockedOut:
                return Problem(statusCode: 401, title: "Authentication failed.", detail: "The account is temporarily locked after repeated failed attempts. Try again later.");
            default: // wrong credentials and disabled accounts look identical to callers
                return Problem(statusCode: 401, title: "Authentication failed.", detail: "Invalid credentials.");
        }
    }

    /// <summary>Revoke the presented token.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(204)]
    public async Task<IActionResult> Logout([FromServices] IAuditService audit)
    {
        var jti = User.FindFirst("jti")?.Value;
        var exp = User.FindFirst("exp")?.Value;
        if (jti is not null && long.TryParse(exp, out var e))
            await _jwt.RevokeAsync(jti, DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime);
        await audit.LogAsync("Authentication", "Logout", details: "Signed out via API");
        return NoContent();
    }
}

/// <summary>Customers (scoped to the caller's role and ownership).</summary>
[Route("api/customers")]
public class CustomersController : ApiBase
{
    private readonly ICustomerService _svc;
    public CustomersController(ICustomerService svc) => _svc = svc;

    /// <summary>List/search customers. Supports search, status, assignedTo, sort, desc, page and pageSize (max 100).</summary>
    [HttpGet] [ProducesResponseType(typeof(PagedResult<CustomerDto>), 200)]
    public async Task<IActionResult> List([FromQuery] CustomerQuery q) => Ok(await _svc.ListAsync(q));

    [HttpGet("{id:int}")] [ProducesResponseType(typeof(CustomerDto), 200)] [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> Get(int id) => Respond(await _svc.GetAsync(id));

    [HttpPost] [ProducesResponseType(typeof(CustomerDto), 201)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)] [ProducesResponseType(typeof(ValidationProblemDetails), 409)]
    public async Task<IActionResult> Create([FromBody] CustomerInput input)
    {
        var r = await _svc.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value!.CustomerId }, r.Value) : Fail(r);
    }

    [HttpPut("{id:int}")] [ProducesResponseType(typeof(CustomerDto), 200)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)] [ProducesResponseType(typeof(ProblemDetails), 404)] [ProducesResponseType(typeof(ValidationProblemDetails), 409)]
    public async Task<IActionResult> Update(int id, [FromBody] CustomerInput input) => Respond(await _svc.UpdateAsync(id, input));

    /// <summary>Deactivate (soft-delete) a customer. Manager/Admin only; blocked while active opportunities exist.</summary>
    [HttpDelete("{id:int}")] [ProducesResponseType(204)] [ProducesResponseType(typeof(ProblemDetails), 403)] [ProducesResponseType(typeof(ProblemDetails), 404)] [ProducesResponseType(typeof(ValidationProblemDetails), 409)]
    public async Task<IActionResult> Delete(int id) => Respond(await _svc.DeleteAsync(id));
}

/// <summary>Leads.</summary>
[Route("api/leads")]
public class LeadsController : ApiBase
{
    private readonly ILeadService _svc;
    public LeadsController(ILeadService svc) => _svc = svc;

    [HttpGet] [ProducesResponseType(typeof(PagedResult<LeadDto>), 200)]
    public async Task<IActionResult> List([FromQuery] LeadQuery q) => Ok(await _svc.ListAsync(q));

    [HttpGet("{id:int}")] [ProducesResponseType(typeof(LeadDto), 200)] [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> Get(int id) => Respond(await _svc.GetAsync(id));

    [HttpPost] [ProducesResponseType(typeof(LeadDto), 201)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> Create([FromBody] LeadInput input)
    {
        var r = await _svc.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value!.LeadId }, r.Value) : Fail(r);
    }

    [HttpPut("{id:int}")] [ProducesResponseType(typeof(LeadDto), 200)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)] [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> Update(int id, [FromBody] LeadInput input) => Respond(await _svc.UpdateAsync(id, input));

    /// <summary>Move a lead through the status workflow (invalid transitions are rejected).</summary>
    [HttpPost("{id:int}/status")] [ProducesResponseType(typeof(LeadDto), 200)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> Status(int id, [FromBody] LeadStatusInput input) => Respond(await _svc.ChangeStatusAsync(id, input.Status));

    /// <summary>Convert a Qualified lead into a customer (and optionally an opportunity).</summary>
    [HttpPost("{id:int}/convert")] [ProducesResponseType(typeof(LeadConversionResult), 200)] [ProducesResponseType(typeof(ValidationProblemDetails), 409)]
    public async Task<IActionResult> Convert(int id, [FromBody] LeadConversionInput input) => Respond(await _svc.ConvertAsync(id, input));

    [HttpDelete("{id:int}")] [ProducesResponseType(204)] [ProducesResponseType(typeof(ProblemDetails), 403)]
    public async Task<IActionResult> Delete(int id) => Respond(await _svc.DeleteAsync(id));
}

/// <summary>Opportunities.</summary>
[Route("api/opportunities")]
public class OpportunitiesController : ApiBase
{
    private readonly IOpportunityService _svc;
    public OpportunitiesController(IOpportunityService svc) => _svc = svc;

    [HttpGet] [ProducesResponseType(typeof(PagedResult<OpportunityDto>), 200)]
    public async Task<IActionResult> List([FromQuery] OpportunityQuery q) => Ok(await _svc.ListAsync(q));

    [HttpGet("{id:int}")] [ProducesResponseType(typeof(OpportunityDto), 200)] [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> Get(int id) => Respond(await _svc.GetAsync(id));

    /// <summary>Amount must be &gt; 0, probability 0–100, expected close date not in the past for active stages.</summary>
    [HttpPost] [ProducesResponseType(typeof(OpportunityDto), 201)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> Create([FromBody] OpportunityInput input)
    {
        var r = await _svc.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value!.OpportunityId }, r.Value) : Fail(r);
    }

    [HttpPut("{id:int}")] [ProducesResponseType(typeof(OpportunityDto), 200)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)] [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> Update(int id, [FromBody] OpportunityInput input) => Respond(await _svc.UpdateAsync(id, input));

    /// <summary>Move to another pipeline stage, or close as Won/Lost.</summary>
    [HttpPost("{id:int}/stage")] [ProducesResponseType(typeof(OpportunityDto), 200)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> Stage(int id, [FromBody] StageChangeInput input) => Respond(await _svc.ChangeStageAsync(id, input));

    [HttpDelete("{id:int}")] [ProducesResponseType(204)] [ProducesResponseType(typeof(ProblemDetails), 403)]
    public async Task<IActionResult> Delete(int id) => Respond(await _svc.DeleteAsync(id));
}

/// <summary>Follow-ups.</summary>
[Route("api/followups")]
public class FollowUpsController : ApiBase
{
    private readonly IFollowUpService _svc;
    public FollowUpsController(IFollowUpService svc) => _svc = svc;

    /// <summary>List follow-ups. view = upcoming | overdue | today | all.</summary>
    [HttpGet] [ProducesResponseType(typeof(PagedResult<FollowUpDto>), 200)]
    public async Task<IActionResult> List([FromQuery] FollowUpQuery q) => Ok(await _svc.ListAsync(q));

    [HttpGet("{id:int}")] [ProducesResponseType(typeof(FollowUpDto), 200)] [ProducesResponseType(typeof(ProblemDetails), 404)]
    public async Task<IActionResult> Get(int id) => Respond(await _svc.GetAsync(id));

    /// <summary>The follow-up date cannot be earlier than today.</summary>
    [HttpPost] [ProducesResponseType(typeof(FollowUpDto), 201)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> Create([FromBody] FollowUpInput input)
    {
        var r = await _svc.CreateAsync(input);
        return r.Succeeded ? CreatedAtAction(nameof(Get), new { id = r.Value!.FollowUpId }, r.Value) : Fail(r);
    }

    [HttpPut("{id:int}")] [ProducesResponseType(typeof(FollowUpDto), 200)] [ProducesResponseType(typeof(ValidationProblemDetails), 400)]
    public async Task<IActionResult> Update(int id, [FromBody] FollowUpInput input) => Respond(await _svc.UpdateAsync(id, input));

    [HttpPost("{id:int}/complete")] [ProducesResponseType(typeof(FollowUpDto), 200)] [ProducesResponseType(typeof(ValidationProblemDetails), 409)]
    public async Task<IActionResult> Complete(int id, [FromBody] CompleteFollowUpRequest? body) => Respond(await _svc.CompleteAsync(id, body?.Remarks));
}

public record CompleteFollowUpRequest(string? Remarks);

/// <summary>Reports (management data).</summary>
[Route("api/reports")]
public class ReportsController : ApiBase
{
    private readonly IReportService _svc;
    public ReportsController(IReportService svc) => _svc = svc;

    /// <summary>Stage-wise and owner-wise pipeline. Managers see their team, admins see everything; sales executives are forbidden.</summary>
    [HttpGet("pipeline")] [Authorize(Policy = PolicyNames.ManagerOrAdmin, AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [ProducesResponseType(typeof(PipelineSummary), 200)] [ProducesResponseType(typeof(ProblemDetails), 403)]
    public async Task<IActionResult> Pipeline() => Ok(await _svc.PipelineSummaryAsync());
}
