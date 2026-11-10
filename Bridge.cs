using RazerSdkReader;
using RazerSdkReader.Structures;

namespace GameLightSync;

/// <summary>
/// Reads what games send to the Razer Chroma SDK (via the Chroma SDK service's shared memory, no injection)
/// and replays it on Logitech G HUB devices through the official Logitech LED SDK.
/// All Logitech SDK calls happen on one worker thread.
/// </summary>
internal sealed class Bridge : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;
    private volatile bool _stopping;

    private ChromaReader? _reader;
    private BridgeConfig _config;
    private readonly DynamicLightingOutput _dynamicLighting = new();
    private readonly HidppOutput _hidpp = new();
    private DateTime _lastRazerFrame = DateTime.MinValue;
    private bool _wasMirroring, _restoredAtStart, _restorePending;
    private readonly Queue<DateTime> _recentFrames = new(); // guarded by _gate
    private readonly KeyboardWatcher _watcher = new(); // passive: only logs what changes the keyboard lighting
    private (string Name, DateTime Checked, bool Running) _processCheck;

    /// <summary>Game frames received during the last second.</summary>
    private int RecentFrameCount()
    {
        var since = DateTime.UtcNow.AddSeconds(-1);
        lock (_gate) return _recentFrames.Count(t => t >= since);
    }

    /// <summary>Whether the Chroma app's process runs (checked at most once per second).</summary>
    private bool IsProcessRunning(string app)
    {
        var name = Path.GetFileNameWithoutExtension(app);
        if (name.Length == 0) return false;
        if (_processCheck.Name == name && (DateTime.UtcNow - _processCheck.Checked).TotalSeconds < 1) return _processCheck.Running;
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        var running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        _processCheck = (name, DateTime.UtcNow, running);
        return running;
    }

    /// <summary>Tray: give the keyboard back to the normal lighting now (runs on the worker thread).</summary>
    public void RequestRestoreNormalLighting()
    {
        _restorePending = true;
        _signal.Set();
    }

    /// <summary>
    /// Manual repair: put the keyboard back into the Logitech Dynamic Lighting service's mode, then take and return
    /// Windows Dynamic Lighting control so Windows re-applies its effect. Outside games GameLightSync never touches
    /// the keyboard by itself.
    /// </summary>
    private void RestoreNormalLighting(BridgeConfig config)
    {
        try
        {
            if (config.UseHidpp) _hidpp.RestoreServiceMode();
            Thread.Sleep(500);
            _dynamicLighting.Nudge(config.IsoLayout);
        }
        catch (Exception ex) { Log.Write($"Restoring normal lighting failed: {ex.Message}"); }
    }
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Diagnostics.Stopwatch _testAnimationClock = new();
    private DateTime _testAnimationUntil = DateTime.MinValue;
    private ChromaColor[]? _lastDynamicFrame;
    private TimeSpan _idleWait = TimeSpan.FromMilliseconds(500); // worker wake-up when no game frames arrive

    // latest data from Razer (guarded by _gate)
    private readonly ChromaColor[] _keyboard = new ChromaColor[KeyMap.RazerRows * KeyMap.RazerCols];
    private readonly int[] _accR = new int[KeyMap.RazerRows * KeyMap.RazerCols];
    private readonly int[] _accG = new int[KeyMap.RazerRows * KeyMap.RazerCols];
    private readonly int[] _accB = new int[KeyMap.RazerRows * KeyMap.RazerCols];
    private int _accCount;
    private ChromaColor _mouseLogo;
    private bool _keyboardDirty, _mouseDirty;
    private string _currentApp = "";

    // Logitech-side state (worker thread only)
    private bool _logiInitialized;
    private DateTime _nextInitAttempt = DateTime.MinValue;
    private readonly byte[] _bitmap = new byte[LogitechLed.BitmapSize];
    private readonly int[] _lastGKeyColors = new int[KeyMap.GKeys.Length + 1];
    private readonly int[] _lastKeyColors = new int[KeyMap.RazerRows * KeyMap.RazerCols];
    private int[]? _keyCodes;
    private bool _keyCodesIso;
    private DateTime _nextFullRefresh = DateTime.MinValue;
    private volatile bool _invalidateCaches;
    private int _lastMouseColor = -1;
    private DateTime _lastFrame = DateTime.MinValue;
    private DateTime _testPatternUntil = DateTime.MinValue;

    // diagnostics
    private int _framesIn, _framesOut, _sendFailures;
    private long _sendTicks;
    private DateTime _nextStats = DateTime.UtcNow.AddSeconds(5);
    private readonly byte[] _sentBitmap = new byte[LogitechLed.BitmapSize]; // guarded by _gate

    public event Action<string>? StatusChanged;
    public bool Enabled { get; set; } = true;
    public string Status { get; private set; } = "Starting";

    public Bridge(BridgeConfig config)
    {
        _config = config;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "GameLightSync worker" };
    }

    public void Start()
    {
        _worker.Start();
        StartReader();
        _watcher.Start();
    }

    public void ReloadConfig(BridgeConfig config)
    {
        lock (_gate) { _config = config; _keyboardDirty = _mouseDirty = true; }
        _invalidateCaches = true; // the worker thread owns the caches
        _signal.Set();
    }

    public void ShowTestPattern(TimeSpan duration)
    {
        _testPatternUntil = DateTime.UtcNow + duration;
        _invalidateCaches = true;
        _signal.Set();
    }

    private void StartReader()
    {
        try
        {
            _reader = new ChromaReader
            {
                EnableAppData = true,
                EnableKeyboard = true,
                EnableMouse = true,
                EnableMousepad = false,
                EnableKeypad = false,
                EnableHeadset = false,
                EnableChromaLink = false,
            };
            _reader.KeyboardUpdated += OnKeyboard;
            _reader.MouseUpdated += OnMouse;
            _reader.AppDataUpdated += OnAppData;
            _reader.Exception += (_, ex) => Log.Write($"Razer reader error: {ex.Message} {ex.InnerException?.Message}");
            _reader.Start();
            Log.Write("Listening to the Razer Chroma SDK service");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not attach to the Razer Chroma SDK (is Razer Synapse / Chroma SDK service running?): {ex}");
            SetStatus("Razer Chroma SDK not available");
        }
    }

    private bool _loggedSize;

    private void OnKeyboard(object? sender, in ChromaKeyboard e)
    {
        if (!_loggedSize) { _loggedSize = true; Log.Write($"Razer keyboard grid {e.Width}x{e.Height}"); }
        if (e.Count != _keyboard.Length) return;
        lock (_gate)
        {
            e.GetColors(_keyboard);
            _keyboardDirty = true;
            _framesIn++;
            _lastRazerFrame = DateTime.UtcNow;
            _recentFrames.Enqueue(_lastRazerFrame);
            while (_recentFrames.Count > 64) _recentFrames.Dequeue();
            // accumulate for the Dynamic Lighting path, which averages all game frames between two sends
            if (_accCount >= 256) { Array.Clear(_accR); Array.Clear(_accG); Array.Clear(_accB); _accCount = 0; }
            for (var i = 0; i < _keyboard.Length; i++)
            {
                _accR[i] += _keyboard[i].R;
                _accG[i] += _keyboard[i].G;
                _accB[i] += _keyboard[i].B;
            }
            _accCount++;
        }
        _signal.Set();
    }

    /// <summary>
    /// Average of all game frames since the last call (caller holds _gate). Games update the Chroma SDK at their
    /// render rate (~200 fps for Overwatch) while the keyboard takes ~30 updates per second; a single snapshot would
    /// randomly catch the on or off phase of fast blinking effects (hard flicker), the average shows what the eye
    /// would see on a Razer keyboard.
    /// </summary>
    private ChromaColor[] TakeAveragedFrame()
    {
        var frame = new ChromaColor[_keyboard.Length];
        for (var i = 0; i < frame.Length; i++)
        {
            frame[i] = new ChromaColor(
                (byte)((_accR[i] + _accCount / 2) / _accCount),
                (byte)((_accG[i] + _accCount / 2) / _accCount),
                (byte)((_accB[i] + _accCount / 2) / _accCount), 255);
        }
        Array.Clear(_accR); Array.Clear(_accG); Array.Clear(_accB);
        _accCount = 0;
        return frame;
    }

    /// <summary>Copies the latest Razer frame and the last bitmap sent to G HUB (for the preview window).</summary>
    public (ChromaColor[] Razer, byte[] Sent, string App) Snapshot()
    {
        lock (_gate) return ((ChromaColor[])_keyboard.Clone(), (byte[])_sentBitmap.Clone(), _currentApp);
    }

    private void OnMouse(object? sender, in ChromaMouse e)
    {
        // Razer mouse grid: 9 rows x 7 cols, logo LED = RZLED2_LOGO (row 7, col 3)
        var logoIndex = 7 * e.Width + 3;
        if (logoIndex >= e.Count) return;
        var color = e.GetColor(logoIndex);
        lock (_gate) { _mouseLogo = color; _mouseDirty = true; }
        _signal.Set();
    }

    private void OnAppData(object? sender, in ChromaAppData e)
    {
        var name = e.CurrentAppName ?? "";
        lock (_gate)
        {
            if (name == _currentApp) return;
            _currentApp = name;
            _keyboardDirty = _mouseDirty = true;
        }
        var registered = new List<string>();
        for (var i = 0; i < Math.Min((int)e.AppCount, 50); i++) registered.Add($"{e.AppInfo[i].AppName}#{e.AppInfo[i].AppId}");
        Log.Write($"Active Chroma app: {(name.Length == 0 ? "(none)" : name)} (current id {e.CurrentAppId}, " +
                  $"registered: {(registered.Count == 0 ? "-" : string.Join(", ", registered))})");
        _signal.Set();
    }

    private string _assumedApp = "";

    private void WorkerLoop()
    {
        while (!_stopping)
        {
            _signal.WaitOne(_idleWait);
            if (_stopping) break;

            try { Tick(); }
            catch (DllNotFoundException ex)
            {
                Log.Write($"LogitechLedEnginesWrapper.dll missing: {ex.Message}");
                SetStatus("Logitech LED SDK dll missing");
                Thread.Sleep(5000);
            }
            catch (Exception ex)
            {
                Log.Write($"Worker error: {ex}");
                Thread.Sleep(1000);
            }
        }
        ReleaseLogitech();
        _dynamicLighting.Stop();
        _hidpp.Stop();
    }

    private void Tick()
    {
        BridgeConfig config;
        string app;
        lock (_gate) { config = _config; app = _currentApp; }

        // The Razer service does not always report which app sends the frames (seen after a Synapse update: the
        // name stays empty while Overwatch runs). Then attribute the frames to an allowed game that is running.
        if (app.Length == 0 && config.Apps.Count > 0 && (DateTime.UtcNow - _lastRazerFrame).TotalSeconds < 2)
        {
            app = config.Apps.FirstOrDefault(IsProcessRunning) ?? "";
            if (app.Length > 0 && app != _assumedApp)
                Log.Write($"Razer service reports no app name - frames come while {app} runs, assuming {app}");
            _assumedApp = app;
        }

        LogStats(app);
        var testAnimation = DateTime.UtcNow < _testAnimationUntil;
        var testPattern = testAnimation || DateTime.UtcNow < _testPatternUntil;
        // a game that is still running but sends nothing (alt-tab, loading) gets the keyboard back to normal lighting
        var gameSending = (DateTime.UtcNow - _lastRazerFrame).TotalSeconds < Math.Max(0.5, config.ReleaseAfterSeconds);
        // ignore stray frames (the Razer service can write one after the game is gone, with the old app name):
        // start only when the game process really runs and a steady stream of frames arrives
        if (!_wasMirroring && gameSending && (RecentFrameCount() < 5 || !IsProcessRunning(app))) gameSending = false;
        var mirroring = Enabled && config.ShouldMirror(app) && gameSending;
        if (!_restoredAtStart)
        {
            // only if a previous run was killed while it had control: hand the keyboard back once at start
            _restoredAtStart = true;
            if (!mirroring && !testPattern && config.UseHidpp) _hidpp.RestoreAfterUncleanExit();
        }
        if (mirroring != _wasMirroring)
        {
            _wasMirroring = mirroring;
            Log.Write(mirroring ? $"Mirroring {app}" :
                !Enabled ? "Paused (tray)" : !config.ShouldMirror(app) ? "No Chroma game active" : $"{app} stopped sending colors - normal lighting back");
        }

        if (!testPattern && !mirroring)
        {
            if (_logiInitialized)
            {
                ReleaseLogitech();
                Log.Write("Released lighting back to G HUB");
            }
            // hand the keyboard back (its saved control mode), then Dynamic Lighting: Windows' lighting takes over last
            _hidpp.Stop();
            _dynamicLighting.Stop();
            _lastDynamicFrame = null;
            if (_restorePending)
            {
                _restorePending = false;
                RestoreNormalLighting(config);
            }
            _idleWait = TimeSpan.FromMilliseconds(500);
            SetStatus(!Enabled ? "Paused" : app.Length == 0 ? "Idle - waiting for a Chroma game" : $"Ignoring {app}");
            return;
        }

        IFrameOutput? output = config.UseHidpp ? _hidpp : config.UseDynamicLighting ? _dynamicLighting : null;
        if (output != null)
        {
            TickFrameOutput(output, config, app, testPattern, testAnimation);
            return;
        }

        _dynamicLighting.Stop();
        _hidpp.Stop();
        if (!EnsureLogitech()) return;

        if (_invalidateCaches)
        {
            _invalidateCaches = false;
            ResetCaches();
        }

        // frame limiter
        var minInterval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(config.MaxFps, 1, 240));
        var wait = _lastFrame + minInterval - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) { Thread.Sleep(wait); }
        _lastFrame = DateTime.UtcNow;

        if (testPattern)
        {
            RenderTestPattern(config);
            SetStatus("Showing test pattern");
            _signal.Set(); // keep ticking until the pattern expires
            return;
        }

        ChromaColor[]? frame = null;
        ChromaColor mouse = default;
        bool mouseDirty;
        lock (_gate)
        {
            if (_keyboardDirty) { frame = (ChromaColor[])_keyboard.Clone(); _keyboardDirty = false; }
            mouseDirty = _mouseDirty;
            mouse = _mouseLogo;
            _mouseDirty = false;
        }

        if (frame != null) RenderKeyboard(frame, config);
        if (mouseDirty && config.Mouse) RenderMouse(mouse, config);
        SetStatus($"Mirroring {app}");
    }

    /// <summary>
    /// Paced per-key output (HID++ or Dynamic Lighting). The G915 X accepts one frame per ~33 ms (it reports this
    /// as its minimum update interval) and silently drops faster ones, so frames are spaced by a fixed interval,
    /// measured with a precise clock (Program raises the Windows timer resolution to 1 ms).
    /// </summary>
    private void TickFrameOutput(IFrameOutput output, BridgeConfig config, string app, bool testPattern, bool testAnimation)
    {
        if (_logiInitialized) ReleaseLogitech(); // a G HUB SDK session would fight with the per-key output
        if (output != _hidpp) _hidpp.Stop();
        // HID++: hold Windows Dynamic Lighting control (sending nothing through it) so Windows' effect pauses
        var holdDynamicLighting = output == _hidpp && config.HoldDynamicLighting;
        if (output == _dynamicLighting || holdDynamicLighting) _dynamicLighting.Start(config.IsoLayout);
        else _dynamicLighting.Stop();
        output.Start(config.IsoLayout);
        if (output is DynamicLightingOutput dl) dl.LampBudget = config.EffectiveLampBudget;

        var fps = output == _hidpp ? config.HidppFps : config.DynamicLightingFps;
        var minInterval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(fps, 5, 30));
        var wait = minInterval - _frameClock.Elapsed;
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        _frameClock.Restart();

        ChromaColor[]? frame;
        if (testAnimation)
        {
            frame = TestAnimationFrame(_testAnimationClock.Elapsed.TotalSeconds);
            _signal.Set(); // keep ticking until the animation ends
        }
        else if (testPattern)
        {
            frame = TestPatternFrame();
            _signal.Set(); // keep ticking until the pattern expires
        }
        else
        {
            lock (_gate)
            {
                frame = _keyboardDirty && _accCount > 0 ? TakeAveragedFrame() : null;
                _keyboardDirty = _mouseDirty = false;
            }
        }

        if (frame != null)
        {
            FillBitmap(frame, config); // for the live preview
            _lastDynamicFrame = frame;
        }
        // Keep ticking at a steady rate even without new game frames: the outputs only send what changed,
        // and their periodic full refresh repairs anything the keyboard dropped.
        if (_lastDynamicFrame != null)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            output.Render(_lastDynamicFrame, c => Scale(c, config));
            _sendTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            _framesOut++;
        }
        _idleWait = minInterval;
        var what = testAnimation ? "Test animation" : testPattern ? "Showing test pattern" : $"Mirroring {app}";
        SetStatus($"{what} via {(output == _hidpp ? "HID++" : "Dynamic Lighting")} ({output.Summary()})");
    }

    /// <summary>Rainbow wave in 12 color bands moving across the keyboard, plus W A S D blinking 4x per second.</summary>
    private static ChromaColor[] TestAnimationFrame(double t)
    {
        var frame = new ChromaColor[KeyMap.RazerRows * KeyMap.RazerCols];
        for (var i = 0; i < frame.Length; i++)
        {
            var hue = (i % KeyMap.RazerCols / (double)KeyMap.RazerCols - t * 0.5) % 1.0;
            if (hue < 0) hue += 1;
            frame[i] = Hsv(Math.Floor(hue * 12) / 12);
        }
        var blinkOn = (int)(t * 8) % 2 == 0;
        foreach (var key in new[] { 0x0203, 0x0302, 0x0303, 0x0304 })
            frame[(key >> 8) * KeyMap.RazerCols + (key & 0xFF)] = blinkOn ? new(255, 255, 255, 255) : new(0, 0, 0, 255);
        return frame;
    }

    private static ChromaColor Hsv(double h)
    {
        var i = (int)(h * 6) % 6;
        var f = h * 6 - Math.Floor(h * 6);
        byte q = (byte)(255 * (1 - f)), u = (byte)(255 * f);
        return i switch
        {
            0 => new(255, u, 0, 255), 1 => new(q, 255, 0, 255), 2 => new(0, 255, u, 255),
            3 => new(0, q, 255, 255), 4 => new(u, 0, 255, 255), _ => new(255, 0, q, 255),
        };
    }

    public void ShowTestAnimation(TimeSpan duration)
    {
        _testAnimationClock.Restart();
        _testAnimationUntil = DateTime.UtcNow + duration;
        _signal.Set();
    }

    private bool EnsureLogitech()
    {
        if (_logiInitialized) return true;
        if (DateTime.UtcNow < _nextInitAttempt) return false;

        if (!LogitechLed.LogiLedInitWithName("GameLightSync"))
        {
            _nextInitAttempt = DateTime.UtcNow.AddSeconds(5);
            SetStatus("G HUB not reachable - is G HUB running?");
            Log.Write("LogiLedInit failed (G HUB not running, or SDK lighting control disabled in G HUB)");
            return false;
        }

        int major = 0, minor = 0, build = 0;
        LogitechLed.LogiLedGetSdkVersion(ref major, ref minor, ref build);
        Log.Write($"Connected to G HUB (LED SDK {major}.{minor}.{build})");
        _logiInitialized = true;
        ResetCaches();
        lock (_gate) { _keyboardDirty = _mouseDirty = true; }
        return true;
    }

    private void ResetCaches()
    {
        Array.Fill(_lastGKeyColors, -1);
        Array.Fill(_lastKeyColors, -1);
        _lastMouseColor = -1;
        _keyCodes = null;
    }

    private void ReleaseLogitech()
    {
        if (!_logiInitialized) return;
        try { LogitechLed.LogiLedShutdown(); } catch { }
        _logiInitialized = false;
    }

    private void FillBitmap(ChromaColor[] frame, BridgeConfig config)
    {
        Array.Clear(_bitmap);
        var map = KeyMap.RazerToLogiPixel;
        for (var i = 0; i < map.Length; i++)
        {
            var px = map[i];
            if (px < 0) continue;
            WritePixel(px, frame[i], config);
        }

        lock (_gate) Buffer.BlockCopy(_bitmap, 0, _sentBitmap, 0, _bitmap.Length);
    }

    private void RenderKeyboard(ChromaColor[] frame, BridgeConfig config)
    {
        FillBitmap(frame, config);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ok = config.PerKeyMode ? SendPerKey(frame, config) : SendBitmap();
        _sendTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        if (ok) _framesOut++;
        else
        {
            _sendFailures++;
            Log.Write("Lost connection to G HUB, reconnecting");
            ReleaseLogitech();
            _nextInitAttempt = DateTime.UtcNow.AddSeconds(2);
            return;
        }

        if (!config.GKeysAndLogo) return;
        for (var k = 0; k < KeyMap.GKeys.Length; k++)
            SetSingleKey(k, KeyMap.GKeys[k].LogiKey, frame[KeyMap.GKeys[k].RazerIndex], config);
        SetSingleKey(KeyMap.GKeys.Length, LogitechLed.KeyGLogo, frame[KeyMap.RazerLogoIndex], config);
    }

    private bool SendBitmap()
    {
        LogitechLed.LogiLedSetTargetDevice(LogitechLed.DeviceTypePerKeyRgb);
        return LogitechLed.LogiLedSetLightingFromBitmap(_bitmap);
    }

    /// <summary>Sets each key individually, only sending keys whose color changed (full refresh every 2 s).</summary>
    private bool SendPerKey(ChromaColor[] frame, BridgeConfig config)
    {
        if (_keyCodes == null || _keyCodesIso != config.IsoLayout)
        {
            _keyCodes = KeyMap.BuildRazerToLogiKey(config.IsoLayout);
            _keyCodesIso = config.IsoLayout;
            Array.Fill(_lastKeyColors, -1);
        }
        if (DateTime.UtcNow >= _nextFullRefresh)
        {
            Array.Fill(_lastKeyColors, -1);
            _nextFullRefresh = DateTime.UtcNow.AddSeconds(2);
        }

        LogitechLed.LogiLedSetTargetDevice(LogitechLed.DeviceTypePerKeyRgb);
        for (var i = 0; i < _keyCodes.Length; i++)
        {
            var code = _keyCodes[i];
            if (code < 0) continue;
            var (r, g, b) = Scale(frame[i], config);
            var packed = (r << 16) | (g << 8) | b;
            if (_lastKeyColors[i] == packed) continue;

            bool ok = code == KeyMap.IsoAngleBracketScanCode
                ? LogitechLed.LogiLedSetLightingForKeyWithScanCode(code, LogitechLed.Pct(r), LogitechLed.Pct(g), LogitechLed.Pct(b))
                : LogitechLed.LogiLedSetLightingForKeyWithKeyName(code, LogitechLed.Pct(r), LogitechLed.Pct(g), LogitechLed.Pct(b));
            if (!ok && code != KeyMap.IsoAngleBracketScanCode) return false; // connection lost
            _lastKeyColors[i] = packed;
        }
        return true;
    }

    private void SetSingleKey(int slot, int logiKey, ChromaColor c, BridgeConfig config)
    {
        var (r, g, b) = Scale(c, config);
        var packed = (r << 16) | (g << 8) | b;
        if (_lastGKeyColors[slot] == packed) return;
        _lastGKeyColors[slot] = packed;
        LogitechLed.LogiLedSetLightingForKeyWithKeyName(logiKey, LogitechLed.Pct(r), LogitechLed.Pct(g), LogitechLed.Pct(b));
    }

    private void RenderMouse(ChromaColor c, BridgeConfig config)
    {
        var (r, g, b) = Scale(c, config);
        var packed = (r << 16) | (g << 8) | b;
        if (packed == _lastMouseColor) return;
        _lastMouseColor = packed;
        // Never use LogiLedSetLighting here: G HUB applies it to the keyboard too and it overrides per-key colors.
        for (var zone = 0; zone < 3; zone++)
            LogitechLed.LogiLedSetLightingForTargetZone(LogitechLed.DeviceType.Mouse, zone,
                LogitechLed.Pct(r), LogitechLed.Pct(g), LogitechLed.Pct(b));
    }

    private static readonly ChromaColor[] TestRows =
    [
        new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255),
        new(255, 255, 255, 255), new(255, 255, 0, 255), new(0, 255, 255, 255),
    ];

    /// <summary>Row 0 red, row 1 green, row 2 blue, row 3 white, row 4 yellow, row 5 cyan; W A S D black.</summary>
    private static ChromaColor[] TestPatternFrame()
    {
        var frame = new ChromaColor[KeyMap.RazerRows * KeyMap.RazerCols];
        for (var i = 0; i < frame.Length; i++) frame[i] = TestRows[i / KeyMap.RazerCols];
        // W, A, S, D in black: checks that single keys can differ from their row
        foreach (var key in new[] { 0x0203, 0x0302, 0x0303, 0x0304 })
            frame[(key >> 8) * KeyMap.RazerCols + (key & 0xFF)] = new(0, 0, 0, 255);
        return frame;
    }

    private void RenderTestPattern(BridgeConfig config)
    {
        RenderKeyboard(TestPatternFrame(), config);
        if (config.Mouse) RenderMouse(TestRows[0], config);
    }

    private void WritePixel(int px, ChromaColor c, BridgeConfig config)
    {
        var (r, g, b) = Scale(c, config);
        var o = px * 4;
        // LED SDK bitmap is BGRA in practice (same order as ChromaColor); swapRedBlue flips it
        if (config.SwapRedBlue) { _bitmap[o] = (byte)r; _bitmap[o + 1] = (byte)g; _bitmap[o + 2] = (byte)b; }
        else { _bitmap[o] = (byte)b; _bitmap[o + 1] = (byte)g; _bitmap[o + 2] = (byte)r; }
        _bitmap[o + 3] = 255;
    }

    private static (int R, int G, int B) Scale(ChromaColor c, BridgeConfig config)
    {
        var k = Math.Clamp(config.Brightness, 0, 1);
        return ((int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
    }

    private void LogStats(string app)
    {
        if (DateTime.UtcNow < _nextStats) return;
        var seconds = (DateTime.UtcNow - _nextStats).TotalSeconds + 10;
        _nextStats = DateTime.UtcNow.AddSeconds(10);
        int framesIn, distinct;
        lock (_gate)
        {
            framesIn = _framesIn; _framesIn = 0;
            distinct = _keyboard.Select(c => (c.R, c.G, c.B)).Distinct().Count();
        }
        if (framesIn == 0 && _framesOut == 0) return;
        var avgMs = _framesOut == 0 ? 0 : _sendTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _framesOut;
        Log.Write($"stats: app={app} mode={(_config.UseHidpp ? "hidpp" : _config.UseDynamicLighting ? "dynamicLighting" : _config.PerKeyMode ? "ghub-perKey" : "ghub-bitmap")} razerFps={framesIn / seconds:F0} " +
                  $"sentFps={_framesOut / seconds:F0} avgSendMs={avgMs:F2} sendFailures={_sendFailures} " +
                  $"distinctKeyColors={distinct} logi={_logiInitialized}");
        _framesOut = 0; _sendFailures = 0; _sendTicks = 0;
    }

    private void SetStatus(string status)
    {
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        try { _reader?.Dispose(); } catch { }
        try { _watcher.Dispose(); } catch { }
        _worker.Join(2000);
        _signal.Dispose();
    }
}
