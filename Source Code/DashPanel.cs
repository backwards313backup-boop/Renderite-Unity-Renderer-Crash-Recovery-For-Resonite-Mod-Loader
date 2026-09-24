using System.Diagnostics;
using System.Globalization;
using System.Text;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

namespace RenderiteRecovery;

internal static class DashPanel
{
    private const string SlotName = "RenderiteRecovery.Panel";
    private const string ButtonLabel = "Recovery";
    private const long OrderOffset = int.MaxValue - 1L;
    private const int MaximumFailures = 3;
    private static readonly System.Reflection.FieldInfo? ScreenButton = AccessTools.Field(typeof(RadiantDashScreen), "_button");

    private static RadiantDashScreen? _screen;
    private static Text? _status;
    private static Text? _left;
    private static Text? _right;
    private static Text? _footer;
    private static long _nextUpdate;
    private static int _queued;
    private static int _failures;
    private static bool _buttonMarked;

    internal static void Poll()
    {
        long now = Environment.TickCount64;
        if (now < _nextUpdate || _failures >= MaximumFailures)
            return;

        _nextUpdate = now + 1000;
        World? world = Userspace.UserspaceWorld;
        if (world is null || world.IsDestroyed || Interlocked.Exchange(ref _queued, 1) != 0)
            return;

        world.RunSynchronously(() =>
        {
            using var measure = ResourceMetrics.Measure(ResourceMetrics.Area.Panel);
            try
            {
                Update(world);
            }
            catch (Exception ex)
            {
                if (++_failures >= MaximumFailures)
                    RenderiteRecoveryMod.Warn($"The dash panel is disabled for this session after repeated errors: {ex}");
                else
                    RenderiteRecoveryMod.Warn($"Could not update the dash panel: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _queued, 0);
            }
        });
    }

    private static void Update(World world)
    {
        if (!Settings.ShowDashPanel)
        {
            if (_screen is not null && !_screen.IsDestroyed)
                _screen.CloseContainer();

            _screen = null;
            return;
        }
        UserspaceRadiantDash? userspaceDash = world.GetRadiantDash();
        RadiantDash? dash = userspaceDash?.Dash;
        if (dash is null)
            return;

        bool built = false;
        if (_screen is null || _screen.IsDestroyed)
        {
            Build(dash);
            built = true;
        }
        MarkButtonNonPersistent();
        if (built || (userspaceDash!.Open && _screen!.IsShown))
            Refresh();
    }

    private static void MarkButtonNonPersistent()
    {
        if (_buttonMarked || _screen is null)
            return;

        if ((ScreenButton?.GetValue(_screen) as SyncRef<RadiantDashButton>)?.Target is not RadiantDashButton button)
            return;

        button.Slot.PersistentSelf = false;
        _buttonMarked = true;
    }

    private static void Build(RadiantDash dash)
    {
        foreach (Slot stale in dash.ScreensContainer.Children.Where(child => child.Name == SlotName).ToList())
            stale.Destroy();

        RadiantDashScreen screen = dash.AttachScreen<RadiantDashScreen>(ButtonLabel,
            RadiantUI_Constants.Hero.CYAN, OfficialAssets.Graphics.Icons.General.Loop);
        screen.Slot.Name = SlotName;
        screen.Slot.PersistentSelf = false;
        screen.Slot.OrderOffset = OrderOffset;

        var ui = new UIBuilder(screen.ScreenCanvas);
        RadiantUI_Constants.SetupDefaultStyle(ui, false);
        ui.Image(UserspaceRadiantDash.DEFAULT_BACKGROUND, false);
        ui.Nest();
        ui.Panel().AddFixedPadding(64f, 48f, 64f, 48f);
        ui.VerticalLayout(16f, 0f, Alignment.TopLeft, true, false);

        ui.Style.MinHeight = 72f;
        Text title = ui.Text("<b>Renderite Recovery</b>", 56f, false, Alignment.MiddleLeft, true);
        title.Color.Value = RadiantUI_Constants.HEADING_COLOR;
        ui.Style.MinHeight = 48f;
        _status = ui.Text("", 32f, false, Alignment.MiddleLeft, true);
        ui.Style.MinHeight = 2f;
        ui.Image(RadiantUI_Constants.Neutrals.MIDLIGHT, false);

        ui.Style.MinHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        ui.HorizontalLayout(48f, 0f, Alignment.TopLeft);
        ui.Style.FlexibleWidth = 1f;
        _left = ui.Text("", 24f, false, Alignment.TopLeft, true);
        _right = ui.Text("", 24f, false, Alignment.TopLeft, true);
        ui.NestOut();
        ui.NestOut();
        BuildTopRight(ui);

        _screen = screen;
        _buttonMarked = false;
        RenderiteRecoveryMod.Msg("Added the Recovery screen to the dash.");
    }

    private const float TitleBandHeight = 72f + 16f + 48f;

    private static void BuildTopRight(UIBuilder ui)
    {
        ui.PushStyle();
        Slot corner = ui.Next("Info");
        RectTransform rect = corner.GetComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.55f, 1f);
        rect.AnchorMax.Value = new float2(1f, 1f);
        rect.OffsetMin.Value = new float2(0f, -TitleBandHeight);
        rect.OffsetMax.Value = float2.Zero;
        ui.NestInto(corner);
        ui.VerticalLayout(8f, 0f, Alignment.TopRight, false, false);

        ui.Style.MinHeight = 48f;
        BuildCredit(ui);

        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = 64f;
        ui.Style.PreferredHeight = -1f;
        _footer = ui.Text("", 22f, false, Alignment.TopRight, true);
        _footer.Color.Value = RadiantUI_Constants.Neutrals.MIDLIGHT;
        _footer.HorizontalAutoSize.Value = true;
        _footer.VerticalAutoSize.Value = true;
        _footer.AutoSizeMin.Value = 12f;
        _footer.AutoSizeMax.Value = 22f;

        ui.NestOut();
        ui.NestOut();
        ui.PopStyle();
    }

