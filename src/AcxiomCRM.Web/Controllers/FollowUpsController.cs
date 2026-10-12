using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.Controllers;

public class FollowUpsController : CrmController
{
    private readonly IFollowUpService _svc;
    private readonly ICustomerService _customers;
    private readonly ILeadService _leads;
    private readonly IOpportunityService _opps;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _me;
    private readonly IClock _clock;

    public FollowUpsController(IFollowUpService svc, ICustomerService customers, ILeadService leads, IOpportunityService opps,
        IAccessScope scope, ICurrentUser me, IClock clock)
    { _svc = svc; _customers = customers; _leads = leads; _opps = opps; _scope = scope; _me = me; _clock = clock; }

    public async Task<IActionResult> Index([FromQuery] FollowUpQuery q)
    {
        q.View ??= "upcoming";
        var counts = async (string view) => (await _svc.ListAsync(new FollowUpQuery { View = view, PageSize = 1, AssignedTo = q.AssignedTo })).TotalCount;
        return View(new FollowUpsViewModel
        {
            Result = await _svc.ListAsync(q), Query = q, Owners = await _scope.AssignableUsersAsync(),
            OverdueCount = await counts("overdue"), TodayCount = await counts("today"), UpcomingCount = await counts("upcoming"),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(RelatedType? relatedType, int? relatedId)
    {
        await LoadFormAsync(null);
        return View("Form", new FollowUpInput { RelatedType = relatedType ?? RelatedType.Customer, RelatedId = relatedId, FollowUpDate = _clock.Today.AddDays(1), AssignedTo = _me.UserId });
    }

    [HttpPost]
    public async Task<IActionResult> Create(FollowUpInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.CreateAsync(input);
            if (r.Succeeded) { Success("Follow-up scheduled."); return Back(r.Value!); }
            AddErrors(r);
        }
        await LoadFormAsync(null);
        return View("Form", input);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _svc.GetAsync(id);
        if (!r.Succeeded) return NotFound();
        var f = r.Value!;
        if (f.Status is nameof(FollowUpStatus.Completed) or nameof(FollowUpStatus.Cancelled)) { Failure($"{f.Status} follow-ups cannot be modified."); return RedirectToAction(nameof(Index), new { view = "all" }); }
        await LoadFormAsync(id);
        return View("Form", new FollowUpInput
        {
            RelatedType = Enum.Parse<RelatedType>(f.RelatedType), RelatedId = f.RelatedId, FollowUpDate = f.FollowUpDate, Subject = f.Subject,
            FollowUpType = Enum.Parse<FollowUpType>(f.FollowUpType), Remarks = f.Remarks, Status = Enum.Parse<FollowUpStatus>(f.Status), AssignedTo = f.AssignedTo,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, FollowUpInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.UpdateAsync(id, input);
            if (r.Succeeded) { Success("Follow-up updated."); return Back(r.Value!); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        await LoadFormAsync(id);
        return View("Form", input);
    }

    [HttpPost]
    public async Task<IActionResult> Complete(int id, string? remarks)
    {
        var r = await _svc.CompleteAsync(id, remarks);
        if (r.Succeeded) Success("Follow-up completed and logged to activity history.");
        else if (Denied(r) is { } denied) return denied;
        else Failure(r.Message ?? "The follow-up could not be completed.");
        return LocalRedirectBack();
    }

    [HttpPost]
    public async Task<IActionResult> Miss(int id) => await SetStatus(id, FollowUpStatus.Missed, "Follow-up marked as missed.");

    [HttpPost]
    public async Task<IActionResult> Cancel(int id) => await SetStatus(id, FollowUpStatus.Cancelled, "Follow-up cancelled.");

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _svc.DeleteAsync(id);
        if (r.Succeeded) Success("Follow-up deleted.");
        else if (Denied(r) is { } denied) return denied;
        else Failure(r.Message ?? "The follow-up could not be deleted.");
        return RedirectToAction(nameof(Index), new { view = "all" });
    }

    private async Task<IActionResult> SetStatus(int id, FollowUpStatus status, string ok)
    {
        var r = await _svc.SetStatusAsync(id, status);
        if (r.Succeeded) Success(ok);
        else if (Denied(r) is { } denied) return denied;
        else Failure(r.Message ?? "The follow-up could not be updated.");
        return LocalRedirectBack();
    }

    private IActionResult LocalRedirectBack()
    {
        var back = Request.Form["returnUrl"].ToString();
        return !string.IsNullOrEmpty(back) && Url.IsLocalUrl(back) ? LocalRedirect(back) : RedirectToAction(nameof(Index));
    }

    private IActionResult Back(FollowUpDto f)
    {
        var back = Request.Form["returnUrl"].ToString();
        if (!string.IsNullOrEmpty(back) && Url.IsLocalUrl(back)) return LocalRedirect(back);
        return f.RelatedType switch
        {
            "Customer" => RedirectToAction("Details", "Customers", new { id = f.RelatedId }),
            "Lead" => RedirectToAction("Details", "Leads", new { id = f.RelatedId }),
            _ => RedirectToAction("Details", "Opportunities", new { id = f.RelatedId }),
        };
    }

    private async Task LoadFormAsync(int? id)
    {
        ViewData["EntityId"] = id;
        ViewData["Owners"] = OwnerItems(await _scope.AssignableUsersAsync());
        ViewData["Types"] = EnumItems<FollowUpType>();
        ViewData["RelatedTypes"] = EnumItems<RelatedType>();
        ViewData["Statuses"] = EnumItems<FollowUpStatus>(only: id is null ? new[] { FollowUpStatus.Planned } : new[] { FollowUpStatus.Planned, FollowUpStatus.Missed });
        ViewData["Customers"] = (await _customers.LookupAsync()).Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToList();
        ViewData["Leads"] = (await _leads.ListAsync(new LeadQuery { PageSize = 100 })).Items.Select(l => new SelectListItem($"{l.LeadName} ({l.LeadCode})", l.LeadId.ToString())).ToList();
        ViewData["Opportunities"] = (await _opps.ListAsync(new OpportunityQuery { PageSize = 100, State = "open" })).Items.Select(o => new SelectListItem(o.OpportunityName, o.OpportunityId.ToString())).ToList();
    }
}
