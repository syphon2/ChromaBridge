using System.Diagnostics;
using GameLightSync;

/// <summary>Visual test: game-like animations sent straight to the G915 X over HID++ (0x8081) with atomic commits.</summary>
internal static class AnimTest
{
    // Physical rows of the German ISO G915 X, as HID keyboard usages (left to right)
    private static readonly int[][] Rows =
    [
        [0x29, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48],
        [0x35, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2D, 0x2E, 0x2A, 0x49, 0x4A, 0x4B, 0x53, 0x54, 0x55, 0x56],
        [0x2B, 0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30, 0x4C, 0x4D, 0x4E, 0x5F, 0x60, 0x61, 0x57],
        [0x39, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34, 0x32, 0x28, 0x5C, 0x5D, 0x5E],
        [0xE1, 0x64, 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38, 0xE5, 0x52, 0x59, 0x5A, 0x5B, 0x58],
        [0xE0, 0xE3, 0xE2, 0x2C, 0xE6, 0xE7, 0x65, 0xE4, 0x50, 0x51, 0x4F, 0x62, 0x63],
    ];

    /// <summary>HID usage -> 0x8081 key code (keys: usage-3, modifiers: usage-0x78).</summary>
    private static byte Code(int usage) => (byte)(usage >= 0xE0 ? usage - 0x78 : usage - 3);

    private static Hidpp _dev = null!;
    private static byte _pk; // 0x8081 feature index
    private static byte? _effects; // 0x8071 feature index
    private static readonly Dictionary<byte, int> Shown = new(); // key code -> color currently on the keyboard
    private static int _messages, _busy, _errors;

    public static void Run()
    {
        Console.WriteLine("G915 X direct HID++ lighting test\n");
        Console.WriteLine("Before starting:");
        Console.WriteLine("  1. Settings > Personalization > Dynamic Lighting: turn OFF 'Use Dynamic Lighting on my devices'");
        Console.WriteLine("     (otherwise Windows and this test write to the keyboard at the same time)");
        Console.WriteLine("  2. GameLightSync tray icon: uncheck 'Enabled'");
        Console.Write("\nPress Enter when ready... ");
        Console.ReadLine();

        _dev = Hidpp.OpenLongCollections(productId: 0xC356).FirstOrDefault()
               ?? throw new InvalidOperationException("G915 X HID++ interface not found");
        _dev.DeviceIndex = 0xFF;
        _pk = _dev.FeatureIndex(0x8081) ?? throw new InvalidOperationException("keyboard has no 0x8081 feature");
        var effects = _dev.FeatureIndex(0x8071);
        Console.WriteLine($"0x8081 at index 0x{_pk:X2}, 0x8071 at index {(effects is { } e ? $"0x{e:X2}" : "-")}");

        // Claim software control (0x8071 fn5 SetSWControl [set, mode 3, flags 6]): the firmware keeps rendering
        // until our first frame replaces it; afterwards flags 5 = effect control + NV config.
        _effects = effects;
        var claim = effects == null ? -1 : _dev.RequestWithError(effects.Value, 5, [1, 3, 6]).Error;
        Console.WriteLine($"software control claim: {(claim == 0 ? "ok" : $"error {claim}")}");

        var accepted = SendFrame(new Dictionary<byte, int> { [Code(0x29)] = Rgb(255, 0, 0) });
        if (effects != null) _dev.RequestWithError(effects.Value, 5, [1, 3, 5]);
        Console.WriteLine(accepted ? "direct frames accepted" : "WARNING: keyboard refuses direct frames");

        var results = new List<string> { $"claim={claim} accepted={accepted}" };
        results.Add(Phase("1/4", "STATIC ROWS: red, green, blue, white, yellow, cyan, with W A S D black", 6, StaticRows));
        results.Add(Phase("2/4", "OVERWATCH-LIKE: dim green background, W A S D orange, E + Shift teal blinking fast", 10, OverwatchLike));
        results.Add(Phase("3/4", "FAST RAINBOW WAVE across the keyboard (worst case, many colors)", 10, RainbowWave));
        results.Add(Phase("4/4", "FLASHES: whole keyboard flashing red/white like taking damage, 8x per second", 8, Flashes));

        // hand the keyboard back to its firmware / Windows
        if (effects != null) _dev.RequestWithError(effects.Value, 5, [1, 0, 0]);

        var file = Path.Combine(AppContext.BaseDirectory, "hidpp-results.txt");
        File.WriteAllLines(file, results);
        Console.WriteLine($"\nResults saved to {file}");
        Console.WriteLine("Turn Dynamic Lighting back ON and re-enable GameLightSync. Press Enter to close.");
        Console.ReadLine();
    }

