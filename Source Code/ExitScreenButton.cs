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

    private static void Postfix(ExitScreen __instance)
    {
        try
        {
            if (__instance.World != Userspace.UserspaceWorld)
                return;

            Canvas? canvas = AccessTools.Property(typeof(RadiantDashScreen), "ScreenCanvas").GetValue(__instance) as Canvas;
            GridLayout? grid = canvas?.Slot.GetComponentInChildren<GridLayout>();
            if (grid is null || grid.Slot.Children.Any(child => child.Name == SlotName))
                return;

            var ui = new UIBuilder(grid.Slot);
            RadiantUI_Constants.SetupDefaultStyle(ui, false);
            Button button = AddButton(ui, OfficialAssets.Graphics.Icons.Dash.CloseWorld, Label);
            button.Slot.Name = SlotName;
            button.Slot.PersistentSelf = false;
            Engine engine = __instance.Engine;
            button.LocalPressed += (_, _) => ForceExit.Run(engine);
        }
        catch (Exception ex)
        {
            RenderiteRecoveryMod.Warn($"Could not add the force exit button: {ex}");
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
