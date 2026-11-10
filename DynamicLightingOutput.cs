using System.Numerics;
using System.Runtime.InteropServices;
using RazerSdkReader.Structures;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.System;
using WinColor = Windows.UI.Color;

namespace GameLightSync;

/// <summary>
/// Drives Windows Dynamic Lighting (HID LampArray) keyboards, e.g. the G915 X exposed by Logitech's LampArray service.
/// Background control needs package identity + "Background light control" selection in Windows Settings.
/// </summary>
internal sealed class DynamicLightingOutput : IFrameOutput
{
    /// <summary>Lamps per update for the "budget" strategy, 0 = "changedKeys" strategy.</summary>
    public int LampBudget { get; set; }

    public void Render(ChromaColor[] frame, Func<ChromaColor, (int R, int G, int B)> scale) => Render(frame, scale, LampBudget);

    private sealed class Device(string id, LampArray lamps)
    {
        public string Id { get; } = id;
        public LampArray Lamps { get; } = lamps;
        public int[] Indices = [];
        public int[] RazerIndex = [];
        public uint[] LastSent = [];
        public int[] Error = [];
        public DateTime LastChange, NextStaticRefresh;
        public bool Dither;
        public Windows.Foundation.TypedEventHandler<LampArray, object>? AvailabilityHandler;

        /// <summary>
        /// Disconnects this LampArray session. The event subscription is a reference cycle across the WinRT boundary
        /// (LampArray -> handler -> Device -> LampArray) that keeps old sessions alive with stale colors otherwise.
        /// </summary>
        public void Close()
        {
            if (AvailabilityHandler != null) Lamps.AvailabilityChanged -= AvailabilityHandler;
            AvailabilityHandler = null;
        }
        public int Mapped, Nearest;
    }

    /// <summary>When nothing changed for this long, the whole keyboard is resent once (repairs stuck keys).</summary>
    private static readonly TimeSpan StaticAfter = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StaticRefreshInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, Device> _devices = new();
    private DeviceWatcher? _watcher;
    private bool _isoLayout = true;

    public bool Active => _watcher != null;

    public int DeviceCount { get { lock (_gate) return _devices.Count; } }

    public bool AnyKeyboardAvailable()
    {
        lock (_gate) return _devices.Values.Any(d => IsAvailable(d.Lamps));
    }

    /// <summary>
    /// Takes Dynamic Lighting control for a moment and gives it back. Windows then re-applies the user's own effect
    /// (a static effect is only sent when Windows gains control, so it would not come back by itself).
    /// </summary>
    public bool Nudge(bool isoLayout)
    {
        if (Active) return false;
        Start(isoLayout);
        // Windows grants background control only after a few seconds; give it back once we really had it
        var deadline = Environment.TickCount64 + 10000;
        while (!AnyKeyboardAvailable() && Environment.TickCount64 < deadline) Thread.Sleep(100);
        var granted = AnyKeyboardAvailable();
        Thread.Sleep(300);
        Stop();
        Log.Write(granted
            ? "Dynamic Lighting: handed control back to Windows (normal lighting)"
            : "Dynamic Lighting: Windows did not grant control within 10 s");
        return granted;
    }

    /// <summary>Device id of the last Dynamic Lighting keyboard seen (key of its settings in the registry).</summary>
    public string? KeyboardDeviceId { get; private set; }

    public void Start(bool isoLayout)
    {
        if (_watcher != null) return;
        _isoLayout = isoLayout;
        _watcher = DeviceInformation.CreateWatcher(LampArray.GetDeviceSelector());
        _watcher.Added += async (_, info) => await AddDeviceAsync(info);
        _watcher.Removed += (_, info) =>
        {
            lock (_gate)
                if (_devices.Remove(info.Id, out var d))
                {
                    d.Close();
                    Log.Write($"Dynamic Lighting device removed: {info.Id}");
                }
        };
        _watcher.Start();
        Log.Write("Dynamic Lighting: watching for LampArray devices");
    }

