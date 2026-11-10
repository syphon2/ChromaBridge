using RazerSdkReader.Structures;

namespace ChromaBridge;

/// <summary>
/// Drives the keyboard directly over HID++ 2.0 (feature 0x8081 per-key lighting, 0x8071 software control), next to G HUB.
/// Frames are grouped by color (13 keys per message), every write and commit is acknowledged, and a commit that the
/// keyboard reports as busy is retried, so a frame is applied completely or not at all. While it runs, the Bridge holds
/// Windows Dynamic Lighting control (sending nothing through it), so the Logitech LampArray service stays quiet.
/// </summary>
internal sealed class HidppOutput : IFrameOutput
{
    private Hidpp? _dev;
    private byte _perKey, _effects;
    private string _name = "";
    private DateTime _nextOpenAttempt = DateTime.MinValue;
    private DateTime _nextFullRefresh = DateTime.MinValue;
    private bool _claimedFully;
    private int[] _razerToCode = [];
    private (byte Code, int RazerIndex, int Fallback)[] _extras = [];
    private readonly Dictionary<byte, int> _shown = new(); // key code -> color the keyboard currently shows
    private int _consecutiveFailures;

    public bool Active { get; private set; }

    public void Start(bool isoLayout)
    {
        if (Active) return;
        Active = true;
        _razerToCode = KeyMap.BuildRazerToHidppCode(isoLayout);
        _extras = KeyMap.HidppExtraKeys;
    }

    public void Stop()
    {
        if (!Active) return;
        Active = false;
        if (_dev != null) Close(release: true);
        else if (_saved is { } s && SetMode(s.Mode, s.Flags))
        {
            // the connection was lost during the game after we had claimed control: hand back anyway
            WriteStateFile(s.Mode, s.Flags, claimed: false);
            Log.Write($"HID++: keyboard handed back (mode {s.Mode}, flags {s.Flags})");
            _saved = null;
        }
    }

    // ---- handing the keyboard back ----
    // Logitech's Dynamic Lighting service keeps the keyboard in software-control mode 3 / flags 6 (power management +
    // NV config). It only repaints colors and does not set that mode again, so after a game the keyboard must get
    // exactly that mode back - "release completely" (mode 0) leaves it to its firmware, which dims it when idle.

    /// <summary>The mode the Logitech service sets when the keyboard connects (read from the G915 X).</summary>
    private static readonly (byte Mode, byte Flags) LogitechServiceMode = (3, 6);

    private (byte Mode, byte Flags)? _saved;

    private static string StateFile => Path.Combine(AppContext.BaseDirectory, "keyboard-state.txt");

    /// <summary>
    /// At start: if a previous run ended while it had control (crash, killed), put the keyboard's saved mode back.
    /// Otherwise the keyboard is left alone.
    /// </summary>
    public void RestoreAfterUncleanExit()
    {
        var saved = ReadStateFile();
        if (saved is not { Claimed: true }) return;
        if (SetMode(saved.Value.Mode, saved.Value.Flags))
        {
            WriteStateFile(saved.Value.Mode, saved.Value.Flags, claimed: false);
            Log.Write($"HID++: previous run ended while in control - keyboard handed back (mode {saved.Value.Mode}, flags {saved.Value.Flags})");
        }
        else Log.Write("HID++: previous run ended while in control, but the keyboard did not answer - will retry at next start");
    }

    /// <summary>Tray "Restore normal lighting": put the Logitech service's mode back right now.</summary>
    public void RestoreServiceMode()
    {
        if (Active) return;
        var (mode, flags) = LogitechServiceMode;
        if (SetMode(mode, flags))
        {
            WriteStateFile(mode, flags, claimed: false);
            Log.Write($"HID++: keyboard set back to the Logitech service mode (mode {mode}, flags {flags})");
        }
    }

    /// <summary>Opens the keyboard just for setting its software-control mode.</summary>
    private static bool SetMode(byte mode, byte flags)
    {
        var done = false;
        foreach (var dev in Hidpp.OpenLongCollections())
        {
            using (dev)
            {
                if (done) continue;
                dev.DeviceIndex = 0xFF;
                if (dev.Request(0x00, 1, [0, 0, 0x5A], 300) == null) continue;
                var name = ReadName(dev);
                if (!name.Contains("G915", StringComparison.OrdinalIgnoreCase)) continue;
                var effects = dev.FeatureIndex(0x8071);
                var error = effects == null ? -2 : dev.RequestWithError(effects.Value, 5, [1, mode, flags]).Error;
                done = error == 0;
                if (!done) Log.Write($"HID++: setting mode {mode}/{flags} on {name} failed (error {error})");
            }
        }
        return done;
    }

    private static (byte Mode, byte Flags, bool Claimed)? ReadStateFile()
    {
        try
        {
            var p = File.ReadAllText(StateFile).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (byte.Parse(p[0]), byte.Parse(p[1]), p[2] == "1");
        }
        catch { return null; }
    }

