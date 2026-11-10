// HID++ probe for the G915 X. "list" is read-only: finds the keyboard, prints its features and measures latency.
// "anim" drives the keyboard directly via feature 0x8081 with game-like animations (visual test, asks for feedback).
using System.Diagnostics;
using ChromaBridge;

var mode = args.Length > 0 ? args[0] : "anim";
if (mode == "anim") { AnimTest.Run(); return; }
if (mode == "keys") { KeyDiag.Run(); return; }
if (mode == "swset")
{
    // sets the software-control mode: swset <mode> <flags>
    var kb = Hidpp.OpenLongCollections(productId: 0xC356).First();
    kb.DeviceIndex = 0xFF;
    var fx = kb.FeatureIndex(0x8071)!.Value;
    var (d, e) = kb.RequestWithError(fx, 5, [1, byte.Parse(args[1]), byte.Parse(args[2])]);
    Console.WriteLine($"SWControl set {args[1]}/{args[2]}: error {e} {(d == null ? "" : Convert.ToHexString(d))}");
    return;
}
if (mode == "swget")
{
    // read-only: current software-control state (0x8071 fn5 GET) and power mode (fn8)
    var kb = Hidpp.OpenLongCollections(productId: 0xC356).First();
    kb.DeviceIndex = 0xFF;
    var fx = kb.FeatureIndex(0x8071)!.Value;
    var (sw, e1) = kb.RequestWithError(fx, 5, [0]);
    var (pw, e2) = kb.RequestWithError(fx, 8, [0]);
    Console.WriteLine($"SWControl get: {(sw == null ? $"error {e1}" : Convert.ToHexString(sw))}");
    Console.WriteLine($"PowerMode get: {(pw == null ? $"error {e2}" : Convert.ToHexString(pw))}");
    return;
}
if (mode == "paint")
{
    // paints all zones in one color (default: the user's normal Dynamic Lighting color) and prints every reply
    var rgb = args.Length > 1 ? Convert.ToInt32(args[1], 16) : 0x4343FF;
    var kb = Hidpp.OpenLongCollections(productId: 0xC356).First();
    kb.DeviceIndex = 0xFF;
    var pk = kb.FeatureIndex(0x8081)!.Value;
    var fx = kb.FeatureIndex(0x8071)!.Value;
    Console.WriteLine($"claim [1,3,6]: error {kb.RequestWithError(fx, 5, [1, 3, 6]).Error}");
    var all = Enumerable.Range(0x01, 0x6F).Concat([0x99, 0x9B, 0x9C, 0x9D, 0x9E, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xD2]).ToList();
    for (var i = 0; i < all.Count; i += 13)
    {
        var p = new byte[16];
        p[0] = (byte)(rgb >> 16); p[1] = (byte)(rgb >> 8); p[2] = (byte)rgb;
        var chunk = all.Skip(i).Take(13).ToList();
        for (var j = 0; j < chunk.Count; j++) p[3 + j] = (byte)chunk[j];
        if (chunk.Count < 13) p[3 + chunk.Count] = 0xFF;
        var sw = Stopwatch.StartNew();
        var (_, e) = kb.RequestWithError(pk, 6, p, 300);
        Console.WriteLine($"zones 0x{chunk[0]:X2}-0x{chunk[^1]:X2}: error {e} ({sw.ElapsedMilliseconds} ms)");
    }
    Console.WriteLine($"commit: error {kb.RequestWithError(pk, 7, new byte[16], 300).Error}");
    Console.WriteLine($"claim [1,3,5]: error {kb.RequestWithError(fx, 5, [1, 3, 5]).Error}");
    return;
}
if (mode == "zones")
{
    // read-only: which 0x8081 zone ids exist (GetInfo pages 0-2, 112 zones per page, LSB-first bitmap)
    var kb = Hidpp.OpenLongCollections(productId: 0xC356).First();
    kb.DeviceIndex = 0xFF;
    var pk = kb.FeatureIndex(0x8081)!.Value;
    var zones = new List<int>();
    for (byte page = 0; page < 3; page++)
    {
        var (data, error) = kb.RequestWithError(pk, 0, [0, page, 0]);
        Console.WriteLine($"page {page}: {(data == null ? $"error {error}" : Convert.ToHexString(data))}");
        if (data == null) continue;
        for (var bit = 0; bit < 112; bit++)
            if ((data[2 + bit / 8] & (1 << (bit % 8))) != 0) zones.Add(page * 112 + bit);
    }
    Console.WriteLine($"{zones.Count} zones: " + string.Join(" ", zones.Select(z => $"0x{z:X2}")));
    foreach (var probe in new[] { 0x99, 0x9B, 0x9C, 0x9D, 0x9E, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xD2 })
        Console.Write($"0x{probe:X2}:{(zones.Contains(probe) ? "yes" : "NO")}  ");
    Console.WriteLine();
    return;
}
var names = new Dictionary<ushort, string>
{
    [0x0000] = "Root", [0x0001] = "FeatureSet", [0x0003] = "DeviceInfo", [0x0005] = "DeviceName",
    [0x1004] = "UnifiedBattery", [0x1B04] = "ReprogControls", [0x1D4B] = "WirelessStatus",
    [0x4522] = "DisableKeysByUsage?", [0x8040] = "Brightness", [0x8070] = "ColorLedEffects",
    [0x8071] = "RgbEffects", [0x8080] = "PerKeyLighting", [0x8081] = "PerKeyLightingV2",
    [0x8100] = "OnboardProfiles", [0x8010] = "GKeys", [0x8020] = "MKeys", [0x8030] = "MR",
    [0x18A1] = "LedSwControl", [0x1990] = "Illumination",
};

