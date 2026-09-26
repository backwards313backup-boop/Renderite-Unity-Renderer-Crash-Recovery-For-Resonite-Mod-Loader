using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using ResoniteModLoader;

namespace RenderiteRecovery;

internal static class Settings
{
    internal static readonly ModConfigurationKey<int> RecoveryAttemptsKey = new("recovery_attempts",
        "How many recovery attempts are allowed before giving up. Every attempt counts a replacement renderer that fails, and a renderer that fails again soon after a recovery. The count starts over once the renderer has run for stable_seconds after a recovery. An attempt that finds the asset crashing the renderer does not count. Minimum 1.",
        () => 10, valueValidator: value => value >= 1);

    internal static readonly ModConfigurationKey<int> StableSecondsKey = new("stable_seconds",
        "How long, in seconds, the renderer has to run after a recovery before its next failure recovery_attempts resets.",
        () => 120, valueValidator: value => value >= 1);

    internal static readonly ModConfigurationKey<long> JournalLimitMbKey = new("journal_limit_mb",
        "Largest recovery journal, in MB (commands held in memory plus their archived payloads). Past it, recovery is disabled for the session and the journal is freed. Rendering continues normally. 0 = no limit.",
        () => 0, valueValidator: value => value >= 0);

    internal static readonly ModConfigurationKey<int> JournalEntryLimitKey = new("journal_entry_limit",
        "Most journal entries kept. Past it, recovery is disabled for the session like journal_limit_mb. 0 = no limit.",
        () => 0, valueValidator: value => value >= 0);

    internal static readonly ModConfigurationKey<long> ArchiveDiskLimitMbKey = new("archive_disk_limit_mb",
        "Largest payload archive on disk (in the temp folder), in MB, including space not yet reclaimed. Past it, recovery is disabled for the session. 0 = no limit (bounded by free disk space).",
        () => 0, valueValidator: value => value >= 0);

    internal static readonly ModConfigurationKey<string> ArchiveDirectoryKey = new("archive_directory",
        "Folder for the payload archive's segment files. Empty = the system temp folder (%TEMP%). Environment variables such as %LOCALAPPDATA% are expanded, and a relative path is relative to the Resonite folder. If the folder cannot be used, the temp folder is used and the log says why. The files are deleted when Resonite exits.",
        () => "");

    internal static readonly ModConfigurationKey<long> ArchiveMemoryMbKey = new("archive_memory_mb",
        "Payload data, in MB, kept in RAM before the archive starts using disk. Past it, the oldest payloads move to disk, so recently changed (and most often replaced) data stays in RAM. RAM is only used as the journal fills, up to this amount. 0 = everything goes to disk, which can stall asset loading on a slow drive.",
        () => 4096, valueValidator: value => value >= 0);

    internal static readonly ModConfigurationKey<long> CompactionThresholdMbKey = new("compaction_threshold_mb",
        "When abandoned assets in the cache equalto this value in MB are unusued on the disk, start compaction which removes these dead assets to reduce cache file size on disk.",
        () => 512, valueValidator: value => value >= 1);

    internal static readonly ModConfigurationKey<bool> ShowDashPanelKey = new("show_dash_panel",
        "Show the Renderite Recovery screen (status and resource use) on the dash, next to Exit.",
        () => true);

    internal static readonly ModConfigurationKey<bool> ShowForceExitButtonKey = new("show_force_exit_button",
        "Show the \"Force Exit Resonite (Renderite Recovery)\" button on the dash Exit screen. It closes the renderer and ends Resonite at once, without saving or syncing, for when a normal exit hangs.",
        () => true);

    internal static readonly ModConfigurationKey<bool> WriteLogFilesKey = new("write_log_files",
        "Write the mod's log files: Logs/RenderiteRecovery.Host.log, a log for each replacement renderer (Logs/RenderiteRecovery.Renderer-<time>-attempt<N>.log) and copies of Unity crash dumps (Logs/RenderiteRecovery.Crashes).",
        () => true);

    private static readonly (string Old, ModConfigurationKey New)[] RenamedKeys =
    [
        ("write_renderer_logs", WriteLogFilesKey),
        ("crash_loop_window_seconds", StableSecondsKey),
    ];

    internal static readonly ModConfigurationKey<bool> AssetQuarantineKey = new("asset_quarantine",
        "When the renderer crashes, the assets it was loading become suspects. The next recovery attempt loads each suspect on its own after everything else and one that crashes the replacement renderer is quarantined.",
        () => true);

    internal static readonly ModConfigurationKey<bool> ExitWhenRecoveryFailsKey = new("exit_when_recovery_fails",
        "If recovery fails all of its attempts to recover, we can keep the main engine running without exiting.",
        () => true);

