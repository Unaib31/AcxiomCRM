using AcxiomCRM.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.Controllers;

public abstract class CrmController : Controller
{
    protected void Success(string msg) => TempData["Success"] = msg;
    protected void Failure(string msg) => TempData["Error"] = msg;

    /// <summary>Copies service validation errors onto the model state so the form shows them inline.</summary>
    protected void AddErrors(ServiceResult r)
    {
        foreach (var (field, msgs) in r.Errors)
            foreach (var m in msgs.Distinct())
                ModelState.AddModelError(field ?? "", m);
        if (r.Errors.Count == 0 && !string.IsNullOrWhiteSpace(r.Message)) ModelState.AddModelError("", r.Message);
    }

    /// <summary>Not-found and forbidden map to standard pages; callers handle validation/conflict themselves.</summary>
    protected IActionResult? Denied(ServiceResult r) => r.Status switch
    {
        ResultStatus.NotFound => NotFound(),
        ResultStatus.Forbidden => Forbid(),
        _ => null,
    };

    protected static List<SelectListItem> OwnerItems(IEnumerable<UserOption> owners, string? selected = null) =>
        owners.Select(o => new SelectListItem($"{o.Name} ({o.Role})", o.Id, o.Id == selected)).ToList();

    protected static List<SelectListItem> EnumItems<T>(T? selected = null, IEnumerable<T>? only = null) where T : struct, Enum =>
        (only ?? Enum.GetValues<T>()).Select(v => new SelectListItem(Display(v), v.ToString(), selected is { } s && EqualityComparer<T>.Default.Equals(s, v))).ToList();

    public static string Display(Enum value)
    {
        var f = value.GetType().GetField(value.ToString());
        var a = f?.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.DisplayAttribute), false).FirstOrDefault() as System.ComponentModel.DataAnnotations.DisplayAttribute;
        return a?.Name ?? value.ToString();
    }
}
