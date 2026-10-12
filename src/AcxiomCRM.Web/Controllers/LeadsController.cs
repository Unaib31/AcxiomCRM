using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.Domain.Rules;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.Controllers;

public class LeadsController : CrmController
{
    private readonly ILeadService _svc;
    private readonly IFollowUpService _followUps;
    private readonly IActivityService _activities;
    private readonly ICustomerService _customers;
    private readonly IAuditQueryService _audit;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _me;

    public LeadsController(ILeadService svc, IFollowUpService followUps, IActivityService activities, ICustomerService customers,
        IAuditQueryService audit, IAccessScope scope, ICurrentUser me)
    { _svc = svc; _followUps = followUps; _activities = activities; _customers = customers; _audit = audit; _scope = scope; _me = me; }

    public async Task<IActionResult> Index([FromQuery] LeadQuery q) =>
        View(new ListViewModel<LeadDto, LeadQuery> { Result = await _svc.ListAsync(q), Query = q, Owners = await _scope.AssignableUsersAsync() });

    public async Task<IActionResult> Details(int id)
    {
        var r = await _svc.GetAsync(id);
        if (!r.Succeeded) return NotFound();
        var lead = r.Value!;
        var status = Enum.Parse<LeadStatus>(lead.Status);
        return View(new LeadDetailsViewModel
        {
            Lead = lead,
            FollowUps = (await _followUps.ListAsync(new FollowUpQuery { RelatedType = RelatedType.Lead, RelatedId = id, PageSize = 50, Desc = true })).Items,
            Activities = (await _activities.ListAsync(new ActivityQuery { LeadId = id, PageSize = 50 })).Items,
            History = await _audit.HistoryAsync("Lead", id),
            NextStatuses = LeadWorkflow.Allowed(status).Where(s => s != status).ToList(),
            CanDelete = _me.Role != RoleNames.SalesExecutive,
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        await LoadFormAsync(null, LeadStatus.New);
        return View("Form", new LeadInput { AssignedTo = _me.UserId });
    }

    [HttpPost]
    public async Task<IActionResult> Create(LeadInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.CreateAsync(input);
            if (r.Succeeded) { Success($"Lead {r.Value!.LeadName} created."); return RedirectToAction(nameof(Details), new { id = r.Value.LeadId }); }
            AddErrors(r);
        }
        await LoadFormAsync(null, LeadStatus.New);
        return View("Form", input);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var r = await _svc.GetAsync(id);
        if (!r.Succeeded) return NotFound();
        var l = r.Value!;
        var status = Enum.Parse<LeadStatus>(l.Status);
        if (status == LeadStatus.Converted) { Failure("Converted leads are read-only."); return RedirectToAction(nameof(Details), new { id }); }
        await LoadFormAsync(id, status);
        return View("Form", new LeadInput
        {
            LeadName = l.LeadName, Email = l.Email, Phone = l.Phone, CompanyName = l.CompanyName, Source = Enum.Parse<LeadSource>(l.Source),
            Status = status, Priority = Enum.Parse<LeadPriority>(l.Priority), ExpectedValue = l.ExpectedValue, Notes = l.Notes, AssignedTo = l.AssignedTo,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, LeadInput input)
    {
        var current = await _svc.GetAsync(id);
        if (!current.Succeeded) return NotFound();
        if (ModelState.IsValid)
        {
            var r = await _svc.UpdateAsync(id, input);
            if (r.Succeeded) { Success("Lead updated."); return RedirectToAction(nameof(Details), new { id }); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        await LoadFormAsync(id, Enum.Parse<LeadStatus>(current.Value!.Status));
        return View("Form", input);
    }

    [HttpPost]
    public async Task<IActionResult> ChangeStatus(int id, LeadStatus status)
    {
        var r = await _svc.ChangeStatusAsync(id, status);
        if (r.Succeeded) Success($"Lead status changed to {status}.");
        else if (Denied(r) is { } denied) return denied;
        else Failure(r.Message ?? "The status could not be changed.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Convert(int id)
    {
        var r = await _svc.GetAsync(id);
        if (!r.Succeeded) return NotFound();
        if (r.Value!.Status != nameof(LeadStatus.Qualified)) { Failure("Only Qualified leads can be converted."); return RedirectToAction(nameof(Details), new { id }); }
        await LoadConvertAsync(r.Value);
        return View(new LeadConversionInput { OpportunityName = $"{r.Value.CompanyName ?? r.Value.LeadName} - Opportunity", Amount = r.Value.ExpectedValue > 0 ? r.Value.ExpectedValue : null, Probability = 20 });
    }

    [HttpPost]
    public async Task<IActionResult> Convert(int id, LeadConversionInput input)
    {
        var lead = await _svc.GetAsync(id);
        if (!lead.Succeeded) return NotFound();
        if (ModelState.IsValid)
        {
            var r = await _svc.ConvertAsync(id, input);
            if (r.Succeeded)
            {
                Success("Lead converted successfully.");
                return r.Value!.OpportunityId is { } oid ? RedirectToAction("Details", "Opportunities", new { id = oid }) : RedirectToAction("Details", "Customers", new { id = r.Value.CustomerId });
            }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        await LoadConvertAsync(lead.Value!);
        return View(input);
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _svc.DeleteAsync(id);
        if (r.Succeeded) { Success("Lead deleted."); return RedirectToAction(nameof(Index)); }
        if (Denied(r) is { } denied) return denied;
        Failure(r.Message ?? "The lead could not be deleted.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task LoadFormAsync(int? id, LeadStatus current)
    {
        ViewData["EntityId"] = id;
        ViewData["Owners"] = OwnerItems(await _scope.AssignableUsersAsync());
        ViewData["Sources"] = EnumItems<LeadSource>();
        ViewData["Priorities"] = EnumItems<LeadPriority>();
        ViewData["Statuses"] = id is null
            ? EnumItems<LeadStatus>(only: new[] { LeadStatus.New, LeadStatus.Contacted })
            : EnumItems<LeadStatus>(only: LeadWorkflow.Allowed(current));
    }

    private async Task LoadConvertAsync(LeadDto lead)
    {
        ViewData["Lead"] = lead;
        ViewData["Customers"] = (await _customers.LookupAsync()).Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToList();
    }
}
