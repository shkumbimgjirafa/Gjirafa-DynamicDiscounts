using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PricingTool.Web.Services;

/// <summary>
/// Global action filter that exposes the current layer + active-layer list to every view (for the
/// nav layer-switcher and brand title) without each controller having to populate ViewData.
/// </summary>
public class LayerContextFilter : IAsyncActionFilter
{
    public const string CurrentLayerKey = "CurrentLayer";
    public const string ActiveLayersKey = "ActiveLayers";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.Controller is Controller controller)
        {
            var layers = context.HttpContext.RequestServices.GetRequiredService<CurrentLayerService>();
            var ct = context.HttpContext.RequestAborted;
            controller.ViewData[ActiveLayersKey] = await layers.GetActiveLayersAsync(ct);
            controller.ViewData[CurrentLayerKey] = await layers.GetCurrentAsync(ct);
        }

        await next();
    }
}
