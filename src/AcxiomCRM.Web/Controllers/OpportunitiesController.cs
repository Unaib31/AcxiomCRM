using AcxiomCRM.Application.Common;
using AcxiomCRM.Application.Dtos;
using AcxiomCRM.Application.Services;
using AcxiomCRM.Domain;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.Controllers;

public class OpportunitiesController : CrmController
{
    private readonly IOpportunityService _svc;
    private readonly ICustomerService _customers;
    private readonly IFollowUpService _followUps;
    private readonly IAuditQueryService _audit;
    private readonly IAccessScope _scope;
    private readonly ICurrentUser _me;
    private readonly IClock _clock;

    public OpportunitiesController(IOpportunityService svc, ICustomerService customers, IFollowUpService followUps, IAuditQueryService audit,
        IAccessScope scope, ICurrentUser me, IClock clock)
    { _svc = svc; _customers = customers; _followUps = followUps; _audit = audit; _scope = scope; _me = me; _clock = clock; }

    public async Task<IActionResult> Index([FromQuery] OpportunityQuery q) =>
        View(new ListViewModel<OpportunityDto, OpportunityQuery> { Result = await _svc.ListAsync(q), Query = q, Owners = await _scope.AssignableUsersAsync() });

    public async Task<IActionResult> Pipeline(string? assignedTo) =>
        View(new PipelineBoardViewModel { Opportunities = await _svc.BoardAsync(assignedTo), Owners = await _scope.AssignableUsersAsync(), AssignedTo = assignedTo });

    public async Task<IActionResult> Details(int id)
    {
        var r = await _svc.GetAsync(id);
        if (!r.Succeeded) return NotFound();
        return View(new OpportunityDetailsViewModel
        {
            Opportunity = r.Value!,
            FollowUps = (await _followUps.ListAsync(new FollowUpQuery { RelatedType = RelatedType.Opportunity, RelatedId = id, PageSize = 50, Desc = true })).Items,
            History = await _audit.HistoryAsync("Opportunity", id),
            CanDelete = _me.Role != RoleNames.SalesExecutive,
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId)
    {
        await LoadFormAsync(null);
        return View("Form", new OpportunityInput
        {
            CustomerId = customerId, AssignedTo = _me.UserId, Stage = OpportunityStage.Qualification, Probability = 20,
            ExpectedCloseDate = _clock.Today.AddDays(30),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create(OpportunityInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.CreateAsync(input);
            if (r.Succeeded) { Success($"Opportunity {r.Value!.OpportunityName} created."); return RedirectToAction(nameof(Details), new { id = r.Value.OpportunityId }); }
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
        var o = r.Value!;
        if (o.Stage is nameof(OpportunityStage.Won) or nameof(OpportunityStage.Lost)) { Failure("Closed opportunities cannot be modified."); return RedirectToAction(nameof(Details), new { id }); }
        await LoadFormAsync(id);
        return View("Form", new OpportunityInput
        {
            OpportunityName = o.OpportunityName, CustomerId = o.CustomerId, Amount = o.Amount, Stage = Enum.Parse<OpportunityStage>(o.Stage), Probability = o.Probability,
            ExpectedCloseDate = o.ExpectedCloseDate, Source = o.Source is null ? null : Enum.Parse<LeadSource>(o.Source), Notes = o.Notes, OutcomeNotes = o.OutcomeNotes, AssignedTo = o.AssignedTo,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, OpportunityInput input)
    {
        if (ModelState.IsValid)
        {
            var r = await _svc.UpdateAsync(id, input);
            if (r.Succeeded) { Success("Opportunity updated."); return RedirectToAction(nameof(Details), new { id }); }
            if (Denied(r) is { } d) return d;
            AddErrors(r);
        }
        await LoadFormAsync(id);
        return View("Form", input);
    }

    [HttpPost]
    public async Task<IActionResult> ChangeStage(int id, StageChangeInput input, string? returnTo)
    {
        var r = ModelState.IsValid ? await _svc.ChangeStageAsync(id, input) : null;
        if (r is { Succeeded: true }) Success($"Opportunity moved to {input.Stage}.");
        else if (r is not null && Denied(r) is { } denied) return denied;
        else Failure(r?.Message ?? "The stage could not be changed.");
        return returnTo == "pipeline" ? RedirectToAction(nameof(Pipeline)) : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _svc.DeleteAsync(id);
        if (r.Succeeded) { Success("Opportunity deleted."); return RedirectToAction(nameof(Index)); }
        if (Denied(r) is { } denied) return denied;
        Failure(r.Message ?? "The opportunity could not be deleted.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task LoadFormAsync(int? id)
    {
        ViewData["EntityId"] = id;
        ViewData["Owners"] = OwnerItems(await _scope.AssignableUsersAsync());
        ViewData["Customers"] = (await _customers.LookupAsync()).Select(c => new SelectListItem(c.Name, c.Id.ToString())).ToList();
        ViewData["Stages"] = EnumItems<OpportunityStage>(only: id is null ? new[] { OpportunityStage.Qualification, OpportunityStage.Proposal, OpportunityStage.Negotiation } : null);
        ViewData["Sources"] = EnumItems<LeadSource>();
        ViewData["Today"] = _clock.Today.ToString("yyyy-MM-dd");
    }
}