    private static void BuildCredit(UIBuilder ui)
    {
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = -1f;
        Slot credit = ui.Next("Creator");
        Image background = credit.AttachComponent<Image>();
        background.Tint.Value = colorX.Clear;
        Button button = credit.AttachComponent<Button>();
        InteractionElement.ColorDriver hover = button.ColorDrivers.Count > 0 ? button.ColorDrivers[0] : button.ColorDrivers.Add();
        if (!hover.ColorDrive.IsLinkValid)
            hover.ColorDrive.Target = background.Tint;

        hover.TintColorMode.Value = InteractionElement.ColorMode.Direct;
        hover.NormalColor.Value = colorX.Clear;
        hover.HighlightColor.Value = new colorX(1f, 1f, 1f, 0.15f);
        hover.PressColor.Value = new colorX(1f, 1f, 1f, 0.3f);
        hover.DisabledColor.Value = colorX.Clear;
        _creditButton = button;
        _creditHoverLogs = 0;
        button.IsHovering.OnValueChange += _ => button.World.RunInUpdates(3, () => LogCredit("hover"));
        credit.AttachComponent<ContactLink>().UserId.Value = CreatorUserId;
        _creatorInfo = credit.AttachComponent<CloudUserInfo>();
        _creatorInfo.UserId.Value = CreatorUserId;
        _creatorIcon = credit.AttachComponent<StaticTexture2D>();
        _creatorIcon.URL.Value = CreatorIconFallback;

        Row(credit, 12f, 6f);
        ui.NestInto(credit);
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = -1f;
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinHeight = CreditTextHeight;
        ui.Style.PreferredHeight = CreditTextHeight;
        Slot name = ui.Next("Name");
        Row(name, 6f, 0f);
        ui.Nest();
        ui.Style.MinWidth = CreditWordWidth;
        ui.Style.PreferredWidth = CreditWordWidth;
        CreditWord("<nobr>Created by</nobr>", Alignment.MiddleRight);
        CreditWord($"<color={CreatorNameColor}>backwards</color>", Alignment.MiddleLeft);
        ui.NestOut();

        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 48f;
        ui.Style.PreferredWidth = 48f;
        ui.Style.MinHeight = 48f;
        ui.Style.PreferredHeight = 48f;
        Slot picture = ui.Next("Picture");
        picture.AttachComponent<Image>().Sprite.Target = ui.CircleSprite;
        picture.AttachComponent<Mask>().ShowMaskGraphic.Value = false;
        ui.Nest();
        ui.RawImage(_creatorIcon, colorX.White, true);
        ui.NestOut();

        ui.NestOut();

        static void Row(Slot slot, float spacing, float padding)
        {
            HorizontalLayout row = slot.AttachComponent<HorizontalLayout>();
            row.Spacing.Value = spacing;
            row.PaddingTop.Value = padding;
            row.PaddingRight.Value = padding;
            row.PaddingBottom.Value = padding;
            row.PaddingLeft.Value = padding;
            row.ChildAlignment = Alignment.MiddleRight;
            row.ForceExpandWidth.Value = false;
            row.ForceExpandHeight.Value = false;
        }

        void CreditWord(string content, Alignment alignment)
        {
            Text word = ui.Text(content, 22f, true, alignment, true);
            word.AutoSizeMin.Value = 12f;
            word.AutoSizeMax.Value = 22f;
            word.Color.Value = colorX.White;
        }
    }

