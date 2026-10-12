using AcxiomCRM.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.ViewComponents;

public class NotificationsViewComponent : ViewComponent
{
    private readonly INotificationService _svc;
    public NotificationsViewComponent(INotificationService svc) => _svc = svc;
    public async Task<IViewComponentResult> InvokeAsync() => View(await _svc.ForCurrentUserAsync());
}