    internal static readonly ModConfigurationKey<bool> JournalTelemetryKey = new("journal_telemetry",
        "Collects journal telemetry of what the recovery journal holds. The report is rewritten every 10 seconds in Logs/RenderiteRecovery.JournalTelemetry.txt. This costs some CPU while it is enabled, so leave it off unless you want to check performance stats.",
        () => false);

    internal static readonly ModConfigurationKey[] Keys =
    [
        RecoveryAttemptsKey, StableSecondsKey, JournalLimitMbKey,
        JournalEntryLimitKey, ArchiveDiskLimitMbKey, ArchiveDirectoryKey, ArchiveMemoryMbKey,
        CompactionThresholdMbKey, ShowDashPanelKey, ShowForceExitButtonKey, WriteLogFilesKey, AssetQuarantineKey,
        ExitWhenRecoveryFailsKey, JournalTelemetryKey
    ];

    private const long Megabyte = 1024L * 1024;

    private static ModConfiguration? _config;
    private static FileSystemWatcher? _watcher;
    private static Timer? _reloadDebounce;

    internal static volatile int RecoveryAttempts = 10;
    internal static volatile int StableSeconds = 120;
    internal static volatile bool ShowDashPanel = true;
    internal static volatile bool ShowForceExitButton = true;
    internal static volatile bool WriteLogFiles = true;
    internal static volatile bool QuarantineAssets = true;
    internal static volatile bool ExitWhenRecoveryFails = true;

    internal static string FilePath { get; private set; } = "rml_config/RenderiteRecovery.json";

    internal static void Initialize(ModConfiguration? config, ResoniteModBase? owner)
    {
        _config = config;
        FilePath = Path.Combine(Directory.GetCurrentDirectory(), "rml_config", Path.ChangeExtension(Path.GetFileName(typeof(Settings).Assembly.Location), ".json"));
        if (config is null)
        {
            RenderiteRecoveryMod.Warn("No mod configuration is available, using default settings.");
            Apply();
            return;
        }
        config.OnThisConfigurationChanged += _ => Apply();
        Apply();
        bool restored = false;
        try { restored = RestoreFromRmlBackup(owner); }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not restore the settings file RML set aside: {ex.Message}"); }
        if (restored)
            ReloadFromFile();

        try { MigrateRenamedKeys(config); }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not carry over renamed settings: {ex.Message}"); }

        try
        {
            if (!File.Exists(FilePath))
            {
                if (owner is not null)
                    AllowSavingField?.SetValue(owner, true);

                config.Save(saveDefaultValues: true);
            }
            else if (FileLacksKeys())
                config.Save(saveDefaultValues: true);
        }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not write the default settings file: {ex.Message}"); }
        Watch();
        RenderiteRecoveryMod.Msg($"Settings ({FilePath}): {Describe()}.");
    }

    internal static string Describe() => $"recovery_attempts={RecoveryAttempts}, stable_seconds={StableSeconds}, journal_limit_mb={CommandJournal.ByteLimit / Megabyte}, journal_entry_limit={CommandJournal.EntryLimit}, archive_disk_limit_mb={PayloadArchiver.DiskLimit / Megabyte}, archive_directory=\"{Get(ArchiveDirectoryKey)}\" ({PayloadArchiver.Directory}), archive_memory_mb={PayloadArchiver.MemoryLimit / Megabyte}, compaction_threshold_mb={PayloadArchiver.CompactionThreshold / Megabyte}, show_dash_panel={ShowDashPanel}, show_force_exit_button={ShowForceExitButton}, write_log_files={WriteLogFiles}, asset_quarantine={QuarantineAssets}, exit_when_recovery_fails={ExitWhenRecoveryFails}, journal_telemetry={JournalTelemetry.Enabled}";

    private static T Get<T>(ModConfigurationKey<T> key)
    {
        ModConfiguration? config = _config;
        if (config is not null && config.TryGetValue(key, out T? value) && value is not null)
            return value;

        return key.TryComputeDefault(out object? fallback) && fallback is T typed ? typed : default!;
    }