    private const float CreditWordWidth = 106f;
    private const float CreditTextHeight = 26f;
    private const string CreatorUserId = "U-backwards";
    private const string CreatorNameColor = "#FFD700";
    private static readonly Uri CreatorIconFallback = new("resdb:///2bfe4c3df1589cc656ae78880f44bbb5dc260ded4a4c5e2fa75bce64a863b088.webp");
    private static Button? _creditButton;
    private static int _creditHoverLogs;

    private static void LogCredit(string reason)
    {
        if (_creditButton is not { IsDestroyed: false } button || _creditHoverLogs >= 6)
            return;

        _creditHoverLogs++;
        string rects = string.Join(", ", new[] { button.Slot }.Concat(button.Slot.Children).Concat(button.Slot.Children.SelectMany(child => child.Children))
            .Select(slot => slot.GetComponent<RectTransform>() is RectTransform rect ? $"{slot.Name} {rect.LocalComputeRect.width:F0}x{rect.LocalComputeRect.height:F0}" : null)
            .Where(entry => entry is not null));
        string drivers = string.Join(", ", button.ColorDrivers.Select(driver => $"{driver.TintColorMode.Value} valid={driver.ColorDrive.IsLinkValid} highlight={driver.HighlightColor.Value}"));
        colorX tint = button.Slot.GetComponent<Image>()?.Tint.Value ?? colorX.Clear;
        RenderiteRecoveryMod.Msg($"Credit {reason}: hovering={button.IsHovering.Value}, pressed={button.IsPressed.Value}, tint={tint}, drivers [{drivers}], rects [{rects}]");
    }

    private static CloudUserInfo? _creatorInfo;
    private static StaticTexture2D? _creatorIcon;

    private const long WorldSizeInterval = 5000;
    private static WorldSize.Report? _worldSize;
    private static World? _worldSizeWorld;
    private static long _nextWorldSize;

    private static void MeasureWorldSize()
    {
        Engine? engine = Engine.Current;
        World? focused = engine?.WorldManager?.FocusedWorld;
        long now = Environment.TickCount64;
        if (focused != _worldSizeWorld)
            _nextWorldSize = 0;

        if (now < _nextWorldSize)
            return;

        _nextWorldSize = now + WorldSizeInterval;
        _worldSizeWorld = focused;
        _worldSize = engine?.RenderSystem is RenderSystem system && focused is not null
            ? WorldSize.Measure(system, focused) : null;
    }

