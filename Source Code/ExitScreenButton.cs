using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

namespace RenderiteRecovery;

[HarmonyPatch(typeof(ExitScreen), "OnStart")]
internal static class ExitScreenButton
{
    private const string SlotName = "RenderiteRecovery.ForceExit";
    private const string Label = "Force Exit Resonite (Renderite Recovery)";

    private static ExitScreen? _screen;

    private static void Postfix(ExitScreen __instance)
    {
        if (__instance.World != Userspace.UserspaceWorld)
            return;

        _screen = __instance;
        Sync(__instance);
    }

    internal static void Refresh()
    {
        ExitScreen? screen = _screen;
        World? world = screen?.World;
        if (screen is null || world is null || world.IsDestroyed)
            return;

        world.RunSynchronously(() => Sync(screen));
    }

    private static void Sync(ExitScreen screen)
    {
        try
        {
            if (screen.IsDestroyed)
                return;

            Canvas? canvas = AccessTools.Property(typeof(RadiantDashScreen), "ScreenCanvas").GetValue(screen) as Canvas;
            GridLayout? grid = canvas?.Slot.GetComponentInChildren<GridLayout>();
            if (grid is null)
                return;

            Slot? existing = grid.Slot.Children.FirstOrDefault(child => child.Name == SlotName);
            if (!Settings.ShowForceExitButton)
            {
                existing?.Destroy();
                return;
            }
            if (existing is not null)
                return;

            var ui = new UIBuilder(grid.Slot);
            RadiantUI_Constants.SetupDefaultStyle(ui, false);
            Button button = AddButton(ui, OfficialAssets.Graphics.Icons.Dash.CloseWorld, Label);
            button.Slot.Name = SlotName;
            button.Slot.PersistentSelf = false;
            Engine engine = screen.Engine;
            button.LocalPressed += (_, _) => ForceExit.Run(engine);
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Warn($"Could not update the force exit button: {ex}");
        }
    }

    private static Button AddButton(UIBuilder ui, Uri icon, string label)
    {
        Image image = ui.Panel(ui.Style.ButtonColor, false);
        image.Sprite.Target = ui.Style.ButtonSprite;
        image.NineSliceSizing.Value = ui.Style.NineSliceSizing;
        Button button = ui.Root.AttachComponent<Button>();
        ui.Panel().AddFixedPadding(8f);
        ui.HorizontalFooter(64f, out RectTransform footer, out RectTransform content);
        ui.NestInto(footer);
        ui.Text(label);
        ui.NestOut();
        ui.NestInto(content);
        ui.Image(icon);
        ui.NestOut();
        ui.NestOut();
        ui.NestOut();
        return button;
    }
}
