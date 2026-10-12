using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.Controllers;

public class ActivitiesController : CrmController
{
    private readonly IActivityService _svc;
    private readonly ICustomerService _customers;
    private readonly ILeadService _leads;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _me;
    private readonly IClock _clock;

    public ActivitiesController(IActivityService svc, ICustomerService customers, ILeadService leads, IAccessScope scope, ICurrentUser me, IClock clock)
    { _svc = svc; _customers = customers; _leads = leads; _scope = scope; _me = me; _clock = clock; }

    public async Task<IActionResult> Index([FromQuery] ActivityQuery q) =>
        View(new ListViewModel<ActivityDto, ActivityQuery> { Result = await _svc.ListAsync(q), Query = q, Owners = await _scope.AssignableUsersAsync() });

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId, int? leadId, ActivityType? type)
    {
        await LoadFormAsync(null);
        return View("Form", new ActivityInput { ActivityType = type ?? ActivityType.Call, CustomerId = customerId, LeadId = leadId, ActivityDate = _clock.Today, AssignedTo = _me.UserId, Status = ActivityStatus.Completed });
    }

    [HttpPost]
    public async Task<IActionResult> Create(ActivityInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.CreateAsync(input);
            if (r.Succeeded) { Success("Activity logged."); return RedirectToAction(nameof(Index)); }
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
        var a = r.Value!;
        await LoadFormAsync(id);
        return View("Form", new ActivityInput
        {
            ActivityType = Enum.Parse<ActivityType>(a.ActivityType), Subject = a.Subject, Description = a.Description, ActivityDate = a.ActivityDate,
            CustomerId = a.CustomerId, LeadId = a.LeadId, Status = Enum.Parse<ActivityStatus>(a.Status), AssignedTo = a.AssignedTo,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, ActivityInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.UpdateAsync(id, input);
            if (r.Succeeded) { Success("Activity updated."); return RedirectToAction(nameof(Index)); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        await LoadFormAsync(id);
        return View("Form", input);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _svc.DeleteAsync(id);
        if (r.Succeeded) Success("Activity deleted.");
        else if (Denied(r) is { } denied) return denied;
        else Failure(r.Message ?? "The activity could not be deleted.");
        return RedirectToAction(nameof(Index));
    }

    private async Task LoadFormAsync(int? id)
    {
        ViewData["EntityId"] = id;
        ViewData["Owners"] = OwnerItems(await _scope.AssignableUsersAsync());
        ViewData["Types"] = EnumItems<ActivityType>();
        ViewData["Statuses"] = EnumItems<ActivityStatus>();
        ViewData["Customers"] = (await _customers.LookupAsync()).Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToList();
        ViewData["Leads"] = (await _leads.ListAsync(new LeadQuery { PageSize = 100 })).Items.Select(l => new SelectListItem($"{l.LeadName} ({l.LeadCode})", l.LeadId.ToString())).ToList();
    }
}