    private static void WriteStateFile(byte mode, byte flags, bool claimed)
    {
        try { File.WriteAllText(StateFile, $"{mode} {flags} {(claimed ? 1 : 0)}"); } catch { }
    }

    public string Summary() => _dev == null ? "keyboard not found (HID++)" : $"{_name} via HID++";

    public void Render(ChromaColor[] frame, Func<ChromaColor, (int R, int G, int B)> scale)
    {
        if (!Active || !EnsureOpen()) return;

        var full = DateTime.UtcNow >= _nextFullRefresh;
        if (full) _nextFullRefresh = DateTime.UtcNow.AddSeconds(1);

        var target = new Dictionary<byte, int>(128);
        for (var i = 0; i < _razerToCode.Length; i++)
            if (_razerToCode[i] >= 0) target[(byte)_razerToCode[i]] = Pack(scale(frame[i]));
        foreach (var (code, razer, fallback) in _extras)
        {
            var c = frame[razer];
            target[code] = Pack(scale(c.R + c.G + c.B == 0 ? frame[fallback] : c));
        }

        // the previous frame's colors are already in the keyboard but its commit was refused as BUSY:
        // only repeat the commit - sending everything again would keep the keyboard busy for good
        if (_commitPending)
        {
            if (!Commit()) return;
            _commitPending = false;
        }

        var changed = target.Where(kv => full || !_shown.TryGetValue(kv.Key, out var c) || c != kv.Value)
                            .GroupBy(kv => kv.Value, kv => kv.Key).ToList();
        if (changed.Count == 0) return;

        if (!SendFrame(changed) && !_commitPending)
        {
            _shown.Clear(); // unknown state: resend everything next time
            // reconnect only when the keyboard really stopped answering (timeouts); a rejected frame is just resent
            if (_lastError == -1 && ++_consecutiveFailures >= 5)
            {
                Log.Write("HID++: keyboard stopped answering, reconnecting");
                Close(release: false);
            }
            return;
        }
        _consecutiveFailures = 0;
        foreach (var kv in target) _shown[kv.Key] = kv.Value; // also when only the commit is pending: colors are staged

        if (!_claimedFully && !_commitPending)
        {
            // after the first complete frame: effect control + NV config (flags 5), as G HUB / OpenRGB do
            _dev!.RequestWithError(_effects, 5, [1, 3, 5]);
            _claimedFully = true;
        }
    }

    private static int Pack((int R, int G, int B) c) => c.R << 16 | c.G << 8 | c.B;

    private bool SendFrame(List<IGrouping<int, byte>> changed)
    {
        var singles = new List<(byte Key, int Color)>();
        foreach (var group in changed)
        {
            var keys = group.ToList();
            if (keys.Count <= 4) { singles.AddRange(keys.Select(k => (k, group.Key))); continue; }
            for (var i = 0; i < keys.Count; i += 13)
            {
                var chunk = keys.Skip(i).Take(13).ToList();
                var p = new byte[16];
                p[0] = (byte)(group.Key >> 16); p[1] = (byte)(group.Key >> 8); p[2] = (byte)group.Key;
                for (var j = 0; j < chunk.Count; j++) p[3 + j] = chunk[j];
                if (chunk.Count < 13) p[3 + chunk.Count] = 0xFF;
                if (!Write(6, p)) return false;
            }
        }
        for (var i = 0; i < singles.Count; i += 4)
        {
            var p = new byte[16];
            var chunk = singles.Skip(i).Take(4).ToList();
            for (var j = 0; j < chunk.Count; j++)
            {
                p[4 * j] = chunk[j].Key;
                p[4 * j + 1] = (byte)(chunk[j].Color >> 16); p[4 * j + 2] = (byte)(chunk[j].Color >> 8); p[4 * j + 3] = (byte)chunk[j].Color;
            }
            if (!Write(1, p)) return false;
        }

        if (Commit()) return true;
        // BUSY for too long: the writes are staged in the keyboard and latch with the next commit
        _commitPending = _lastError == Hidpp.ErrorBusy;
        return false;
    }

    private bool _commitPending;

    /// <summary>
    /// Shows the staged colors. BUSY = the keyboard is still applying the previous frame (longer the more keys
    /// changed); wait for it, which also paces the frame rate to what the keyboard can do.
    /// </summary>
    private bool Commit()
    {
        var deadline = Environment.TickCount64 + 400;
        while (true)
        {
            var (_, error) = _dev!.RequestWithError(_perKey, 7, new byte[16], 100);
            if (error == 0) return true;
            if (error == Hidpp.ErrorBusy && Environment.TickCount64 < deadline) { Thread.Sleep(3); continue; }
            NoteError("commit", error);
            return false;
        }
    }