    private static void Apply()
    {
        WriteLogFiles = Get(WriteLogFilesKey);
        RecoveryAttempts = Math.Max(1, Get(RecoveryAttemptsKey));
        StableSeconds = Math.Max(1, Get(StableSecondsKey));
        CommandJournal.ByteLimit = Math.Max(0, Get(JournalLimitMbKey)) * Megabyte;
        CommandJournal.EntryLimit = Math.Max(0, Get(JournalEntryLimitKey));
        PayloadArchiver.Configure(Math.Max(0, Get(ArchiveDiskLimitMbKey)) * Megabyte,
            Math.Max(1, Get(CompactionThresholdMbKey)) * Megabyte,
            Math.Max(0, Get(ArchiveMemoryMbKey)) * Megabyte,
            ResolveDirectory(Get(ArchiveDirectoryKey)));
        ShowDashPanel = Get(ShowDashPanelKey);
        bool showForceExit = Get(ShowForceExitButtonKey);
        if (showForceExit != ShowForceExitButton)
        {
            ShowForceExitButton = showForceExit;
            ExitScreenButton.Refresh();
        }
        QuarantineAssets = Get(AssetQuarantineKey);
        ExitWhenRecoveryFails = Get(ExitWhenRecoveryFailsKey);
        JournalTelemetry.SetEnabled(Get(JournalTelemetryKey));
    }

    internal static void SetJournalTelemetry(bool enabled)
    {
        ModConfiguration? config = _config;
        if (config is null)
        {
            JournalTelemetry.SetEnabled(enabled);
            return;
        }
        config.Set(JournalTelemetryKey, enabled, "dash button");
        try { config.Save(saveDefaultValues: true); }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not save journal_telemetry to the settings file: {ex.Message}"); }
    }

