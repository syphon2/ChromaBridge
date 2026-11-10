namespace ChromaBridge;

/// <summary>
/// Maps the Razer Chroma keyboard grid (6 rows x 22 columns, key id 0xRRCC) onto the
/// Logitech LED SDK per-key bitmap (6 rows x 21 columns, US/ISO layout from the SDK docs).
/// </summary>
internal static class KeyMap
{
    public const int RazerRows = 6;
    public const int RazerCols = 22;

    /// <summary>Razer grid index (row * 22 + col) -> Logitech bitmap pixel index, or -1.</summary>
    public static readonly int[] RazerToLogiPixel = Build();

    /// <summary>Razer macro column (col 0, rows 1-5 = M1..M5) -> Logitech G1..G5.</summary>
    public static readonly (int RazerIndex, int LogiKey)[] GKeys =
    [
        (1 * RazerCols, LogitechLed.KeyG1 + 0),
        (2 * RazerCols, LogitechLed.KeyG1 + 1),
        (3 * RazerCols, LogitechLed.KeyG1 + 2),
        (4 * RazerCols, LogitechLed.KeyG1 + 3),
        (5 * RazerCols, LogitechLed.KeyG1 + 4),
    ];

    /// <summary>Razer logo LED (RZLED_LOGO = 0x0014).</summary>
    public const int RazerLogoIndex = 0 * RazerCols + 20;

    /// <summary>Scan code for the ISO '&lt;&gt;' key, which has no LogiLed::KeyName.</summary>
    public const int IsoAngleBracketScanCode = 0x56;

    /// <summary>
    /// Logitech bitmap pixel -> LogiLed::KeyName (scan code based, values from LogitechLEDLib.h).
    /// Pixel 75 is the ISO '#' key (same scan code as the ANSI backslash), pixel 85 is ISO '&lt;&gt;'.
    /// </summary>
    private static readonly Dictionary<int, int> PixelToKeyName = new()
    {
        [0] = 0x01, [1] = 0x3b, [2] = 0x3c, [3] = 0x3d, [4] = 0x3e, [5] = 0x3f, [6] = 0x40, [7] = 0x41,
        [8] = 0x42, [9] = 0x43, [10] = 0x44, [11] = 0x57, [12] = 0x58, [13] = 0x137, [14] = 0x46, [15] = 0x145,

        [21] = 0x29, [22] = 0x02, [23] = 0x03, [24] = 0x04, [25] = 0x05, [26] = 0x06, [27] = 0x07, [28] = 0x08,
        [29] = 0x09, [30] = 0x0A, [31] = 0x0B, [32] = 0x0C, [33] = 0x0D, [34] = 0x0E, [35] = 0x152, [36] = 0x147,
        [37] = 0x149, [38] = 0x45, [39] = 0x135, [40] = 0x37, [41] = 0x4A,

        [42] = 0x0F, [43] = 0x10, [44] = 0x11, [45] = 0x12, [46] = 0x13, [47] = 0x14, [48] = 0x15, [49] = 0x16,
        [50] = 0x17, [51] = 0x18, [52] = 0x19, [53] = 0x1A, [54] = 0x1B, [55] = 0x2B, [56] = 0x153, [57] = 0x14F,
        [58] = 0x151, [59] = 0x47, [60] = 0x48, [61] = 0x49, [62] = 0x4E,

        [63] = 0x3A, [64] = 0x1E, [65] = 0x1F, [66] = 0x20, [67] = 0x21, [68] = 0x22, [69] = 0x23, [70] = 0x24,
        [71] = 0x25, [72] = 0x26, [73] = 0x27, [74] = 0x28, [75] = 0x2B, [76] = 0x1C, [80] = 0x4B, [81] = 0x4C,
        [82] = 0x4D,

        [84] = 0x2A, [85] = IsoAngleBracketScanCode, [86] = 0x2C, [87] = 0x2D, [88] = 0x2E, [89] = 0x2F, [90] = 0x30,
        [91] = 0x31, [92] = 0x32, [93] = 0x33, [94] = 0x34, [95] = 0x35, [97] = 0x36, [99] = 0x148, [101] = 0x4F,
        [102] = 0x50, [103] = 0x51, [104] = 0x11C,

        [105] = 0x1D, [106] = 0x15B, [107] = 0x38, [110] = 0x39, [116] = 0x138, [117] = 0x15C, [118] = 0x15D,
        [119] = 0x11D, [120] = 0x14B, [121] = 0x150, [122] = 0x14D, [123] = 0x52, [124] = 0x53,
    };

