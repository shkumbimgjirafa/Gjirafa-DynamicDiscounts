namespace PricingTool.Data.Entities;

public static class ToolSettingKeys
{
    /// <summary>Daily run time, UTC "HH:mm".</summary>
    public const string RunTimeUtc = "Schedule.RunTimeUtc";

    /// <summary>Cadence in hours between scheduled runs (default 24).</summary>
    public const string CadenceHours = "Schedule.CadenceHours";

    /// <summary>Set by the worker after each scheduled run to compute the next one.</summary>
    public const string LastScheduledRunUtc = "Schedule.LastScheduledRunUtc";
}

/// <summary>Small key/value store for admin-editable runtime settings (schedule etc.).</summary>
public class ToolSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = "";
}
