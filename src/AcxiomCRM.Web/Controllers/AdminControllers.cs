using System.Text;
using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.Infrastructure;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.Controllers;

[Authorize(Policy = PolicyNames.UsersView)]
public class UsersController : CrmController
{
    private readonly IUserAdminService _svc;
    private readonly ICurrentUser _me;
    public UsersController(IUserAdminService svc, ICurrentUser me) { _svc = svc; _me = me; }

    public async Task<IActionResult> Index([FromQuery] UserQuery q) =>
        View(new ListViewModel<UserDto, UserQuery> { Result = await _svc.ListAsync(q), Query = q });

    public async Task<IActionResult> Details(string id)
    {
        var r = await _svc.GetAsync(id);
        if (!r.Succeeded) return NotFound();
        return View(new UserDetailsViewModel { User = r.Value!, CanAdminister = _me.IsAdmin, IsSelf = id == _me.UserId });
    }

    [HttpGet, Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Create()
    {
        await LoadFormAsync(null);
        return View("Form", new UserCreateInput { Role = RoleNames.SalesExecutive });
    }

    [HttpPost, Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Create(UserCreateInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.CreateAsync(input);
            if (r.Succeeded) { Success($"User {r.Value!.FullName} created."); return RedirectToAction(nameof(Details), new { id = r.Value.Id }); }
            AddErrors(r);
        }
        input.Password = input.ConfirmPassword = null;
        await LoadFormAsync(null);
        return View("Form", input);
    }

    [HttpGet, Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Edit(string id)
    {
        var r = await _svc.GetAsync(id);
        if (!r.Succeeded) return NotFound();
        var u = r.Value!;
        await LoadFormAsync(id);
        ViewData["IsSelf"] = id == _me.UserId;
        return View("Form", new UserCreateInput { FullName = u.FullName, Email = u.Email, Role = u.Role, ManagerId = u.ManagerId, IsActive = u.IsActive });
    }

    [HttpPost, Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Edit(string id, UserEditInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.UpdateAsync(id, input);
            if (r.Succeeded) { Success("User updated."); return RedirectToAction(nameof(Details), new { id }); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        await LoadFormAsync(id);
        ViewData["IsSelf"] = id == _me.UserId;
        return View("Form", new UserCreateInput { FullName = input.FullName, Email = input.Email, Role = input.Role, ManagerId = input.ManagerId, IsActive = input.IsActive });
    }

    [HttpPost, Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> SetActive(string id, bool active)
    {
        var r = await _svc.SetActiveAsync(id, active);
        if (r.Succeeded) Success(active ? "User activated." : "User deactivated and signed out.");
        else if (Denied(r) is { } denied) return denied;
        else Failure(r.Message ?? "The user could not be updated.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Unlock(string id)
    {
        var r = await _svc.UnlockAsync(id);
        if (Denied(r) is { } denied) return denied;
        if (r.Succeeded) Success("Account unlocked."); else Failure(r.Message ?? "The account could not be unlocked.");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Issues a single-use reset link that the administrator hands to the user. The token is shown once and never stored or logged.</summary>
    [HttpPost, Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> ResetLink(string id)
    {
        var user = await _svc.GetAsync(id);
        if (!user.Succeeded) return NotFound();
        var r = await _svc.CreateResetTokenAsync(id);
        if (!r.Succeeded) return NotFound();
        ViewData["Link"] = Url.Action("ResetPassword", "Account", new { userId = id, token = r.Value }, Request.Scheme);
        return View(user.Value!);
    }

    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Roles() => View(new RolesViewModel { Roles = await _svc.RolesAsync() });

    private async Task LoadFormAsync(string? id)
    {
        ViewData["EntityId"] = id;
        ViewData["Roles"] = RoleNames.All.Select(r => new SelectListItem(RoleNames.Display(r), r)).ToList();
        ViewData["Managers"] = (await _svc.ManagersAsync()).Select(m => new SelectListItem(m.Name, m.Id)).ToList();
    }
}

[Authorize(Policy = PolicyNames.AuditView)]
public class AuditController : CrmController
{
    private readonly IAuditQueryService _svc;
    private readonly IUserAdminService _users;
    public AuditController(IAuditQueryService svc, IUserAdminService users) { _svc = svc; _users = users; }

    public async Task<IActionResult> Index([FromQuery] AuditQuery q)
    {
        var users = (await _users.ListAsync(new UserQuery { PageSize = 100 })).Items.Select(u => new UserOption(u.Id, u.FullName, RoleNames.Display(u.Role))).ToList();
        return View(new AuditViewModel
        {
            Result = await _svc.ListAsync(q), Query = q, Modules = await _svc.ModulesAsync(), Actions = await _svc.ActionsAsync(), Users = users,
        });
    }

    public async Task<IActionResult> Export([FromQuery] AuditQuery q)
    {
        var rows = await _svc.ExportAsync(q);
        var sb = new StringBuilder("Time,User,Module,Action,Entity,Record,Result,Details\r\n");
        foreach (var a in rows)
            sb.Append(string.Join(",", new[] { a.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss"), a.UserName, a.Module, a.Action, a.EntityName, a.RecordId, a.Result, a.Details }.Select(ReportService.Csv))).Append("\r\n");
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", $"audit-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }
}

public class ReportsController : CrmController
{
    private readonly IReportService _svc;
    private readonly IAccessScope _scope;
    public ReportsController(IReportService svc, IAccessScope scope) { _svc = svc; _scope = scope; }

    public IActionResult Index() => View(new ReportIndexViewModel { Reports = _svc.Available() });

    public async Task<IActionResult> Run(string id, [FromQuery] ReportQuery q)
    {
        if (_svc.Find(id) is null) return NotFound();
        return View(new ReportRunViewModel { Result = await _svc.RunAsync(id, q), Query = q, Owners = await _scope.AssignableUsersAsync() });
    }

    public async Task<IActionResult> Export(string id, [FromQuery] ReportQuery q)
    {
        if (_svc.Find(id) is null) return NotFound();
        var csv = await _svc.ExportCsvAsync(id, q);
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"{id}-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }
}