    private static void Refresh()
    {
        MeasureWorldSize();
        if (_creatorInfo?.IconURL.Value is Uri icon && _creatorIcon is not null && _creatorIcon.URL.Value != icon)
            _creatorIcon.URL.Value = icon;

        RecoveryCoordinator.StatusReport status = RecoveryCoordinator.GetStatus();
        ResourceMetrics.Report? report = ResourceMetrics.GetReport();

        if (_status is not null)
        {
            string color = status.Healthy ? Hex(RadiantUI_Constants.Hero.GREEN_HEX) : status.State.StartsWith("Recovering") || status.State.StartsWith("Resuming") ? Hex(RadiantUI_Constants.Hero.YELLOW_HEX) : Hex(RadiantUI_Constants.Hero.RED_HEX);
            _status.Content.Value = $"<color={color}>{status.State}</color>";
        }
        if (_left is not null)
            _left.Content.Value = LeftColumn(status, report);

        if (_right is not null)
            _right.Content.Value = RightColumn(status, report);

        if (_footer is not null)
            _footer.Content.Value = $"Settings: {Settings.FilePath}. Version {RenderiteRecoveryMod.ModVersion}.";
    }

    private static string LeftColumn(RecoveryCoordinator.StatusReport status, ResourceMetrics.Report? report)
    {
        var text = new StringBuilder();
        Heading(text, "Recovery");
        text.AppendLine($"Recoveries this session: {status.Recoveries}");
        text.AppendLine($"Attempts used: {status.AttemptsUsed} of {Settings.RecoveryAttempts} (all back after {RenderiteRecoveryMod.Seconds(Settings.StableSeconds)} without a failure)");
        if (status.LastRecoveryLocal is DateTime last)
        {
            text.AppendLine($"Last: {last.ToString("T", CultureInfo.CurrentCulture)}, took {status.LastRecoveryMs / 1000.0:F1} seconds, {status.LastRecoveryCommands:N0} commands");
        }
        else
            text.AppendLine("Last: none yet");

        if (status.QuarantinedAssets.Count > 0)
        {
            text.AppendLine($"Quarantined assets: {status.QuarantinedAssets.Count} ({AssetQuarantine.Describe(status.QuarantinedAssets, 4)})");
            text.AppendLine($"  engine commands held back: {status.BlockedCommands:N0}");
        }
        else
            text.AppendLine(Settings.QuarantineAssets ? "Quarantined assets: none" : "Quarantined assets: off (asset_quarantine)");

        if (status.SuspectAssets > 0)
            text.AppendLine($"Suspects to test: {status.SuspectAssets}");

        text.AppendLine();

        Heading(text, "GPU");
        if (status.ReplayTotal > 0 && status.State.StartsWith("Recovering"))
            text.AppendLine($"  now: {Bytes(status.ReplayedBytes)} uploaded ({status.ReplayedCommands:N0} of {status.ReplayTotal:N0} commands)");

        text.AppendLine(status.LastRecoveryLocal is null ? "last recovery: none yet" : $"last recovery: {Bytes(status.LastRecoveryBytes)} of mesh, texture and buffer data");
        text.AppendLine($"next recovery would upload about {Bytes(status.ArchiveLiveBytes)}");
        text.AppendLine();

        Heading(text, "Resonite");
        try
        {
            using var process = Process.GetCurrentProcess();
            text.AppendLine($"Working set: {Bytes(process.WorkingSet64)}");
        }
        catch (Exception) { }
        text.AppendLine($"Managed heap: {Bytes(GC.GetTotalMemory(false))}");
        if (report is not null)
            text.AppendLine($"Engine: {report.FramesPerSecond:F0} frames per second");

        text.AppendLine();

        GarbageCollector(text, status);
        return text.ToString().TrimEnd();
    }