    /// <summary>One per-key write; BUSY (the keyboard is still applying a frame, ours or another program's) is retried.</summary>
    private bool Write(byte function, byte[] p)
    {
        var deadline = Environment.TickCount64 + 150;
        while (true)
        {
            var error = _dev!.RequestWithError(_perKey, function, p, 250).Error;
            if (error == 0) return true;
            if (error == Hidpp.ErrorBusy && Environment.TickCount64 < deadline) { Thread.Sleep(2); continue; }
            NoteError($"write fn{function}", error);
            return false;
        }
    }

    private DateTime _nextErrorLog = DateTime.MinValue;
    private int _lastError;

    /// <summary>Remembers the last failure; logged at most every 5 s.</summary>
    private void NoteError(string what, int error)
    {
        _lastError = error;
        if (DateTime.UtcNow < _nextErrorLog) return;
        _nextErrorLog = DateTime.UtcNow.AddSeconds(5);
        Log.Write($"HID++: {what} failed with {(error == -1 ? "no answer (timeout)" : $"error 0x{error:X2}")}");
    }

    private bool EnsureOpen()
    {
        if (_dev != null) return true;
        if (DateTime.UtcNow < _nextOpenAttempt) return false;
        _nextOpenAttempt = DateTime.UtcNow.AddSeconds(5);

        foreach (var dev in Hidpp.OpenLongCollections())
        {
            if (_dev == null && TryUse(dev)) continue;
            dev.Dispose(); // not the keyboard, or the keyboard was already found on another collection
        }
        if (_dev == null && DateTime.UtcNow >= _nextNotFoundLog)
        {
            _nextNotFoundLog = DateTime.UtcNow.AddMinutes(1);
            Log.Write("HID++: G915 keyboard not found or not answering (asleep / switched off?), retrying every 5 s");
        }
        return _dev != null;
    }

    private DateTime _nextNotFoundLog = DateTime.MinValue;

    private bool TryUse(Hidpp dev)
    {
        foreach (byte index in new byte[] { 0xFF, 1, 2 })
        {
            dev.DeviceIndex = index;
            if (dev.Request(0x00, 1, [0, 0, 0x5A], 200) == null) continue;
            var perKey = dev.FeatureIndex(0x8081);
            var effects = dev.FeatureIndex(0x8071);
            var name = ReadName(dev);
            if (perKey == null || effects == null || !name.Contains("G915", StringComparison.OrdinalIgnoreCase)) continue;

            _dev = dev; _perKey = perKey.Value; _effects = effects.Value; _name = name;
            _shown.Clear(); _claimedFully = false; _consecutiveFailures = 0; _commitPending = false;

            // remember the mode we have to hand back (once per session: a reconnect would read our own claim)
            if (_saved == null)
            {
                var current = dev.RequestWithError(_effects, 5, [0]).Data;
                (byte Mode, byte Flags) mode = current == null ? LogitechServiceMode : (current[1], current[2]);
                // mode 0 (left by an older ChromaBridge) or our own flags 5 (crash during a game): use the service mode
                if (mode.Mode == 0 || mode.Flags == 5) mode = LogitechServiceMode;
                _saved = mode;
                WriteStateFile(mode.Mode, mode.Flags, claimed: true);
            }

            // claim software control; the firmware keeps rendering until our first frame replaces it
            var claim = dev.RequestWithError(_effects, 5, [1, 3, 6]).Error;
            Log.Write($"HID++: {name} (device 0x{index:X2}), software control {(claim == 0 ? "claimed" : $"error {claim}")}, " +
                      $"will hand back mode {_saved.Value.Mode}/flags {_saved.Value.Flags}");
            return true;
        }
        return false;
    }

    private static string ReadName(Hidpp dev)
    {
        var idx = dev.FeatureIndex(0x0005);
        if (idx == null) return "";
        var len = dev.Request(idx.Value, 0, [])?[0] ?? 0;
        var name = "";
        while (name.Length < len)
        {
            var part = dev.Request(idx.Value, 1, [(byte)name.Length]);
            if (part == null || part.All(b => b == 0)) break;
            name += System.Text.Encoding.ASCII.GetString(part).TrimEnd('\0');
        }
        return name;
    }

    private void Close(bool release)
    {
        if (_dev == null) return;
        if (release)
        {
            // hand back exactly the mode the keyboard had (normally the Logitech service's mode 3 / flags 6);
            // the keyboard announces the change and the service repaints the Windows lighting
            var (mode, flags) = _saved ?? LogitechServiceMode;
            var error = -1;
            try { error = _dev.RequestWithError(_effects, 5, [1, mode, flags]).Error; } catch { }
            if (error == 0)
            {
                WriteStateFile(mode, flags, claimed: false);
                _saved = null;
            }
            Log.Write($"HID++: keyboard handed back (mode {mode}, flags {flags}){(error == 0 ? "" : $" - error {error}, will retry at next start")}");
        }
        _dev.Dispose();
        _dev = null;
        _shown.Clear();
    }
}
