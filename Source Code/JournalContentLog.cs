using System.Diagnostics;
using System.Globalization;
using System.Text;
using FrooxEngine;

namespace RenderiteRecovery;

internal static class JournalContentLog
{
    internal const string FileName = "RenderiteRecovery.JournalContent.txt";

    private static int _writing;

    internal sealed record Result(bool Success, int Entries, string Message);

    internal static string? ReportPath
    {
        get
        {
            string? appPath = Engine.Current?.AppPath;
            return appPath is null ? null : Path.Combine(appPath, "Logs", FileName);
        }
    }

    internal static bool Write(Action<Result> finished)
    {
        if (ReportPath is not string path || Interlocked.Exchange(ref _writing, 1) != 0)
            return false;

        CommandJournal.Contents contents;
        Dictionary<(string Family, int AssetId), string> names;
        DateTime written = DateTime.Now;
        long now = Stopwatch.GetTimestamp();
        try
        {
            contents = RecoveryCoordinator.JournalContents();
            names = AssetNames.Resolve(Engine.Current?.RenderSystem, contents.Entries
                .Where(entry => entry.AssetId is int)
                .Select(entry => (JournalTelemetry.FamilyOf(entry.Type), entry.AssetId!.Value)));
        }
        catch
        {
            Volatile.Write(ref _writing, 0);
            throw;
        }

        _ = Task.Run(() =>
        {
            Result result;
            var clock = Stopwatch.StartNew();
            try
            {
                string text = Format(contents, names, written, now);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, text);
                File.Move(temporary, path, overwrite: true);
                long total = contents.Entries.Sum(entry => entry.CommandBytes + entry.PayloadBytes);
                string message = contents.OffReason is string reason
                    ? $"Wrote {path}: the journal is off for this session ({reason})."
                    : $"Wrote {path}: {contents.Entries.Count:N0} journal entries, {DashPanel.Bytes(total)}, in {clock.Elapsed.TotalSeconds:F1} seconds.";
                RenderiteRecoveryMod.Msg(message);
                result = new Result(true, contents.Entries.Count, message);
            }
            catch (Exception ex)
            {
                string message = $"Could not write {path}: {ex.Message}";
                RenderiteRecoveryMod.Warn(message);
                result = new Result(false, 0, message);
            }
            finally
            {
                Volatile.Write(ref _writing, 0);
            }
            finished(result);
        });
        return true;
    }

    private static string Format(CommandJournal.Contents contents, Dictionary<(string Family, int AssetId), string> names,
        DateTime written, long now)
    {
        var text = new StringBuilder();
        text.AppendLine("Renderite Recovery journal content");
        text.AppendLine($"Written {written:yyyy-MM-dd HH:mm:ss}.");
        if (contents.OffReason is string reason)
        {
            text.AppendLine();
            text.AppendLine($"The journal is off for this session ({reason}).");
            return text.ToString();
        }

        List<CommandJournal.EntryInfo> entries = contents.Entries
            .OrderByDescending(entry => entry.CommandBytes + entry.PayloadBytes)
            .ThenBy(entry => entry.Type, StringComparer.Ordinal)
            .ThenBy(entry => entry.AssetId ?? int.MinValue).ToList();
        long commands = entries.Sum(entry => entry.CommandBytes);
        long payloads = entries.Sum(entry => entry.PayloadBytes);
        long total = Math.Max(1, commands + payloads);
        text.AppendLine($"{entries.Count:N0} entries, {DashPanel.Bytes(commands + payloads)} in total: {DashPanel.Bytes(commands)} of commands and {DashPanel.Bytes(payloads)} of payloads. Startup data: {DashPanel.Bytes(contents.InitializationBytes)}.");
        text.AppendLine("Sorted by size, largest first. Size is the command plus its payload (the mesh, texture or buffer data that a recovery uploads again). Age is how long the entry has been in the journal.");
        text.AppendLine();

        double frequency = Stopwatch.Frequency;
        var rows = new List<string[]>(entries.Count);
        long running = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            CommandJournal.EntryInfo entry = entries[i];
            long size = entry.CommandBytes + entry.PayloadBytes;
            running += size;
            string asset = "-", what = "-";
            if (entry.AssetId is int assetId)
            {
                string family = JournalTelemetry.FamilyOf(entry.Type);
                asset = $"{family} {assetId}";
                what = names.GetValueOrDefault((family, assetId), "not found");
            }
            rows.Add([
                (i + 1).ToString("N0", CultureInfo.InvariantCulture),
                DashPanel.Bytes(size),
                entry.PayloadBytes > 0 ? DashPanel.Bytes(entry.PayloadBytes) : "-",
                DashPanel.Bytes(entry.CommandBytes),
                JournalTelemetry.Percent(size * 100.0 / total),
                JournalTelemetry.Percent(running * 100.0 / total),
                JournalTelemetry.Duration(Math.Max(0, now - entry.RecordedTimestamp) / frequency),
                entry.Type,
                asset,
                what
            ]);
        }
        JournalTelemetry.Table(text, ["Rank", "Size", "Payload", "Command", "Share", "Running total", "Age", "Command type", "Asset", "Name/Path/Database URL"], rows);
        return text.ToString();
    }
}
