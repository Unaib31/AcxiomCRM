using AcxiomCRM.Application.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.Controllers;

public class DashboardController : CrmController
{
    private readonly IDashboardService _svc;
    public DashboardController(IDashboardService svc) => _svc = svc;

    public async Task<IActionResult> Index([FromQuery] DashboardQuery q) => View(await _svc.GetAsync(q));
}

public class SearchController : CrmController
{
    private readonly ISearchService _svc;
    public SearchController(ISearchService svc) => _svc = svc;

    public async Task<IActionResult> Index(string? q) =>
        View(new SearchViewModel { Query = q?.Trim() ?? "", Hits = await _svc.SearchAsync(q ?? "") });
}

[AllowAnonymous]
public class HomeController : Controller
{
    [Route("Home/Error")]
    public IActionResult Error()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        HttpContext.Response.StatusCode = 500;
        if (feature?.Path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) == true)
            return Problem(statusCode: 500, title: "An unexpected error occurred.");
        return View("Error", new ErrorViewModel { TraceId = HttpContext.TraceIdentifier });
    }

    [Route("Home/StatusCode")]
    public IActionResult StatusCodePage(int code)
    {
        var vm = code switch
        {
            401 => new ErrorViewModel { Code = 401, Title = "Sign in required", Message = "Please sign in to continue." },
            403 => new ErrorViewModel { Code = 403, Title = "Access denied", Message = "You do not have permission to view this page." },
            404 => new ErrorViewModel { Code = 404, Title = "Page not found", Message = "The page or record you are looking for does not exist, or you do not have access to it." },
            429 => new ErrorViewModel { Code = 429, Title = "Too many requests", Message = "Please wait a moment and try again." },
            _ => new ErrorViewModel { Code = code, Title = "Something went wrong", Message = "The request could not be completed." },
        };
        vm.TraceId = HttpContext.TraceIdentifier;
        Response.StatusCode = code;
        return View("Error", vm);
    }
}
