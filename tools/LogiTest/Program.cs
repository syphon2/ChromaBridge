// Interactive check of which Logitech LED SDK calls actually reach the keyboard through G HUB.
using System.Runtime.InteropServices;

const int ALL = 7, RGB = 2, PERKEY = 4;
int[] wasd = { 0x11, 0x1E, 0x1F, 0x20 };
int[] allKeys =
{
    0x01,0x3b,0x3c,0x3d,0x3e,0x3f,0x40,0x41,0x42,0x43,0x44,0x57,0x58,0x137,0x46,0x145,
    0x29,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B,0x0C,0x0D,0x0E,0x152,0x147,0x149,0x45,0x135,0x37,0x4A,
    0x0F,0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1A,0x1B,0x2B,0x153,0x14F,0x151,0x47,0x48,0x49,0x4E,
    0x3A,0x1E,0x1F,0x20,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x1C,0x4B,0x4C,0x4D,
    0x2A,0x2C,0x2D,0x2E,0x2F,0x30,0x31,0x32,0x33,0x34,0x35,0x36,0x148,0x4F,0x50,0x51,0x11C,
    0x1D,0x15B,0x38,0x39,0x138,0x15C,0x15D,0x11D,0x14B,0x150,0x14D,0x52,0x53,
};

byte[] RowsBitmap()
{
    (byte r, byte g, byte b)[] rows = { (255,0,0), (0,255,0), (0,0,255), (255,255,255), (255,255,0), (0,255,255) };
    var bmp = new byte[21 * 6 * 4];
    for (var p = 0; p < 21 * 6; p++)
    {
        var (r, g, b) = rows[p / 21];
        if (p is 44 or 64 or 65 or 66) (r, g, b) = (0, 0, 0); // W A S D
        bmp[p * 4] = b; bmp[p * 4 + 1] = g; bmp[p * 4 + 2] = r; bmp[p * 4 + 3] = 255;
    }
    return bmp;
}

var phases = new (string Expect, Action Apply)[]
{
    ("WHOLE keyboard RED (baseline: set all, target ALL)", () =>
        { Sdk.LogiLedSetTargetDevice(ALL); Sdk.LogiLedSetLighting(100, 0, 0); }),

    ("ROWS: red / green / blue / white / yellow / cyan, with W A S D BLACK (bitmap, target ALL)", () =>
        { Sdk.LogiLedSetTargetDevice(ALL); Sdk.LogiLedSetLightingFromBitmap(RowsBitmap()); }),

    ("BLUE keyboard with W A S D WHITE (per-key by name, target ALL)", () =>
        {
            Sdk.LogiLedSetTargetDevice(ALL);
            foreach (var k in allKeys) Sdk.LogiLedSetLightingForKeyWithKeyName(k, 0, 0, 100);
            foreach (var k in wasd) Sdk.LogiLedSetLightingForKeyWithKeyName(k, 100, 100, 100);
        }),

    ("MAGENTA keyboard with W A S D WHITE (per-key by scan code, target ALL)", () =>
        {
            Sdk.LogiLedSetTargetDevice(ALL);
            foreach (var k in allKeys) Sdk.LogiLedSetLightingForKeyWithScanCode(k, 100, 0, 100);
            foreach (var k in wasd) Sdk.LogiLedSetLightingForKeyWithScanCode(k, 100, 100, 100);
        }),

    ("GREEN keyboard with W A S D WHITE (per-key by name, target PER-KEY only = old ChromaBridge)", () =>
        {
            Sdk.LogiLedSetTargetDevice(PERKEY);
            foreach (var k in allKeys) Sdk.LogiLedSetLightingForKeyWithKeyName(k, 0, 100, 0);
            foreach (var k in wasd) Sdk.LogiLedSetLightingForKeyWithKeyName(k, 100, 100, 100);
        }),

    ("ROWS with W A S D BLACK (bitmap, target RGB)", () =>
        { Sdk.LogiLedSetTargetDevice(RGB); Sdk.LogiLedSetLightingFromBitmap(RowsBitmap()); }),
};

Console.WriteLine("Logitech G HUB lighting test. Close ChromaBridge first (tray -> Exit).");
Console.WriteLine("Look at your keyboard after each step and answer y (yes) / n (no) / or describe what you see.\n");
if (System.Diagnostics.Process.GetProcessesByName("ChromaBridge").Length > 0)
{
    Console.WriteLine("ChromaBridge is still running - please exit it from the tray, then press Enter.");
    Console.ReadLine();
}
if (!Sdk.LogiLedInitWithName("ChromaBridge LogiTest")) { Console.WriteLine("Could not connect to G HUB."); Console.ReadLine(); return; }
Thread.Sleep(500);

var results = new List<string>();
for (var i = 0; i < phases.Length; i++)
{
    var (expect, apply) = phases[i];
    var cts = new CancellationTokenSource();
    var pump = Task.Run(async () => { while (!cts.IsCancellationRequested) { apply(); await Task.Delay(200); } });
    Console.Write($"Step {i + 1}/{phases.Length}: expected {expect}\n   Do you see that? > ");
    var answer = Console.ReadLine() ?? "";
    cts.Cancel(); pump.Wait();
    results.Add($"step {i + 1} [{expect}] => {answer}");
}
Sdk.LogiLedShutdown();

var file = Path.Combine(AppContext.BaseDirectory, "logitest-results.txt");
File.WriteAllLines(file, results);
Console.WriteLine($"\nDone, results saved to {file}. You can restart ChromaBridge now. Press Enter to close.");
Console.ReadLine();

static class Sdk
{
    const string Dll = "LogitechLedEnginesWrapper.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool LogiLedInitWithName(string name);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool LogiLedSetTargetDevice(int t);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool LogiLedSetLighting(int r, int g, int b);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool LogiLedSetLightingFromBitmap(byte[] bmp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool LogiLedSetLightingForKeyWithKeyName(int k, int r, int g, int b);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool LogiLedSetLightingForKeyWithScanCode(int k, int r, int g, int b);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void LogiLedShutdown();
}
