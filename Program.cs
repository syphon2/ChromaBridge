using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ChromaBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\ChromaBridge.SingleInstance", out var isFirst);
        if (!isFirst) return;

        if (args.Contains("--console")) AllocConsole();

        // 1 ms timer resolution for this process: frame pacing with the default 15.6 ms makes updates uneven,
        // and the keyboard drops frames that arrive closer than ~33 ms.
        timeBeginPeriod(1);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(args.Contains("--test")));
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);
}

internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ChromaBridge";

    private readonly NotifyIcon _tray;
    private readonly Bridge _bridge;
    private readonly ToolStripMenuItem _statusItem, _enabledItem, _autostartItem;
    private readonly SynchronizationContext _ui;
    private readonly Icon _iconActive = MakeIcon(Color.FromArgb(68, 214, 44)), _iconIdle = MakeIcon(Color.Gray);
    private FileSystemWatcher? _configWatcher;

    public TrayContext(bool showTestPattern)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Log.Write("ChromaBridge starting");

        _bridge = new Bridge(BridgeConfig.Load());

        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled()) { Checked = true };
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = IsAutostart() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add("Live preview...", null, (_, _) => ShowPreview());
        menu.Items.Add("Show test pattern (30 s)", null, (_, _) => _bridge.ShowTestPattern(TimeSpan.FromSeconds(30)));
        menu.Items.Add("Test animation (15 s)", null, (_, _) => _bridge.ShowTestAnimation(TimeSpan.FromSeconds(15)));
        menu.Items.Add("Restore normal lighting", null, (_, _) => _bridge.RequestRestoreNormalLighting());
        var hidppItem = new ToolStripMenuItem("Direct HID++ (recommended)");
        var dlItem = new ToolStripMenuItem("Windows Dynamic Lighting");
        var ghubItem = new ToolStripMenuItem("G HUB SDK (one color only)");
        hidppItem.Click += (_, _) => SetConfigValue("output", "hidpp");
        dlItem.Click += (_, _) => SetConfigValue("output", "dynamicLighting");
        ghubItem.Click += (_, _) => SetConfigValue("output", "ghub");
        var outputMenu = new ToolStripMenuItem("Output", null, hidppItem, dlItem, ghubItem);
        outputMenu.DropDownOpening += (_, _) =>
        {
            var c = BridgeConfig.Load();
            hidppItem.Checked = c.UseHidpp;
            dlItem.Checked = c.UseDynamicLighting;
            ghubItem.Checked = !c.UseHidpp && !c.UseDynamicLighting;
        };
        menu.Items.Add(outputMenu);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Edit settings (bridge.json)", null, (_, _) => Open(BridgeConfig.FilePath));
        menu.Items.Add("Open log", null, (_, _) => Open(Log.FilePath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = _iconIdle, Text = "ChromaBridge", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowPreview();

        _bridge.StatusChanged += s => _ui.Post(_ => UpdateStatus(s), null);
        _bridge.Start();
        WatchConfig();

        if (showTestPattern) _bridge.ShowTestPattern(TimeSpan.FromSeconds(8));
    }

    private void UpdateStatus(string status)
    {
        _statusItem.Text = status;
        _tray.Text = ("ChromaBridge - " + status) is var t && t.Length > 63 ? t[..63] : t;
        _tray.Icon = status.StartsWith("Mirroring") || status.StartsWith("Showing") ? _iconActive : _iconIdle;
    }

    private PreviewForm? _preview;

    private void ShowPreview()
    {
        if (_preview is { IsDisposed: false }) { _preview.Activate(); return; }
        _preview = new PreviewForm(_bridge);
        _preview.Show();
    }

    /// <summary>Rewrites one string value in bridge.json (keeps comments); the file watcher reloads it.</summary>
    private static void SetConfigValue(string key, string value)
    {
        var path = BridgeConfig.FilePath;
        var json = File.Exists(path) ? File.ReadAllText(path) : "{\n}";
        var pattern = new System.Text.RegularExpressions.Regex($"\"{key}\"\\s*:\\s*\"[^\"]*\"");
        json = pattern.IsMatch(json)
            ? pattern.Replace(json, $"\"{key}\": \"{value}\"", 1)
            : json.Insert(json.IndexOf('{') + 1, $"\n  \"{key}\": \"{value}\",");
        File.WriteAllText(path, json);
        Log.Write($"{key} set to {value}");
    }

    private void ToggleEnabled()
    {
        _enabledItem.Checked = !_enabledItem.Checked;
        _bridge.Enabled = _enabledItem.Checked;
        _bridge.ReloadConfig(BridgeConfig.Load());
    }

    private void WatchConfig()
    {
        _configWatcher = new FileSystemWatcher(AppContext.BaseDirectory, "bridge.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _configWatcher.Changed += (_, _) =>
        {
            Thread.Sleep(200); // let the editor finish writing
            _bridge.ReloadConfig(BridgeConfig.Load());
            Log.Write("Reloaded bridge.json");
        };
    }

    // Autostart uses a scheduled task "at logon": Windows skips Run-key entries for ChromaBridge.exe because the exe
    // has package identity (the sparse package for Dynamic Lighting), so the Run key never started it.
    private const string TaskName = "ChromaBridge";

    private static bool IsAutostart() => Run("schtasks.exe", $"/Query /TN {TaskName}") == 0;

    private void ToggleAutostart()
    {
        if (IsAutostart())
            RunPowerShell($"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false");
        else
            RunPowerShell(
                $"$u = \"$env:USERDOMAIN\\$env:USERNAME\"; " +
                $"$a = New-ScheduledTaskAction -Execute '{Environment.ProcessPath}' -WorkingDirectory '{AppContext.BaseDirectory.TrimEnd('\\')}'; " +
                "$t = New-ScheduledTaskTrigger -AtLogOn -User $u; $t.Delay = 'PT15S'; " +
                "$p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Limited; " +
                "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
                $"Register-ScheduledTask -TaskName '{TaskName}' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null");
        // the old Run-key entry never worked; remove it so there is only one autostart
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) key?.DeleteValue(RunValue, false);
        _autostartItem.Checked = IsAutostart();
        Log.Write($"Start with Windows: {(_autostartItem.Checked ? "on (scheduled task at logon)" : "off")}");
    }

    private static int RunPowerShell(string command) =>
        Run("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " +
                              Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)));

    private static int Run(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    private static void Open(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // little keyboard: rounded body + three rows of keys
            using var body = new SolidBrush(Color.FromArgb(40, 40, 40));
            g.FillRectangle(body, 1, 7, 30, 18);
            using var key = new SolidBrush(color);
            for (var row = 0; row < 3; row++)
                for (var col = 0; col < 6; col++)
                    g.FillRectangle(key, 3 + col * 4.6f, 9 + row * 5, 3.4f, 3.4f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _configWatcher?.Dispose();
        _tray.Visible = false;
        _bridge.Dispose();
        Log.Write("ChromaBridge stopped");
        base.ExitThreadCore();
    }
}