    private static string Phase(string step, string description, int seconds, Func<double, int, int> colorAt)
    {
        Console.WriteLine($"\nStep {step}: {description} ({seconds} s)");
        var sw = Stopwatch.StartNew();
        int frames = 0;
        _messages = 0; _busy = 0; _errors = 0;
        var frameTime = TimeSpan.FromMilliseconds(1000.0 / 60);
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var t = sw.Elapsed.TotalSeconds;
            var target = new Dictionary<byte, int>();
            for (var r = 0; r < Rows.Length; r++)
                for (var c = 0; c < Rows[r].Length; c++)
                    target[Code(Rows[r][c])] = colorAt(t, r * 100 + c * 100 / Rows[r].Length);
            SendFrame(target);
            frames++;
            var wait = frameTime * frames - sw.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
        var fps = frames / sw.Elapsed.TotalSeconds;
        var stats = $"fps={fps:F0} msgs/s={_messages / sw.Elapsed.TotalSeconds:F0} busyRetries={_busy} errors={_errors}";
        Console.WriteLine($"   {stats}");
        Console.Write("   Did it look smooth and correct, without flicker? (y / n / describe) > ");
        var answer = Console.ReadLine() ?? "";
        return $"step {step} [{description}] {stats} => {answer}";
    }

    /// <summary>
    /// Sends only changed keys, grouped by color (13 keys per message, each write acknowledged), then commits the
    /// frame. The keyboard answers the commit with BUSY while it is still applying the previous frame; we retry
    /// until it accepts, so no frame is ever dropped or half-applied, and the keyboard paces the frame rate.
    /// </summary>
    private static bool SendFrame(Dictionary<byte, int> target)
    {
        var changed = target.Where(kv => !Shown.TryGetValue(kv.Key, out var old) || old != kv.Value)
                            .GroupBy(kv => kv.Value, kv => kv.Key).ToList();
        if (changed.Count == 0) return true;
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
                Write(6, p);
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
            if (chunk.Count < 4) p[4 * chunk.Count] = 0xFF;
            Write(1, p);
        }

        // commit: the keyboard shows the whole frame at once; BUSY = still applying the previous one, retry
        var deadline = Environment.TickCount64 + 250;
        while (true)
        {
            var (_, error) = _dev.RequestWithError(_pk, 7, new byte[16], 100);
            _messages++;
            if (error == 0) break;
            if (error == Hidpp.ErrorBusy && Environment.TickCount64 < deadline) { _busy++; Thread.Sleep(1); continue; }
            _errors++;
            return false;
        }
        foreach (var kv in target) Shown[kv.Key] = kv.Value;
        return true;
    }

    private static void Write(byte function, byte[] p)
    {
        _messages++;
        var (_, error) = _dev.RequestWithError(_pk, function, p, 100);
        if (error != 0) _errors++;
    }

    private static int Rgb(int r, int g, int b) => r << 16 | g << 8 | b;
    private static readonly HashSet<int> Wasd = [0x1A, 0x04, 0x16, 0x07];

    private static int UsageAt(int pos) => Rows[pos / 100][Math.Min(Rows[pos / 100].Length - 1, pos % 100 * Rows[pos / 100].Length / 100)];

    private static int StaticRows(double t, int pos)
    {
        if (Wasd.Contains(UsageAt(pos))) return 0;
        return (pos / 100) switch
        {
            0 => Rgb(255, 0, 0), 1 => Rgb(0, 255, 0), 2 => Rgb(0, 0, 255),
            3 => Rgb(255, 255, 255), 4 => Rgb(255, 255, 0), _ => Rgb(0, 255, 255),
        };
    }

    private static int OverwatchLike(double t, int pos)
    {
        var usage = UsageAt(pos);
        if (Wasd.Contains(usage)) return Rgb(0xDE, 0x99, 0x00);
        var blink = (int)(t * 12) % 2 == 0;
        if (usage is 0x08 or 0xE1) return blink ? Rgb(0x04, 0x8D, 0x90) : Rgb(0x04, 0x1B, 0x01);
        // background pulse like Overwatch's idle glow
        var k = 0.6 + 0.4 * Math.Sin(t * 2 * Math.PI * 0.5);
        return Rgb((int)(4 * k), (int)(27 * k + 10), (int)(1 * k));
    }

    private static int RainbowWave(double t, int pos)
    {
        var x = pos % 100 / 100.0;
        var hue = (x - t * 0.8) % 1.0; if (hue < 0) hue += 1;
        hue = Math.Floor(hue * 12) / 12; // 12 color bands
        return Hsv(hue);
    }

    private static int Flashes(double t, int pos)
    {
        var phase = t * 8 % 1.0;
        return phase < 0.25 ? Rgb(255, 255, 255) : phase < 0.6 ? Rgb(200, 0, 0) : Rgb(10, 0, 0);
    }

    private static int Hsv(double h)
    {
        var i = (int)(h * 6) % 6; var f = h * 6 - Math.Floor(h * 6);
        var q = (int)(255 * (1 - f)); var u = (int)(255 * f);
        return i switch { 0 => Rgb(255, u, 0), 1 => Rgb(q, 255, 0), 2 => Rgb(0, 255, u), 3 => Rgb(0, q, 255), 4 => Rgb(u, 0, 255), _ => Rgb(255, 0, q) };
    }
}
