using Microsoft.AspNetCore.Mvc;
using PricingTool.Agent.Operations;

namespace PricingTool.Web.ViewComponents;

/// <summary>The count next to "Exceptions" in the navigation: what is waiting for a person. A failure to count
/// never breaks the page; the badge is simply left out.</summary>
public class WaitingBadgeViewComponent : ViewComponent
{
    private readonly ExceptionsInbox _inbox;
    private readonly ILogger<WaitingBadgeViewComponent> _logger;

    public WaitingBadgeViewComponent(ExceptionsInbox inbox, ILogger<WaitingBadgeViewComponent> logger)
    {
        _inbox = inbox;
        _logger = logger;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        try
        {
            return View((await _inbox.LoadAsync()).WaitingCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count the exceptions inbox for the navigation badge.");
            return Content(string.Empty);
        }
    }
}
