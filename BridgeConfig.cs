using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLightSync;

internal sealed class BridgeConfig
{
    /// <summary>Only mirror these Chroma apps (exe names, case-insensitive). Empty = any app.</summary>
    public List<string> Apps { get; set; } = [];

    /// <summary>Never mirror these Chroma apps (e.g. Razer's own tools).</summary>
    public List<string> IgnoreApps { get; set; } = [];

    /// <summary>Set to true if red and blue appear swapped on the keyboard (check with the test pattern).</summary>
    public bool SwapRedBlue { get; set; }

    /// <summary>0.0 - 1.0 multiplier for all colors.</summary>
    public double Brightness { get; set; } = 1.0;

    public int MaxFps { get; set; } = 60;

    /// <summary>
    /// "hidpp": per-key directly over HID++ (Windows Dynamic Lighting must be turned off).
    /// "dynamicLighting": per-key via Windows Dynamic Lighting (needs the package install + Background light control).
    /// "ghub": Logitech LED SDK through G HUB (on the G915 X only whole-keyboard colors work).
    /// </summary>
    public string Output { get; set; } = "hidpp";

    public bool UseHidpp => string.Equals(Output, "hidpp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Frames per second for the HID++ output (5-30). The G915 X drops frames closer than ~33 ms.</summary>
    public int HidppFps { get; set; } = 25;

    /// <summary>
    /// With the HID++ output: take Windows Dynamic Lighting control while mirroring (without sending anything through
    /// it) so Windows' own effect pauses, and give it back when the game stops - your normal lighting returns.
    /// Needs GameLightSync on top of "Background light control".
    /// </summary>
    public bool HoldDynamicLighting { get; set; } = true;

    /// <summary>Hand the keyboard back when the game has sent no Chroma frame for this many seconds (e.g. alt-tab).</summary>
    public double ReleaseAfterSeconds { get; set; } = 3;

    /// <summary>Updates per second sent to Dynamic Lighting keyboards (5-30).</summary>
    public int DynamicLightingFps { get; set; } = 30;

    /// <summary>
    /// "budget": at most <see cref="LampsPerUpdate"/> keys per update, furthest from their correct color first.
    /// "changedKeys": every changed key, plus a full resend once per second.
    /// </summary>
    public string DynamicLightingStrategy { get; set; } = "changedKeys";

    /// <summary>Most keys sent per update with the "budget" strategy.</summary>
    public int LampsPerUpdate { get; set; } = 24;

    public int EffectiveLampBudget =>
        string.Equals(DynamicLightingStrategy, "changedKeys", StringComparison.OrdinalIgnoreCase)
            ? 0 : Math.Clamp(LampsPerUpdate, 1, 200);

    public bool UseDynamicLighting => string.Equals(Output, "dynamicLighting", StringComparison.OrdinalIgnoreCase);

    /// <summary>"perKey" sets each key individually (works on all G HUB keyboards); "bitmap" sends one 21x6 image per frame.</summary>
    public string KeyboardMode { get; set; } = "perKey";

    /// <summary>ISO (e.g. German) layout: '#' next to Enter and '&lt;&gt;' next to left Shift.</summary>
    public bool IsoLayout { get; set; } = true;

    public bool PerKeyMode => !string.Equals(KeyboardMode, "bitmap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Mirror the Razer mouse logo color onto the Logitech mouse (and other non-per-key RGB devices).</summary>
    public bool Mouse { get; set; } = true;

    /// <summary>Mirror Razer macro keys M1-M5 to G1-G5 and the Razer logo to the G logo.</summary>
    public bool GKeysAndLogo { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "bridge.json");

    public static BridgeConfig Load()
    {
        try
        {
            var defaults = Path.Combine(AppContext.BaseDirectory, "bridge.default.json");
            if (!File.Exists(FilePath) && File.Exists(defaults)) File.Copy(defaults, FilePath);

            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read {FilePath}: {ex.Message} - using defaults");
        }
        return new();
    }

    public bool ShouldMirror(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;
        var exe = Path.GetFileName(appName.Trim());
        if (IgnoreApps.Any(a => string.Equals(a, exe, StringComparison.OrdinalIgnoreCase))) return false;
        return Apps.Count == 0 || Apps.Any(a => string.Equals(a, exe, StringComparison.OrdinalIgnoreCase));
    }
}