    private static void GarbageCollector(StringBuilder text, RecoveryCoordinator.StatusReport status)
    {
        Heading(text, "Garbage collector (all of Resonite, since load)");
        if (GarbageCollection.GetReport() is not GarbageCollection.Report gc)
        {
            text.AppendLine("Measuring...");
            return;
        }
        int gen2 = gc.Collections[2], gen1 = gc.Collections[1] - gen2, gen0 = gc.Collections[0] - gc.Collections[1];
        text.AppendLine($"Collections: {gen0:N0} gen 0, {gen1:N0} gen 1, {gen2:N0} gen 2 ({gc.CollectionsPerMinute:F0}/min now)");
        text.AppendLine($"Reclaimed: {Bytes(gc.ReclaimedBytes)} of {Bytes(gc.AllocatedBytes)} allocated, {Bytes((long)gc.ReclaimedBytesPerSecond)} per second now");
        string paused = gc.Pause.TotalSeconds < 10 ? $"{gc.Pause.TotalMilliseconds:F0} ms" : $"{gc.Pause.TotalSeconds:F1} seconds";
        text.AppendLine($"Paused: {paused} in total ({Percent(gc.PausePercent)} of the time)");
        if (gc.Last is GarbageCollection.Collection last)
        {
            text.AppendLine($"Last: #{last.Number:N0}, {last.Kind}{(last.Compacted ? ", compacting" : "")}, {last.PauseMs:F1} ms pause");
            text.AppendLine($"  {Bytes(last.HeapBeforeBytes)} -> {Bytes(last.HeapAfterBytes)}: {Bytes(Math.Max(0, last.HeapBeforeBytes - last.HeapAfterBytes))} freed, {Bytes(last.PromotedBytes)} promoted");
            long[] g = last.GenerationAfterBytes;
            if (g.Length >= 5)
                text.AppendLine($"  heap after: gen 0 {Bytes(g[0])}, gen 1 {Bytes(g[1])}, gen 2 {Bytes(g[2])}, large {Bytes(g[3])}, pinned {Bytes(g[4])}, fragmented {Bytes(last.FragmentedBytes)}");
        }
        long modAllocated = ResourceMetrics.TotalAllocatedBytes;
        long modHeld = status.JournalMemoryBytes + status.ArchiveMemoryBytes + status.DeferredBytes;
        double share = gc.AllocatedBytes > 0 ? modAllocated * 100.0 / gc.AllocatedBytes : 0;
        text.AppendLine($"This mod: allocated {Bytes(modAllocated)} ({Percent(share)} of Resonite's), about {Bytes(Math.Max(0, modAllocated - modHeld))} became garbage, {Bytes(modHeld)} still held");
    }

