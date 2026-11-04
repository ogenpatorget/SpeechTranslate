namespace ChurchTranslate.Api;

public sealed class ApiOptions
{
    public string DataPath { get; set; } = "data";
    public bool EphemeralMode { get; set; }
    public string ApprovedAdminEmails { get; set; } = "";
    public string AdminOrigin { get; set; } = "http://127.0.0.1:5301";
    public string ListenerOrigin { get; set; } = "http://127.0.0.1:5302";
    public string? PublicAdminUrl { get; set; }
    public string? PublicListenerUrl { get; set; }
    public bool CrossSiteCookies { get; set; }
    public string? BootstrapPlatformAdminEmail { get; set; }
    public bool DevOpenAccess { get; set; }
    public string TranslationProvider { get; set; } = "Mock";
    public decimal EstimatedUsdPerAudioMinute { get; set; } = 0.034m;
    public TimeSpan MaxBroadcastDuration { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxListenersPerBroadcast { get; set; } = 100;
}
