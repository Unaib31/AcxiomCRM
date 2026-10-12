using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.Controllers;

public class CustomersController : CrmController
{
    private readonly ICustomerService _svc;
    private readonly IOpportunityService _opps;
    private readonly IFollowUpService _followUps;
    private readonly IActivityService _activities;
    private readonly ILeadService _leads;
    private readonly IAuditQueryService _audit;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _me;

    public CustomersController(ICustomerService svc, IOpportunityService opps, IFollowUpService followUps, IActivityService activities,
        ILeadService leads, IAuditQueryService audit, IAccessScope scope, ICurrentUser me)
    { _svc = svc; _opps = opps; _followUps = followUps; _activities = activities; _leads = leads; _audit = audit; _scope = scope; _me = me; }

    public async Task<IActionResult> Index([FromQuery] CustomerQuery q) =>
        View(new ListViewModel<CustomerDto, CustomerQuery> { Result = await _svc.ListAsync(q), Query = q, Owners = await _scope.AssignableUsersAsync() });

    public async Task<IActionResult> Details(int id)
    {
        var r = await _svc.GetAsync(id);
        if (!r.Succeeded) return NotFound();
        return View(new CustomerDetailsViewModel
        {
            Customer = r.Value!,
            Opportunities = (await _opps.ListAsync(new OpportunityQuery { CustomerId = id, PageSize = 100 })).Items,
            FollowUps = (await _followUps.ListAsync(new FollowUpQuery { RelatedType = RelatedType.Customer, RelatedId = id, PageSize = 50, Desc = true })).Items,
            Activities = (await _activities.ListAsync(new ActivityQuery { CustomerId = id, PageSize = 50 })).Items,
            Leads = (await _leads.ListAsync(new LeadQuery { ConvertedCustomerId = id, PageSize = 20 })).Items,
            History = await _audit.HistoryAsync("Customer", id),
            CanDelete = _me.Role != RoleNames.SalesExecutive,
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        await LoadFormAsync(null);
        return View("Form", new CustomerInput { AssignedTo = _me.UserId });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CustomerInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.CreateAsync(input);
            if (r.Succeeded) { Success($"Customer {r.Value!.CustomerName} created."); return RedirectToAction(nameof(Details), new { id = r.Value.CustomerId }); }
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
        var c = r.Value!;
        await LoadFormAsync(id);
        return View("Form", new CustomerInput
        {
            CustomerName = c.CustomerName, Email = c.Email, Phone = c.Phone, CompanyName = c.CompanyName, Address = c.Address,
            City = c.City, State = c.State, Status = Enum.Parse<CustomerStatus>(c.Status), Notes = c.Notes, AssignedTo = c.AssignedTo,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, CustomerInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.UpdateAsync(id, input);
            if (r.Succeeded) { Success("Customer updated."); return RedirectToAction(nameof(Details), new { id }); }
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
        if (r.Succeeded) { Success("Customer deleted."); return RedirectToAction(nameof(Index)); }
        if (Denied(r) is { } denied) return denied;
        Failure(r.Message ?? "The customer could not be deleted.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task LoadFormAsync(int? id)
    {
        ViewData["EntityId"] = id;
        ViewData["Owners"] = OwnerItems(await _scope.AssignableUsersAsync());
        ViewData["Statuses"] = EnumItems<CustomerStatus>();
    }
}