    private static string RightColumn(RecoveryCoordinator.StatusReport status, ResourceMetrics.Report? report)
    {
        var text = new StringBuilder();
        CurrentWorld(text);
        Heading(text, "CPU");
        if (report is null)
            text.AppendLine("Measuring...");
        else
        {
            text.AppendLine($"{Percent(report.CpuPercentOfCore)} of one core over the last {report.WindowSeconds:F0} seconds");
            text.AppendLine($"{report.HookMsPerFrame:F3} ms of hook time per engine frame");
            text.AppendLine($"{Percent(report.ShareOfProcessCpuPercent)} of Resonite's CPU time");
            foreach (ResourceMetrics.AreaUsage area in report.Areas)
                text.AppendLine($"  {AreaName(area.Area)}: {Percent(area.CpuPercentOfCore)}, total {area.TotalCpuSeconds:F1} seconds");

            text.AppendLine($"Total since start: {report.TotalCpuSeconds:F1} seconds");
        }
        text.AppendLine();

        Heading(text, "Memory");
        text.AppendLine($"Journal (RAM): {Bytes(status.JournalMemoryBytes)} in {status.JournalEntries:N0} entries");
        if (report is not null)
            text.AppendLine($"Allocations: {Bytes((long)report.AllocatedBytesPerSecond)} per second (reclaimed by the Garbage Collector)");

        if (status.PersistentLeaseBytes > 0)
            text.AppendLine($"Shared memory held for point/trail buffers: {Bytes(status.PersistentLeaseBytes)}");

        if (status.DeferredCommands > 0)
            text.AppendLine($"Queued during recovery: {status.DeferredCommands:N0} commands, {Bytes(status.DeferredBytes)}");

        long waitingForDisk = PayloadArchiver.PendingBytes;
        if (PayloadArchiver.MemoryLimit > 0)
            text.AppendLine($"Payload archive (RAM): {Bytes(Math.Max(0, status.ArchiveMemoryBytes - waitingForDisk))} of {Bytes(PayloadArchiver.MemoryLimit)}");

        if (waitingForDisk > 0)
            text.AppendLine($"Payloads waiting for the disk: {Bytes(waitingForDisk)}");

        string journalLimit = CommandJournal.ByteLimit > 0 ? Bytes(CommandJournal.ByteLimit) : "none";
        text.AppendLine($"Journal limit: {journalLimit}");
        text.AppendLine();

        Heading(text, "Disk");
        text.AppendLine($"Folder: {PayloadArchiver.Directory}");
        text.AppendLine($"Payload archive: {Bytes(Math.Max(0, status.ArchiveLiveBytes - status.ArchiveMemoryBytes))} live, {Bytes(status.ArchiveDiskBytes)} on disk, {status.ArchiveSegments} segment{(status.ArchiveSegments == 1 ? "" : "s")}");
        string diskLimit = PayloadArchiver.DiskLimit > 0 ? Bytes(PayloadArchiver.DiskLimit) : "none";
        text.AppendLine($"Disk limit: {diskLimit}, compaction at {Bytes(PayloadArchiver.CompactionThreshold)} dead");
        return text.ToString().TrimEnd();
    }

    private static void CurrentWorld(StringBuilder text)
    {
        Heading(text, "Current world");
        if (_worldSize is not WorldSize.Report size)
        {
            text.AppendLine("No world is focused.");
            text.AppendLine();
            return;
        }
        text.AppendLine($"{size.WorldName}: {Bytes(size.TotalBytes)}");
        text.AppendLine($"  Textures: {Bytes(size.Textures.Bytes)} ({size.Textures.Count:N0})");
        if (size.TexturesOnly)
            text.AppendLine("  Meshes and other buffers: not counted while the journal is off");
        else
        {
            text.AppendLine($"  Meshes: {Bytes(size.Meshes.Bytes)} ({size.Meshes.Count:N0})");
            text.AppendLine($"  Other GPU buffers: {Bytes(size.Other.Bytes)} ({size.Other.Count:N0})");
        }
        text.AppendLine();
    }

    private static void Heading(StringBuilder text, string title) => text.AppendLine($"<b><color={Hex(RadiantUI_Constants.Hero.CYAN_HEX)}>{title.ToUpperInvariant()}</color></b>");

    private static string AreaName(ResourceMetrics.Area area) => area switch
    {
        ResourceMetrics.Area.Recording => "Journaling",
        ResourceMetrics.Area.EngineHooks => "Engine hooks",
        ResourceMetrics.Area.Background => "Monitor + compaction",
        ResourceMetrics.Area.Recovery => "Recovery",
        ResourceMetrics.Area.Panel => "This panel",
        _ => area.ToString()
    };

    private static string Hex(string hex) => hex.StartsWith('#') ? hex : "#" + hex;

    private static string Percent(double value) => value switch
    {
        < 0.01 => "0%",
        < 1 => value.ToString("F2", CultureInfo.InvariantCulture) + "%",
        _ => value.ToString("F1", CultureInfo.InvariantCulture) + "%"
    };

    internal static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("F0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " B"
    };
}
