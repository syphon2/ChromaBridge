namespace GameLightSync;

/// <summary>
/// Passive monitor of the keyboard's HID++ traffic. It only listens (no requests after the initial feature lookup,
/// so no extra radio traffic): HID delivers input reports to every open handle, so we also see the keyboard's
/// answers to G HUB's and Windows' lighting commands, plus its wake-up / reconnect notifications. Everything that can
/// change the keyboard lighting is written to the log, to find out what changes it.
/// </summary>
internal sealed class KeyboardWatcher : IDisposable
{
    private const int OurSoftwareId = 0x0B;
    private readonly List<Hidpp> _collections = [];
    private readonly Dictionary<byte, ushort> _featureIds = [];
    private readonly Dictionary<string, int> _otherLighting = [];
    private readonly Dictionary<string, DateTime> _lastDetail = [];
    private readonly object _gate = new();
    private System.Threading.Timer? _flush;

    public void Start() => Task.Run(() =>
    {
        try { Open(); }
        catch (Exception ex) { Log.Write($"Keyboard monitor could not start: {ex.Message}"); }
    });

    private void Open()
    {
        Hidpp? keyboard = null;
        foreach (var dev in Hidpp.OpenLongCollections())
        {
            if (keyboard == null && IsG915(dev)) keyboard = dev;
            else dev.Dispose();
        }
        if (keyboard == null) { Log.Write("Keyboard monitor: G915 not found"); return; }

        // feature index -> feature id, to name what we see
        var set = keyboard.FeatureIndex(0x0001);
        var count = set == null ? 0 : keyboard.Request(set.Value, 0, [])?[0] ?? 0;
        for (var i = 1; i <= count; i++)
        {
            var f = keyboard.Request(set!.Value, 1, [(byte)i]);
            if (f != null) _featureIds[(byte)i] = (ushort)(f[0] << 8 | f[1]);
        }

        // also listen on the short-report collection: receiver connection notifications arrive there
        var pid = System.Text.RegularExpressions.Regex.Match(keyboard.Path, "pid_([0-9a-f]{4})").Groups[1].Value;
        _collections.Add(keyboard);
        foreach (var dev in Hidpp.OpenVendorCollections(0x046D, Convert.ToUInt16(pid, 16), 7)) _collections.Add(dev);
        foreach (var c in _collections) c.ReportReceived += OnReport;

        _flush = new System.Threading.Timer(_ => Flush(), null, 2000, 2000);
        Log.Write($"Keyboard monitor: listening to the G915 ({_collections.Count} HID++ channels, {_featureIds.Count} features)");
    }

    private static bool IsG915(Hidpp dev)
    {
        dev.DeviceIndex = 0xFF;
        if (dev.Request(0x00, 1, [0, 0, 0x5A], 200) == null) return false;
        var idx = dev.FeatureIndex(0x0005);
        var name = idx == null ? null : dev.Request(idx.Value, 1, [0]);
        return name != null && System.Text.Encoding.ASCII.GetString(name).Contains("G915");
    }

    private void OnReport(byte[] r)
    {
        if (r.Length < 7) return;

        // HID++ 1.0 receiver notification: device (dis)connected - e.g. the keyboard waking up from sleep
        if (r[0] == 0x10 && r[2] is 0x40 or 0x41)
        {
            var connected = r[2] == 0x41 && (r[4] & 0x40) == 0;
            Log.Write($"Keyboard monitor: receiver reports keyboard {(connected ? "connected" : "disconnected")} (raw {Convert.ToHexString(r, 0, 7)})");
            return;
        }
        if (r[0] != 0x11 && r[0] != 0x10) return;

        // HID++ 2.0 error reply for someone else's request
        if (r[2] == 0xFF)
        {
            if ((r[4] & 0x0F) != OurSoftwareId && IsLighting(r[3]))
                Count($"error 0x{r[5]:X2} on {Name(r[3])} fn{r[4] >> 4} (sw {r[4] & 0x0F})");
            return;
        }

        var feature = r[2];
        var fn = r[3] >> 4;
        var sw = r[3] & 0x0F;
        if (sw == 0)
        {
            // unsolicited event from the keyboard
            var id = _featureIds.GetValueOrDefault(feature);
            if (id == 0x1004) return; // battery level updates
            Detail($"event {id:X4}/{fn}", $"Keyboard monitor: event from {Name(feature)} fn{fn}: {Convert.ToHexString(r, 4, Math.Min(8, r.Length - 4))}");
            return;
        }
        if (sw == OurSoftwareId || !IsLighting(feature)) return;

        // another program (G HUB, Windows' LampArray service) changed the lighting
        var key = $"{Name(feature)} fn{fn} (sw {sw})";
        Count(key);
        if (_featureIds.GetValueOrDefault(feature) == 0x8071)
            Detail(key, $"Keyboard monitor: other program -> {key}: {Convert.ToHexString(r, 4, Math.Min(8, r.Length - 4))}");
    }

    private bool IsLighting(byte feature) => _featureIds.GetValueOrDefault(feature) is 0x8071 or 0x8081 or 0x8040;

    private string Name(byte feature) => _featureIds.GetValueOrDefault(feature) switch
    {
        0x8071 => "RgbEffects(0x8071)", 0x8081 => "PerKey(0x8081)", 0x8040 => "Brightness(0x8040)",
        0x1D4B => "WirelessStatus(0x1D4B)", var id => $"0x{id:X4}",
    };

    private void Count(string key)
    {
        lock (_gate) _otherLighting[key] = _otherLighting.GetValueOrDefault(key) + 1;
    }

    /// <summary>Logs a detail line at most once per second per kind.</summary>
    private void Detail(string kind, string line)
    {
        lock (_gate)
        {
            if (_lastDetail.TryGetValue(kind, out var last) && (DateTime.UtcNow - last).TotalSeconds < 1) return;
            _lastDetail[kind] = DateTime.UtcNow;
        }
        Log.Write(line);
    }

    private void Flush()
    {
        string? line = null;
        lock (_gate)
        {
            if (_otherLighting.Count > 0)
                line = "Keyboard monitor: other programs' lighting commands in the last 2 s: " +
                       string.Join(", ", _otherLighting.Select(kv => $"{kv.Key} x{kv.Value}"));
            _otherLighting.Clear();
        }
        if (line != null) Log.Write(line);
    }

    public void Dispose()
    {
        _flush?.Dispose();
        foreach (var c in _collections) { c.ReportReceived -= OnReport; c.Dispose(); }
        _collections.Clear();
    }
}