    /// <summary>Drops all LampArray handles so Windows hands the devices back to the next controller / G HUB.</summary>
    public void Stop()
    {
        if (_watcher == null) return;
        try { _watcher.Stop(); } catch { }
        _watcher = null;
        lock (_gate)
        {
            foreach (var d in _devices.Values) d.Close();
            _devices.Clear();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Log.Write("Dynamic Lighting: released devices");
    }

    public string Summary()
    {
        lock (_gate)
        {
            if (_devices.Count == 0) return "no Dynamic Lighting keyboard found";
            var unavailable = _devices.Values.Count(d => !IsAvailable(d.Lamps));
            return unavailable == 0
                ? $"{_devices.Count} device(s)"
                : "not in control - select GameLightSync under Background light control";
        }
    }

    /// <summary>
    /// Sends at most <paramref name="budget"/> lamps per call, choosing the lamps whose shown color is furthest from
    /// the target. The keyboard link (Logitech LampArray service + wireless) applies updates late or out of order
    /// when it gets more than it can handle, which looks like keys flickering between two colors; a fixed budget
    /// keeps the traffic bounded during heavy animations and lets the rest catch up over the next frames.
    /// Colors are always exact. When the picture is still, the whole keyboard is resent every 2 s to repair stuck keys.
    /// A budget of 0 selects the older "changedKeys" strategy instead.
    /// </summary>
    public void Render(ChromaColor[] frame, Func<ChromaColor, (int R, int G, int B)> scale, int budget)
    {
        Device[] devices;
        lock (_gate) devices = _devices.Values.ToArray();
        var now = DateTime.UtcNow;
        foreach (var d in devices)
        {
            var count = d.Indices.Length;
            var targets = new uint[count];
            var pending = 0;
            for (var k = 0; k < count; k++)
            {
                var (r, g, b) = scale(frame[d.RazerIndex[k]]);
                targets[k] = (uint)(r << 16 | g << 8 | b);
                d.Error[k] = d.LastSent[k] == uint.MaxValue ? int.MaxValue : MaxChannelDiff(d.LastSent[k], targets[k]);
                if (d.Error[k] > 0) pending++;
            }

            int[] send;
            if (budget <= 0)
            {
                // "changedKeys" strategy: every changed lamp, plus a full resend once per second with the lowest
                // blue bit toggled so the device cannot skip it
                var full = now >= d.NextStaticRefresh;
                if (full)
                {
                    d.NextStaticRefresh = now + TimeSpan.FromSeconds(1);
                    d.Dither = !d.Dither;
                    if (d.Dither)
                        for (var k = 0; k < count; k++)
                            targets[k] = (targets[k] & 0xFF) >= 255 ? targets[k] - 1 : targets[k] + 1;
                }
                send = Enumerable.Range(0, count).Where(k => full || d.Error[k] > 0).ToArray();
                if (send.Length == 0) continue;
            }
            else if (pending > 0)
            {
                d.LastChange = now;
                d.NextStaticRefresh = now + StaticAfter;
                send = Enumerable.Range(0, count).Where(k => d.Error[k] > 0)
                    .OrderByDescending(k => d.Error[k]).Take(Math.Max(1, budget)).ToArray();
            }
            else if (now >= d.NextStaticRefresh)
            {
                d.NextStaticRefresh = now + StaticRefreshInterval;
                send = Enumerable.Range(0, count).ToArray(); // exact resend of everything while nothing moves
            }
            else continue;

            var colors = new WinColor[send.Length];
            var indices = new int[send.Length];
            for (var i = 0; i < send.Length; i++)
            {
                var k = send[i];
                var t = targets[k];
                d.LastSent[k] = t;
                colors[i] = WinColor.FromArgb(255, (byte)(t >> 16), (byte)(t >> 8), (byte)t);
                indices[i] = d.Indices[k];
            }
            try { d.Lamps.SetColorsForIndices(colors, indices); }
            catch (Exception ex) { Log.Write($"Dynamic Lighting update failed: {ex.Message}"); }
        }
    }

    private static int MaxChannelDiff(uint a, uint b) =>
        Math.Max(Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)),
                 Math.Max(Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)), Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF))));

    private async Task AddDeviceAsync(DeviceInformation info)
    {
        try
        {
            var lamps = await LampArray.FromIdAsync(info.Id);
            if (lamps == null) return;
            if (lamps.LampArrayKind != LampArrayKind.Keyboard)
            {
                Log.Write($"Dynamic Lighting: skipping {info.Name} ({lamps.LampArrayKind})");
                return;
            }

            KeyboardDeviceId = info.Id;
            var device = new Device(info.Id, lamps);
            BuildMap(device);
            device.AvailabilityHandler = (s, _) =>
            {
                Log.Write($"Dynamic Lighting: {info.Name} available={IsAvailable(s)}");
                Array.Fill(device.LastSent, uint.MaxValue); // another app drew in between: resend everything
            };
            lamps.AvailabilityChanged += device.AvailabilityHandler;
            lock (_gate)
            {
                // a reconnect can report the same device again: close the old session first
                if (_devices.Remove(info.Id, out var old)) old.Close();
                if (_watcher == null) { device.Close(); return; } // stopped while we were opening it
                _devices[info.Id] = device;
            }

            Log.Write($"Dynamic Lighting keyboard: {info.Name}, {lamps.LampCount} lamps, " +
                      $"{device.Mapped} mapped to keys, {device.Nearest} by position, available={IsAvailable(lamps)}");
        }
        catch (Exception ex)
        {
            Log.Write($"Dynamic Lighting: could not open {info.Name}: {ex.Message}");
        }
    }

    private static bool IsAvailable(LampArray lamps)
    {
        try { return lamps.IsAvailable; } catch { return true; } // property missing on older Windows builds
    }

    // ---------- Razer grid -> lamp mapping ----------

    private void BuildMap(Device d)
    {
        var la = d.Lamps;
        var count = la.LampCount;
        var razerForLamp = new int[count];
        Array.Fill(razerForLamp, -1);
        var positions = new Vector3[count];
        for (var i = 0; i < count; i++) positions[i] = la.GetLampInfo(i).Position;

        var hkl = PickLayout(la, positions);
        var scanCodes = KeyMap.BuildRazerToLogiKey(_isoLayout); // Logitech key names are scan codes

        for (var razer = 0; razer < scanCodes.Length; razer++)
        {
            var scan = scanCodes[razer];
            if (scan < 0) continue;
            foreach (var lamp in LampsForScanCode(la, positions, scan, hkl))
                if (razerForLamp[lamp] < 0) { razerForLamp[lamp] = razer; d.Mapped++; }
        }

        // Branding lamps (logo) -> Razer logo LED
        for (var i = 0; i < count; i++)
            if (razerForLamp[i] < 0 && la.GetLampInfo(i).Purposes.HasFlag(LampPurposes.Branding))
            { razerForLamp[i] = KeyMap.RazerLogoIndex; d.Mapped++; }

        // Everything else (media keys, indicators, G-keys...) copies the nearest mapped key
        var mapped = Enumerable.Range(0, count).Where(i => razerForLamp[i] >= 0).ToArray();
        if (mapped.Length > 0)
            for (var i = 0; i < count; i++)
            {
                if (razerForLamp[i] >= 0) continue;
                var nearest = mapped.MinBy(m => Vector3.DistanceSquared(positions[m], positions[i]));
                razerForLamp[i] = razerForLamp[nearest];
                d.Nearest++;
            }

        var used = Enumerable.Range(0, count).Where(i => razerForLamp[i] >= 0).ToArray();
        d.Indices = used;
        d.RazerIndex = used.Select(i => razerForLamp[i]).ToArray();
        d.LastSent = new uint[used.Length];
        Array.Fill(d.LastSent, uint.MaxValue); // unknown: first frames send everything (within the budget)
        d.Error = new int[used.Length];
    }

    private static IEnumerable<int> LampsForScanCode(LampArray la, Vector3[] pos, int scan, IntPtr hkl)
    {
        var (vk, pick) = FixedKey(scan);
        if (vk == VirtualKey.None)
        {
            vk = (VirtualKey)MapVirtualKeyEx((uint)scan, MAPVK_VSC_TO_VK_EX, hkl);
            pick = Pick.All;
        }
        if (vk == VirtualKey.None) return [];

        var lamps = la.GetIndicesForKey(vk);
        if (lamps.Length == 0 && Generic.TryGetValue(vk, out var generic)) lamps = la.GetIndicesForKey(generic);
        if (lamps.Length <= 1 || pick == Pick.All) return lamps;
        return [pick == Pick.Leftmost ? lamps.MinBy(i => pos[i].X) : lamps.MaxBy(i => pos[i].X)];
    }

    private enum Pick { All, Leftmost, Rightmost }

    /// <summary>Keys whose virtual key does not depend on the keyboard layout (or is ambiguous).</summary>
    private static (VirtualKey, Pick) FixedKey(int scan) => scan switch
    {
        0x1C => (VirtualKey.Enter, Pick.Leftmost),
        0x11C => (VirtualKey.Enter, Pick.Rightmost),
        0x2A => (VirtualKey.LeftShift, Pick.Leftmost),
        0x36 => (VirtualKey.RightShift, Pick.Rightmost),
        0x1D => (VirtualKey.LeftControl, Pick.Leftmost),
        0x11D => (VirtualKey.RightControl, Pick.Rightmost),
        0x38 => (VirtualKey.LeftMenu, Pick.Leftmost),
        0x138 => (VirtualKey.RightMenu, Pick.Rightmost),
        0x15B => (VirtualKey.LeftWindows, Pick.All),
        0x15C => (VirtualKey.RightWindows, Pick.All),
        0x15D => (VirtualKey.Application, Pick.All),
        0x152 => (VirtualKey.Insert, Pick.All),
        0x147 => (VirtualKey.Home, Pick.All),
        0x149 => (VirtualKey.PageUp, Pick.All),
        0x153 => (VirtualKey.Delete, Pick.All),
        0x14F => (VirtualKey.End, Pick.All),
        0x151 => (VirtualKey.PageDown, Pick.All),
        0x148 => (VirtualKey.Up, Pick.All),
        0x14B => (VirtualKey.Left, Pick.All),
        0x150 => (VirtualKey.Down, Pick.All),
        0x14D => (VirtualKey.Right, Pick.All),
        0x137 => (VirtualKey.Snapshot, Pick.All),
        0x46 => (VirtualKey.Scroll, Pick.All),
        0x145 => (VirtualKey.Pause, Pick.All),
        0x45 => (VirtualKey.NumberKeyLock, Pick.All),
        0x135 => (VirtualKey.Divide, Pick.All),
        0x37 => (VirtualKey.Multiply, Pick.All),
        0x4A => (VirtualKey.Subtract, Pick.All),
        0x4E => (VirtualKey.Add, Pick.All),
        0x53 => (VirtualKey.Decimal, Pick.All),
        0x52 => (VirtualKey.NumberPad0, Pick.All),
        0x4F => (VirtualKey.NumberPad1, Pick.All),
        0x50 => (VirtualKey.NumberPad2, Pick.All),
        0x51 => (VirtualKey.NumberPad3, Pick.All),
        0x4B => (VirtualKey.NumberPad4, Pick.All),
        0x4C => (VirtualKey.NumberPad5, Pick.All),
        0x4D => (VirtualKey.NumberPad6, Pick.All),
        0x47 => (VirtualKey.NumberPad7, Pick.All),
        0x48 => (VirtualKey.NumberPad8, Pick.All),
        0x49 => (VirtualKey.NumberPad9, Pick.All),
        _ => (VirtualKey.None, Pick.All),
    };

    private static readonly Dictionary<VirtualKey, VirtualKey> Generic = new()
    {
        [VirtualKey.LeftShift] = VirtualKey.Shift, [VirtualKey.RightShift] = VirtualKey.Shift,
        [VirtualKey.LeftControl] = VirtualKey.Control, [VirtualKey.RightControl] = VirtualKey.Control,
        [VirtualKey.LeftMenu] = VirtualKey.Menu, [VirtualKey.RightMenu] = VirtualKey.Menu,
    };

    /// <summary>
    /// Windows resolves lamp keys through a keyboard layout. Find the installed layout under which the key
    /// right of T (scan 0x15: 'Y' on US, 'Z' on German) really sits next to T on this keyboard.
    /// </summary>
    private static IntPtr PickLayout(LampArray la, Vector3[] pos)
    {
        var layouts = new IntPtr[16];
        var n = GetKeyboardLayoutList(layouts.Length, layouts);
        var candidates = new List<IntPtr> { GetKeyboardLayout(0) };
        candidates.AddRange(layouts.Take(n));

        var tLamps = la.GetIndicesForKey(VirtualKey.T);
        if (tLamps.Length == 0) return candidates[0];
        var tPos = pos[tLamps[0]];

        foreach (var hkl in candidates.Distinct())
        {
            var vk = (VirtualKey)MapVirtualKeyEx(0x15, MAPVK_VSC_TO_VK_EX, hkl);
            var lamps = la.GetIndicesForKey(vk);
            if (lamps.Length > 0 && Math.Abs(pos[lamps[0]].Y - tPos.Y) < 0.006 && pos[lamps[0]].X > tPos.X)
            {
                Log.Write($"Dynamic Lighting: using keyboard layout {(hkl.ToInt64() & 0xFFFF):X4} for key mapping");
                return hkl;
            }
        }
        return candidates[0];
    }

    private const uint MAPVK_VSC_TO_VK_EX = 3;

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] list);
}