    /// <summary>Set-1 scan code (as used in <see cref="PixelToKeyName"/>) -> USB HID keyboard usage.</summary>
    private static readonly Dictionary<int, int> ScanToUsage = new()
    {
        [0x01] = 0x29, [0x3B] = 0x3A, [0x3C] = 0x3B, [0x3D] = 0x3C, [0x3E] = 0x3D, [0x3F] = 0x3E, [0x40] = 0x3F,
        [0x41] = 0x40, [0x42] = 0x41, [0x43] = 0x42, [0x44] = 0x43, [0x57] = 0x44, [0x58] = 0x45,
        [0x137] = 0x46, [0x46] = 0x47, [0x145] = 0x48,
        [0x29] = 0x35, [0x02] = 0x1E, [0x03] = 0x1F, [0x04] = 0x20, [0x05] = 0x21, [0x06] = 0x22, [0x07] = 0x23,
        [0x08] = 0x24, [0x09] = 0x25, [0x0A] = 0x26, [0x0B] = 0x27, [0x0C] = 0x2D, [0x0D] = 0x2E, [0x0E] = 0x2A,
        [0x152] = 0x49, [0x147] = 0x4A, [0x149] = 0x4B, [0x45] = 0x53, [0x135] = 0x54, [0x37] = 0x55, [0x4A] = 0x56,
        [0x0F] = 0x2B, [0x10] = 0x14, [0x11] = 0x1A, [0x12] = 0x08, [0x13] = 0x15, [0x14] = 0x17, [0x15] = 0x1C,
        [0x16] = 0x18, [0x17] = 0x0C, [0x18] = 0x12, [0x19] = 0x13, [0x1A] = 0x2F, [0x1B] = 0x30, [0x2B] = 0x31,
        [0x153] = 0x4C, [0x14F] = 0x4D, [0x151] = 0x4E, [0x47] = 0x5F, [0x48] = 0x60, [0x49] = 0x61, [0x4E] = 0x57,
        [0x3A] = 0x39, [0x1E] = 0x04, [0x1F] = 0x16, [0x20] = 0x07, [0x21] = 0x09, [0x22] = 0x0A, [0x23] = 0x0B,
        [0x24] = 0x0D, [0x25] = 0x0E, [0x26] = 0x0F, [0x27] = 0x33, [0x28] = 0x34, [0x1C] = 0x28,
        [0x4B] = 0x5C, [0x4C] = 0x5D, [0x4D] = 0x5E,
        [0x2A] = 0xE1, [IsoAngleBracketScanCode] = 0x64, [0x2C] = 0x1D, [0x2D] = 0x1B, [0x2E] = 0x06, [0x2F] = 0x19,
        [0x30] = 0x05, [0x31] = 0x11, [0x32] = 0x10, [0x33] = 0x36, [0x34] = 0x37, [0x35] = 0x38, [0x36] = 0xE5,
        [0x148] = 0x52, [0x4F] = 0x59, [0x50] = 0x5A, [0x51] = 0x5B, [0x11C] = 0x58,
        [0x1D] = 0xE0, [0x15B] = 0xE3, [0x38] = 0xE2, [0x39] = 0x2C, [0x138] = 0xE6, [0x15C] = 0xE7, [0x15D] = 0x65,
        [0x11D] = 0xE4, [0x14B] = 0x50, [0x150] = 0x51, [0x14D] = 0x4F, [0x52] = 0x62, [0x53] = 0x63,
    };

