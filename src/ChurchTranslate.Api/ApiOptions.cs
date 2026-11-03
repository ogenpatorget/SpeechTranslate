namespace ChurchTranslate.Api;

public sealed class ApiOptions
{
    public string DataPath { get; set; } = "data";
    public string AdminOrigin { get; set; } = "http://127.0.0.1:5301";
    public string ListenerOrigin { get; set; } = "http://127.0.0.1:5302";
    public string? BootstrapPlatformAdminEmail { get; set; }
    public bool DevOpenAccess { get; set; }
    public string TranslationProvider { get; set; } = "Mock";
    public decimal EstimatedUsdPerAudioMinute { get; set; } = 0.034m;
}
