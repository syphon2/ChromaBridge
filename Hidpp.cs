using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameLightSync;

/// <summary>
/// Minimal Logitech HID++ 2.0 client over the vendor-defined "long report" HID collection (report id 0x11, 20 bytes).
/// Used to talk to the keyboard directly (feature 0x8081 per-key lighting), next to G HUB.
/// </summary>
internal sealed class Hidpp : IDisposable
{
    public const byte LongReport = 0x11;
    public const int LongLength = 20;
    private const byte SoftwareId = 0x0B; // distinguishes our replies from G HUB's

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly System.Threading.Channels.Channel<byte[]> _incoming =
        System.Threading.Channels.Channel.CreateBounded<byte[]>(new System.Threading.Channels.BoundedChannelOptions(256)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    public string Path { get; }
    public byte DeviceIndex { get; set; } = 0xFF;

    /// <summary>
    /// Every input report on this collection, also the answers to other programs' requests (G HUB, Windows):
    /// HID delivers input reports to every open handle. Raised on the reader thread.
    /// </summary>
    public event Action<byte[]>? ReportReceived;

    private Hidpp(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.ReadWrite, LongLength, isAsync: true);
        _ = Task.Run(ReadLoop);
    }

    /// <summary>Single reader: every input report on this collection (ours and G HUB's replies) goes into a queue.</summary>
    private async Task ReadLoop()
    {
        var buf = new byte[64];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await _stream.ReadAsync(buf, _cts.Token);
                if (n <= 0) continue;
                var report = buf.AsSpan(0, n).ToArray();
                _incoming.Writer.TryWrite(report);
                try { ReportReceived?.Invoke(report); } catch { /* a listener must not stop the reader */ }
            }
        }
        catch { /* handle closed */ }
    }

    /// <summary>Opens every Logitech HID collection whose reports are HID++ long reports (20 bytes).</summary>
    public static List<Hidpp> OpenLongCollections(ushort vendorId = 0x046D, ushort? productId = null) =>
        OpenVendorCollections(vendorId, productId, LongLength);

    /// <summary>Opens the vendor-defined (0xFF00) HID++ collections with the given output report lengths (7 = short, 20 = long).</summary>
    public static List<Hidpp> OpenVendorCollections(ushort vendorId, ushort? productId, params int[] reportLengths)
    {
        var result = new List<Hidpp>();
        foreach (var path in HidPaths())
        {
            var lower = path.ToLowerInvariant();
            if (!lower.Contains($"vid_{vendorId:x4}")) continue;
            if (productId != null && !lower.Contains($"pid_{productId:x4}")) continue;

            var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                               OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (HidD_GetPreparsedData(h, out var pre))
            {
                try
                {
                    if (HidP_GetCaps(pre, out var caps) == HIDP_STATUS_SUCCESS &&
                        caps.UsagePage == 0xFF00 && reportLengths.Contains(caps.OutputReportByteLength))
                    {
                        result.Add(new Hidpp(path, h));
                        continue;
                    }
                }
                finally { HidD_FreePreparsedData(pre); }
            }
            h.Dispose();
        }
        return result;
    }

    /// <summary>Sends a request without waiting for the answer (for streaming lighting frames).</summary>
    public void Send(byte featureIndex, byte function, ReadOnlySpan<byte> parameters)
    {
        var buf = new byte[LongLength];
        buf[0] = LongReport;
        buf[1] = DeviceIndex;
        buf[2] = featureIndex;
        buf[3] = (byte)(function << 4 | SoftwareId);
        parameters[..Math.Min(parameters.Length, 16)].CopyTo(buf.AsSpan(4));
        _stream.Write(buf, 0, buf.Length);
    }

    /// <summary>Sends a request and waits for the matching reply. Returns the 16 parameter bytes, or null on error/timeout.</summary>
    public byte[]? Request(byte featureIndex, byte function, ReadOnlySpan<byte> parameters, int timeoutMs = 500) =>
        RequestWithError(featureIndex, function, parameters, timeoutMs).Data;

    /// <summary>HID++ 2.0 error code for "busy": the device is still applying the previous frame.</summary>
    public const int ErrorBusy = 0x08;

    /// <summary>
    /// All our requests share one software id and the device broadcasts its answers to every open handle, so two
    /// requests in flight at once (e.g. the keyboard monitor and an output) could take each other's answers.
    /// One request at a time in the whole process (~2 ms each).
    /// </summary>
    private static readonly object RequestLock = new();

    /// <summary>Like <see cref="Request"/>, but also returns the HID++ error code (0 = ok, -1 = timeout / no answer).</summary>
    public (byte[]? Data, int Error) RequestWithError(byte featureIndex, byte function, ReadOnlySpan<byte> parameters, int timeoutMs = 500)
    {
        lock (RequestLock) return RequestUnlocked(featureIndex, function, parameters, timeoutMs);
    }

    private (byte[]? Data, int Error) RequestUnlocked(byte featureIndex, byte function, ReadOnlySpan<byte> parameters, int timeoutMs)
    {
        while (_incoming.Reader.TryRead(out _)) { } // drop stale reports
        Send(featureIndex, function, parameters);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            using var wait = new CancellationTokenSource((int)Math.Max(1, deadline - Environment.TickCount64));
            byte[] buf;
            try { buf = _incoming.Reader.ReadAsync(wait.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return (null, -1); }
            var n = buf.Length;
            if (n < 7 || buf[1] != DeviceIndex) continue;
            if (buf[0] == LongReport && buf[2] == featureIndex && buf[3] == (byte)(function << 4 | SoftwareId))
                return (buf.AsSpan(4, 16).ToArray(), 0);
            // HID++ 2.0 error: feature index 0xFF, then our feature/function and the error code
            if (buf[2] == 0xFF && buf[3] == featureIndex && buf[4] == (byte)(function << 4 | SoftwareId)) return (null, buf[5]);
            // HID++ 1.0 error from the receiver (e.g. device not connected at this index)
            if (buf[0] == 0x10 && buf[2] == 0x8F) return (null, buf[5]);
        }
        return (null, -1);
    }

    /// <summary>Root feature getFeature: index of a feature id, or null when unsupported.</summary>
    public byte? FeatureIndex(ushort featureId)
    {
        var r = Request(0x00, 0, [(byte)(featureId >> 8), (byte)featureId]);
        return r == null || r[0] == 0 ? null : r[0];
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
        _handle.Dispose();
    }

    // ---------------- Win32 ----------------

    private static IEnumerable<string> HidPaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(detail + 4)!;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
