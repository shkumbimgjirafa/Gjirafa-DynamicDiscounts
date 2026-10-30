using Microsoft.AspNetCore.Mvc.ViewFeatures;
using PricingTool.Data.Entities;

namespace PricingTool.Web.Services;

/// <summary>
/// Surfaces the current layer's ISO currency code (EUR / MKD / ALL) to views. Every page is
/// scoped to a single layer (see <see cref="LayerContextFilter"/>), and prices come back in that
/// layer's native currency with no FX conversion — so the displayed values and this label always
/// agree. Falls back to EUR if no layer context is present (e.g. error pages).
/// </summary>
public static class ViewCurrencyExtensions
{
    public static string CurrencyCode(this ViewDataDictionary viewData)
        => (viewData[LayerContextFilter.CurrentLayerKey] as Layer)?.Currency ?? "EUR";
}
