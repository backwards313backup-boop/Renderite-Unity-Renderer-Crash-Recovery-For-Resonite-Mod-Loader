using System.Text;

namespace RenderiteRecovery;

internal static class RendererDiagnostics
{
    private const int KeptRendererLogs = 20;
    private const int MaximumExceptionLines = 40;
    private const string LogPrefix = "RenderiteRecovery.Renderer-";

    private sealed record Launch(string? LogPath, DateTime StartedUtc, string Description);

    private static Launch? _current;
    private static int _reported;

    internal static string? BeginReplacement(string appPath, int attempt)
    {
        string? path = null;
        if (Settings.WriteLogFiles)
        {
            string directory = Path.Combine(appPath, "Logs");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"{LogPrefix}{DateTime.Now:yyyy-MM-dd HH_mm_ss}-attempt{attempt}.log");
            Prune(directory);
        }
        Volatile.Write(ref _current, new Launch(path, DateTime.UtcNow, $"renderer started by recovery attempt {attempt}"));
        Volatile.Write(ref _reported, 0);
        return path;
    }

    internal static string LogArguments(string? logPath) => logPath is null ? "-nolog" : $"-logFile \"{logPath}\"";

    internal static void ObserveOriginal(DateTime? startedUtc)
    {
        if (Volatile.Read(ref _current) is not null)
            return;

        string? log = DefaultPlayerLog();
        if (log is null)
            return;

        Volatile.Write(ref _current, new Launch(log, startedUtc ?? DateTime.UtcNow.AddDays(-1), "renderer"));
    }

    internal static void ReportStopped(string reason)
    {
        Launch? launch = Volatile.Read(ref _current);
        if (launch is null || Interlocked.Exchange(ref _reported, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            try { Report(launch, reason); }
            catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not read the renderer's diagnostics: {ex.Message}"); }
        });
    }

    private static void Report(Launch launch, string reason)
    {
        var text = new StringBuilder();
        text.Append($"Why the {launch.Description} stopped ({reason}): ");
        string? fatal = null;
        string? exception = launch.LogPath is null ? null : ReadLastException(launch.LogPath, out fatal);
        if (launch.LogPath is null)
            text.Append("it was started without a log (write_log_files is false), so its exception is unknown.");
        else if (exception is not null)
            text.Append("the last exception in its log is\n").Append(exception);
        else
            text.Append("its log has no exception.");

        if (fatal is not null)
            text.Append('\n').Append(fatal.Trim());

        List<string> crashes = CollectCrashFolders(launch.StartedUtc);
        if (crashes.Count > 0)
            text.Append("\nUnity wrote a native crash dump: ").Append(string.Join(", ", crashes));
        else if (exception is null)
            text.Append(" No native crash dump was written either (the process was terminated or exited on its own).");

        if (launch.LogPath is not null)
            text.Append($"\nRenderer log: {launch.LogPath}");

        if (exception is not null || crashes.Count > 0)
            RenderiteRecoveryMod.Error(text.ToString());
        else
            RenderiteRecoveryMod.Warn(text.ToString());
    }

    internal static string? ReadLastException(string path, out string? fatalLine)
    {
        fatalLine = null;
        if (!File.Exists(path))
            return null;

        string[] lines;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            lines = reader.ReadToEnd().Split('\n');

        int start = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].TrimEnd('\r');
            if (fatalLine is null && (line.Contains("FatalError: True") || line.StartsWith("Crash!!!")))
                fatalLine = line;

            if (IsExceptionStart(line))
            {
                start = i;
                break;
            }
        }
        if (start < 0)
            return null;

        if (start > 0 && lines[start - 1].StartsWith("Exception in messaging system"))
            start--;

        var block = new StringBuilder();
        for (int i = start, taken = 0; i < lines.Length && taken < MaximumExceptionLines; i++, taken++)
        {
            string line = lines[i].TrimEnd('\r');
            if (i > start && (line.StartsWith("0x0") || line.StartsWith("(Filename:") || line.Length == 0))
                break;

            block.Append("    ").AppendLine(line);
        }
        return block.ToString().TrimEnd();
    }

    private static bool IsExceptionStart(string line) => line.StartsWith("Exception in messaging system") || line.StartsWith("Unhandled exception") || line.StartsWith("Crash!!!") || (!line.StartsWith(" ") && !line.StartsWith("\t") && line.Contains("Exception: ") && !line.StartsWith("  at "));

    private static List<string> CollectCrashFolders(DateTime sinceUtc)
    {
        var found = new List<string>();
        string? root = CrashRoot();
        if (root is null || !Directory.Exists(root))
            return found;

        string? appPath = FrooxEngine.Engine.Current?.AppPath;
        foreach (DirectoryInfo crash in new DirectoryInfo(root).GetDirectories()
                     .Where(directory => directory.CreationTimeUtc >= sinceUtc.AddSeconds(-5))
                     .OrderBy(directory => directory.CreationTimeUtc))
        {
            string location = crash.FullName;
            if (appPath is not null && Settings.WriteLogFiles)
            {
                try
                {
                    string target = Path.Combine(appPath, "Logs", "RenderiteRecovery.Crashes", crash.Name);
                    Directory.CreateDirectory(target);
                    foreach (FileInfo file in crash.GetFiles())
                        file.CopyTo(Path.Combine(target, file.Name), overwrite: true);

                    location = target;
                }
                catch (Exception ex)
                {
                    RenderiteRecoveryMod.Warn($"Could not copy the renderer crash folder {crash.FullName}: {ex.Message}");
                }
            }
            found.Add(location);
        }
        return found;
    }

    private static (string Company, string Product)? AppInfo()
    {
        try
        {
            string? rendererPath = FrooxEngine.Engine.Current?.RenderSystem?.RendererPath;
            if (rendererPath is null)
                return null;

            string info = Path.Combine(Path.GetDirectoryName(rendererPath)!, Path.GetFileNameWithoutExtension(rendererPath) + "_Data", "app.info");
            string[] lines = File.ReadAllLines(info);
            return lines.Length >= 2 ? (lines[0].Trim(), lines[1].Trim()) : null;
        }
        catch (Exception) { return null; }
    }

    private static string? CrashRoot() => AppInfo() is (string company, string product) ? Path.Combine(Path.GetTempPath(), company, product, "Crashes") : null;

    private static string? DefaultPlayerLog()
    {
        if (AppInfo() is not (string company, string product))
            return null;

        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string? appData = Path.GetDirectoryName(local);
            return appData is null ? null : Path.Combine(appData, "LocalLow", company, product, "Player.log");
        }
        string config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(config, "unity3d", company, product, "Player.log");
    }

    private static void Prune(string directory)
    {
        try
        {
            foreach (FileInfo old in new DirectoryInfo(directory).GetFiles(LogPrefix + "*.log")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(KeptRendererLogs - 1))
                old.Delete();
        }
        catch (Exception) { }
    }
}