    private static void MigrateRenamedKeys(ModConfiguration config)
    {
        if (!File.Exists(FilePath))
            return;

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FilePath), JsonOptions);
        if (!document.RootElement.TryGetProperty("values", out JsonElement values)
            || values.ValueKind != JsonValueKind.Object)
            return;

        bool changed = false;
        foreach ((string oldName, ModConfigurationKey key) in RenamedKeys)
        {
            if (values.TryGetProperty(key.Name, out _) || !values.TryGetProperty(oldName, out JsonElement old))
                continue;

            if (Convert(old, key.ValueType()) is not { } value || !key.Validate(value))
            {
                RenderiteRecoveryMod.Warn($"Settings file: {oldName} is now {key.Name}, but its value {old} is not valid there, so the default is used.");
                continue;
            }
            config.Set(key, value, "renamed from " + oldName);
            RenderiteRecoveryMod.Msg($"Settings file: {oldName} is now {key.Name} (value {value} kept).");
            changed = true;
        }
        if (changed)
            Apply();
    }

    private static bool FileLacksKeys()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FilePath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
            });
            if (!document.RootElement.TryGetProperty("values", out JsonElement values)
                || values.ValueKind != JsonValueKind.Object)
                return false;

            return Keys.Any(key => !values.TryGetProperty(key.Name, out _));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ResolveDirectory(string? value)
    {
        string text = (value ?? "").Trim().Trim('"');
        if (text.Length == 0)
            return null;

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(text), Directory.GetCurrentDirectory());
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Warn($"archive_directory \"{text}\" is not a valid path ({ex.Message}), using the temp folder.");
            return null;
        }
    }

    private static void Watch()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (directory is null)
                return;

            Directory.CreateDirectory(directory);
            _reloadDebounce = new Timer(_ => ReloadFromFile());
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            FileSystemEventHandler changed = (_, _) => _reloadDebounce.Change(500, Timeout.Infinite);
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => _reloadDebounce.Change(500, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { RenderiteRecoveryMod.Warn($"Settings file changes will apply after a restart only: {ex.Message}"); }
    }

    private static void ReloadFromFile()
    {
        ModConfiguration? config = _config;
        if (config is null)
            return;

        try
        {
            string text;
            try { text = File.ReadAllText(FilePath); }
            catch (IOException)
            {
                _reloadDebounce?.Change(500, Timeout.Infinite);
                return;
            }
            if (RepairDirectoryValue(text) is string repaired && IsValidJson(repaired))
            {
                text = repaired;
                try
                {
                    File.WriteAllText(FilePath, repaired);
                    RenderiteRecoveryMod.Msg("Settings file: archive_directory had single backslashes, which JSON does not allow, so it was rewritten with doubled ones.");
                }
                catch (Exception ex) { RenderiteRecoveryMod.Warn($"Could not write the corrected settings file: {ex.Message}"); }
            }
            using JsonDocument document = JsonDocument.Parse(text, JsonOptions);
            if (!document.RootElement.TryGetProperty("values", out JsonElement values)
                || values.ValueKind != JsonValueKind.Object)
                return;

            var changes = new List<string>();
            foreach (ModConfigurationKey key in Keys)
            {
                if (!values.TryGetProperty(key.Name, out JsonElement element))
                    continue;

                object? value = Convert(element, key.ValueType());
                if (value is null)
                {
                    string expected = key.ValueType() == typeof(bool) ? "true or false" : key.ValueType() == typeof(string) ? "a quoted folder path" : "a whole number";
                    RenderiteRecoveryMod.Warn($"Settings file: ignoring {key.Name} (expected {expected}).");
                    continue;
                }
                if (Equals(config.GetValue(key), value))
                    continue;

                if (!key.Validate(value))
                {
                    RenderiteRecoveryMod.Warn($"Settings file: ignoring {key.Name} = {element} (out of range, see its description).");
                    continue;
                }
                config.Set(key, value, "settings file");
                changes.Add(value is string folder ? $"{key.Name}=\"{folder}\"" : $"{key.Name}={value}");
            }
            if (changes.Count > 0)
                RenderiteRecoveryMod.Msg("Settings file changed. Applied " + string.Join(", ", changes) + ".");
        }
        catch (JsonException ex)
        {
            RenderiteRecoveryMod.Warn($"Settings file is not valid JSON, keeping the current settings: {ex.Message} (A backslash inside quotes must be doubled, e.g. \"D:\\\\RRCache\", or use / instead.)");
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Warn($"Could not reload the settings file: {ex.Message}");
        }
    }

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex DirectoryValue = new("(\"archive_directory\"\\s*:\\s*)\"(?<raw>[^\\r\\n]*?)\"(?=\\s*(?:,|\\}|\\r?\\n|$))");

    internal static string? RepairDirectoryValue(string text)
    {
        Match match = DirectoryValue.Match(text);
        if (!match.Success)
            return null;

        string raw = match.Groups["raw"].Value;
        string value = raw;
        try
        {
            string? decoded = JsonSerializer.Deserialize<string>("\"" + raw + "\"");
            if (decoded is not null && !decoded.Any(char.IsControl))
                value = decoded;
        }
        catch (JsonException) { }
        string escaped = JsonSerializer.Serialize(value, RelaxedJson);
        if (escaped == "\"" + raw + "\"")
            return null;

        return string.Concat(text.AsSpan(0, match.Index), match.Groups[1].Value, escaped,
            text.AsSpan(match.Index + match.Length));
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, JsonOptions);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static readonly FieldInfo? AllowSavingField = AccessTools.Field(typeof(ResoniteModBase), "AllowSavingConfiguration");

    private static bool RestoreFromRmlBackup(ResoniteModBase? owner)
    {
        if (File.Exists(FilePath) || owner is null)
            return false;

        if (AllowSavingField?.GetValue(owner) is not false)
            return false;

        string? directory = Path.GetDirectoryName(FilePath);
        if (directory is null || !Directory.Exists(directory))
            return false;

        string prefix = Path.GetFileName(FilePath) + ".";
        FileInfo? backup = new DirectoryInfo(directory).GetFiles(prefix + "*.bak")
            .Select(file => (File: file, Age: BackupAgeSeconds(file.Name, prefix)))
            .Where(candidate => candidate.Age is <= 600)
            .OrderBy(candidate => candidate.Age).Select(candidate => candidate.File).FirstOrDefault();
        if (backup is null)
            return false;

        string text = File.ReadAllText(backup.FullName);
        if (IsValidJson(text) || RepairDirectoryValue(text) is not string repaired || !IsValidJson(repaired))
            return false;

        File.WriteAllText(FilePath, repaired);
        backup.MoveTo(backup.FullName + ".restored", overwrite: true);
        AllowSavingField?.SetValue(owner, true);
        RenderiteRecoveryMod.Warn($"ResoniteModLoader could not read {Path.GetFileName(FilePath)} (archive_directory had single backslashes) and set it aside as {backup.Name}. Restored it with the path corrected.");
        return true;
    }

    private static int? BackupAgeSeconds(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".bak", StringComparison.Ordinal))
            return null;

        string stamp = name.Substring(prefix.Length, name.Length - prefix.Length - ".bak".Length);
        try
        {
            string hex = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(stamp));
            int seconds = int.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            const int Day = 24 * 60 * 60;
            int now = (int)DateTimeOffset.Now.TimeOfDay.TotalSeconds;
            return ((now - seconds) % Day + Day) % Day;
        }
        catch (Exception) { return null; }
    }

    private static object? Convert(JsonElement element, Type type)
    {
        if (type == typeof(bool))
            return element.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };

        if (type == typeof(string))
            return element.ValueKind switch { JsonValueKind.String => element.GetString(), JsonValueKind.Null => "", _ => null };

        if (element.ValueKind != JsonValueKind.Number)
            return null;

        if (type == typeof(int))
            return element.TryGetInt32(out int number) ? number : null;

        if (type == typeof(long))
            return element.TryGetInt64(out long number) ? number : null;

        return null;
    }
}
