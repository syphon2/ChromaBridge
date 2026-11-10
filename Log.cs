namespace GameLightSync;

internal static class Log
{
    private static readonly object Gate = new();

    // Next to the exe: AppData writes from the packaged (app identity) process do not land in the normal AppData folder.
    public static string Dir { get; } = AppContext.BaseDirectory;

    public static string FilePath { get; } = Path.Combine(Dir, "log.txt");

    static Log()
    {
        Directory.CreateDirectory(Dir);
        // keep the log from growing forever
        try { if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath); } catch { }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        }
        Console.WriteLine(line);
    }
}
