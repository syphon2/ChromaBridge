using System.Runtime.InteropServices;
using ChromaBridge;

/// <summary>Step-by-step check of which 0x8081 message formats the G915 X really applies.</summary>
internal static class KeyDiag
{
    private static Hidpp _dev = null!;
    private static byte _pk;
    private static readonly List<string> Results = [];

    private static byte K(int usage) => (byte)(usage >= 0xE0 ? usage - 0x78 : usage - 3);
    private static readonly int[] AllUsages =
        Enumerable.Range(0x04, 0x65 - 0x04 + 1).Concat(Enumerable.Range(0xE0, 8)).ToArray();

    public static void Run()
    {
        timeBeginPeriod(1);
        Console.WriteLine("G915 X per-key message check (Dynamic Lighting must still be OFF, ChromaBridge closed)");
        Console.Write("Press Enter to start... ");
        Console.ReadLine();

        _dev = Hidpp.OpenLongCollections(productId: 0xC356).First();
        _dev.DeviceIndex = 0xFF;
        _pk = _dev.FeatureIndex(0x8081)!.Value;
        var fx = _dev.FeatureIndex(0x8071)!.Value;
        _dev.RequestWithError(fx, 5, [1, 3, 6]);

        // Base: everything dim blue, written with the message type that already worked (one color, 13 keys)
        Fill(AllUsages, 0, 0, 60);
        Commit();
        _dev.RequestWithError(fx, 5, [1, 3, 5]);
        Ask("Whole keyboard dim BLUE?");

        Individual([(0x1A, 255, 0, 0)], terminator: false);               // W
        Commit();
        Ask("Only W turned RED (rest still blue)?");

        Individual([(0x04, 255, 0, 0), (0x16, 0, 255, 0), (0x07, 0, 0, 255), (0x09, 255, 255, 255)], terminator: false);
        Commit();
        Ask("A red, S green, D bright blue, F white?");

        Individual([(0x14, 255, 255, 0), (0x08, 255, 255, 0)], terminator: true); // Q, E with 0xFF end marker
        Commit();
        Ask("Q and E YELLOW?");

        Fill([0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27], 255, 0, 255); // number row
        Individual([(0x29, 0, 255, 0)], terminator: false);                               // Esc
        Commit();
        Ask("Number row 1-0 MAGENTA and Esc GREEN (both in the same frame)?");

        MovingDot(terminator: false);
        Ask("A white dot moved smoothly along Q W E R T Z U I O P (version A)?");
        MovingDot(terminator: true);
        Ask("Same again (version B with end marker) - did the dot move?");

        _dev.RequestWithError(fx, 5, [1, 0, 0]);
        var file = Path.Combine(AppContext.BaseDirectory, "keydiag-results.txt");
        File.WriteAllLines(file, Results);
        Console.WriteLine($"\nSaved to {file}. Turn Dynamic Lighting back on. Press Enter to close.");
        Console.ReadLine();
    }

    private static void MovingDot(bool terminator)
    {
        int[] row = [0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13];
        Console.WriteLine("   watch the Q-P row...");
        var prev = -1;
        for (var step = 0; step < 40; step++)
        {
            var cur = row[step % row.Length];
            var entries = new List<(int, byte, byte, byte)> { (cur, 255, 255, 255) };
            if (prev >= 0) entries.Insert(0, (prev, 0, 0, 60));
            Individual(entries, terminator);
            Commit();
            prev = cur;
            Thread.Sleep(100);
        }
        Individual([(prev, 0, 0, 60)], terminator);
        Commit();
    }

    /// <summary>fn6 SetRgbZonesSingleValue: one color, up to 13 keys per message.</summary>
    private static void Fill(int[] usages, byte r, byte g, byte b)
    {
        for (var i = 0; i < usages.Length; i += 13)
        {
            var p = new byte[16];
            p[0] = r; p[1] = g; p[2] = b;
            var chunk = usages.Skip(i).Take(13).ToArray();
            for (var j = 0; j < chunk.Length; j++) p[3 + j] = K(chunk[j]);
            if (chunk.Length < 13) p[3 + chunk.Length] = 0xFF;
            Check("fill", _dev.RequestWithError(_pk, 6, p).Error);
        }
    }

    /// <summary>fn1 SetIndividualRgbZones: up to 4 [key, r, g, b] entries; unused space zero (A) or 0xFF-terminated (B).</summary>
    private static void Individual(List<(int Usage, byte R, byte G, byte B)> entries, bool terminator)
    {
        var p = new byte[16];
        for (var j = 0; j < entries.Count; j++)
        {
            p[4 * j] = K(entries[j].Usage);
            p[4 * j + 1] = entries[j].R; p[4 * j + 2] = entries[j].G; p[4 * j + 3] = entries[j].B;
        }
        if (terminator && entries.Count < 4) p[4 * entries.Count] = 0xFF;
        Check("individual", _dev.RequestWithError(_pk, 1, p).Error);
    }

    private static void Commit()
    {
        var deadline = Environment.TickCount64 + 250;
        while (true)
        {
            var e = _dev.RequestWithError(_pk, 7, new byte[16]).Error;
            if (e == Hidpp.ErrorBusy && Environment.TickCount64 < deadline) { Thread.Sleep(1); continue; }
            Check("commit", e);
            return;
        }
    }

    private static void Check(string what, int error)
    {
        if (error != 0) { Console.WriteLine($"   ({what} error {error})"); Results.Add($"   {what} error {error}"); }
    }

    private static void Ask(string question)
    {
        Console.Write($"{question} (y/n/describe) > ");
        Results.Add($"{question} => {Console.ReadLine()}");
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
}