    /// <summary>
    /// Razer grid index -> HID++ 0x8081 zone code, or -1. Keys use USB HID usage - 3, modifiers usage - 0x78
    /// (same numbering as OpenRGB / Solaar).
    /// </summary>
    public static int[] BuildRazerToHidppCode(bool isoLayout)
    {
        var scans = BuildRazerToLogiKey(isoLayout);
        var codes = new int[scans.Length];
        for (var i = 0; i < scans.Length; i++)
        {
            codes[i] = -1;
            if (scans[i] < 0 || !ScanToUsage.TryGetValue(scans[i], out var usage)) continue;
            if (usage == 0x31 && isoLayout) usage = 0x32; // scan 0x2B is the ISO '#' key on German keyboards
            codes[i] = usage >= 0xE0 ? usage - 0x78 : usage - 3;
        }
        return codes;
    }

    /// <summary>
    /// Keyboard lights outside the Razer grid: G1-G5 follow Razer M1-M5, the logo the Razer logo; when the game
    /// leaves those black (most games do), they take the color of the key next to them. Media keys and the
    /// brightness key take the colors of the keys right below them.
    /// </summary>
    public static readonly (byte Code, int RazerIndex, int Fallback)[] HidppExtraKeys =
    [
        (0xB4, 1 * RazerCols, 1 * RazerCols + 1), (0xB5, 2 * RazerCols, 2 * RazerCols + 1),
        (0xB6, 3 * RazerCols, 3 * RazerCols + 1), (0xB7, 4 * RazerCols, 4 * RazerCols + 1),
        (0xB8, 5 * RazerCols, 5 * RazerCols + 1),
        (0xD2, RazerLogoIndex, 0 * RazerCols + 1),
        (0x9E, 1 * RazerCols + 18, 1 * RazerCols + 18), (0x9B, 1 * RazerCols + 19, 1 * RazerCols + 19),
        (0x9D, 1 * RazerCols + 20, 1 * RazerCols + 20), (0x9C, 1 * RazerCols + 21, 1 * RazerCols + 21),
        (0x99, 0 * RazerCols + 17, 0 * RazerCols + 17),
    ];

    /// <summary>Razer grid index -> Logitech key code for per-key mode, or -1.</summary>
    public static int[] BuildRazerToLogiKey(bool isoLayout)
    {
        var keys = new int[RazerRows * RazerCols];
        for (var i = 0; i < keys.Length; i++)
        {
            var px = RazerToLogiPixel[i];
            keys[i] = px >= 0 && PixelToKeyName.TryGetValue(px, out var k) ? k : -1;
        }
        // ANSI backslash and ISO '#' share scan code 0x2B: keep only the one that exists on this layout
        if (isoLayout) keys[2 * RazerCols + 14] = -1;
        else
        {
            keys[3 * RazerCols + 13] = -1;
            keys[4 * RazerCols + 2] = -1;
        }
        return keys;
    }

    private static int[] Build()
    {
        var map = new int[RazerRows * RazerCols];
        Array.Fill(map, -1);

        void Set(int razerRow, int razerCol, int logiCol) =>
            map[razerRow * RazerCols + razerCol] = razerRow * LogitechLed.BitmapWidth + logiCol;

        // Row 0: ESC (0x0001), F1..F12 (0x0003-0x000E), PrtSc/Scroll/Pause (0x000F-0x0011)
        Set(0, 1, 0);
        for (var c = 3; c <= 17; c++) Set(0, c, c - 2);

        // Rows 1-4 line up one column to the left of Razer (Razer col 0 is the macro column).
        // Row 3 col 13 = ISO '#' key, row 4 col 2 = ISO '<>' key.
        for (var r = 1; r <= 4; r++)
            for (var c = 1; c <= 21; c++)
                Set(r, c, c - 1);

        // Row 5: Razer spreads the space bar / numpad differently than Logitech
        Set(5, 1, 0);   // LCtrl
        Set(5, 2, 1);   // LWin
        Set(5, 3, 2);   // LAlt
        Set(5, 7, 5);   // Space
        Set(5, 11, 11); // RAlt / AltGr
        Set(5, 12, 12); // Fn (Razer) -> RWin slot (Logitech)
        Set(5, 13, 13); // Menu
        Set(5, 14, 14); // RCtrl
        Set(5, 15, 15); // Left
        Set(5, 16, 16); // Down
        Set(5, 17, 17); // Right
        Set(5, 19, 18); // Numpad 0
        Set(5, 20, 19); // Numpad .

        return map;
    }
}
