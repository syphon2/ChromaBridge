using System.Runtime.InteropServices;

namespace GameLightSync;

/// <summary>P/Invoke surface of the Logitech LED SDK 9.00 (LogitechLedEnginesWrapper.dll), which talks to G HUB.</summary>
internal static class LogitechLed
{
    private const string Dll = "LogitechLedEnginesWrapper.dll";

    public const int DeviceTypeMonochrome = 1 << 0;
    public const int DeviceTypeRgb = 1 << 1;
    public const int DeviceTypePerKeyRgb = 1 << 2;
    public const int DeviceTypeAll = DeviceTypeMonochrome | DeviceTypeRgb | DeviceTypePerKeyRgb;

    public const int BitmapWidth = 21;
    public const int BitmapHeight = 6;
    public const int BitmapSize = BitmapWidth * BitmapHeight * 4;

    // LogiLed::KeyName values for keys that are not part of the 21x6 bitmap
    public const int KeyG1 = 0xFFF1;
    public const int KeyGLogo = 0xFFFF1;

    public enum DeviceType { Keyboard = 0x0, Mouse = 0x3, Mousemat = 0x4, Headset = 0x8, Speaker = 0xe }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLedInitWithName(string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLedGetSdkVersion(ref int major, ref int minor, ref int build);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLedSetTargetDevice(int targetDevice);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLedSetLighting(int redPercentage, int greenPercentage, int bluePercentage);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLedSetLightingFromBitmap(byte[] bitmap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLedSetLightingForKeyWithKeyName(int keyName, int redPercentage, int greenPercentage, int bluePercentage);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLedSetLightingForKeyWithScanCode(int scanCode, int redPercentage, int greenPercentage, int bluePercentage);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool LogiLedSetLightingForTargetZone(DeviceType deviceType, int zone, int redPercentage, int greenPercentage, int bluePercentage);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void LogiLedShutdown();

    public static int Pct(int v) => (v * 100 + 127) / 255;
}