foreach (var dev in Hidpp.OpenLongCollections())
{
    using (dev)
    {
        Console.WriteLine($"collection: {dev.Path}");
        foreach (byte index in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
        {
            dev.DeviceIndex = index;
            var ping = dev.Request(0x00, 1, [0, 0, 0x5A], 300);
            if (ping == null) continue;
            Console.WriteLine($"  device index 0x{index:X2}: HID++ {ping[0]}.{ping[1]}");

            // name
            var nameIdx = dev.FeatureIndex(0x0005);
            if (nameIdx != null)
            {
                var len = dev.Request(nameIdx.Value, 0, [])?[0] ?? 0;
                var name = "";
                while (name.Length < len)
                {
                    var part = dev.Request(nameIdx.Value, 1, [(byte)name.Length]);
                    if (part == null) break;
                    name += System.Text.Encoding.ASCII.GetString(part).TrimEnd('\0');
                    if (part.All(b => b == 0)) break;
                }
                Console.WriteLine($"    name: {name}");
            }

            // feature list
            var setIdx = dev.FeatureIndex(0x0001);
            if (setIdx != null)
            {
                var count = dev.Request(setIdx.Value, 0, [])?[0] ?? 0;
                for (var i = 1; i <= count; i++)
                {
                    var f = dev.Request(setIdx.Value, 1, [(byte)i]);
                    if (f == null) continue;
                    var id = (ushort)(f[0] << 8 | f[1]);
                    Console.WriteLine($"    feature idx 0x{i:X2}: 0x{id:X4} {names.GetValueOrDefault(id, "")}{(f[2] != 0 ? $" (type 0x{f[2]:X2})" : "")}");
                }
            }

            // per-key lighting info
            var pk = dev.FeatureIndex(0x8081);
            if (pk != null)
            {
                var info = dev.Request(pk.Value, 0, [0, 0, 0]);
                Console.WriteLine($"    0x8081 getInfo: {(info == null ? "error" : Convert.ToHexString(info))}");
            }

            // latency
            var sw = Stopwatch.StartNew();
            int ok = 0;
            var times = new List<double>();
            for (var i = 0; i < 30; i++)
            {
                var t0 = sw.Elapsed.TotalMilliseconds;
                if (dev.Request(0x00, 1, [0, 0, (byte)i], 300) != null) { ok++; times.Add(sw.Elapsed.TotalMilliseconds - t0); }
            }
            if (times.Count > 0)
                Console.WriteLine($"    round trip: {ok}/30 ok, avg {times.Average():F1} ms, min {times.Min():F1}, max {times.Max():F1}");
        }
    }
}
Console.WriteLine("done");
