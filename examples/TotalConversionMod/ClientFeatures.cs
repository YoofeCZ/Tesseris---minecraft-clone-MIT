using Tesseris.ModApi;

namespace TotalConversionMod;

internal sealed class AltarScreenFactory(IModClientUiRegistry ui, IModAudioRegistry audio)
    : IModScreenFactory
{
    public IModScreenV2 Create() => new AltarScreen(ui, audio);
}

internal sealed class AltarScreen(IModClientUiRegistry ui, IModAudioRegistry audio) : IModScreenV2
{
    public void OnOpened(ModScreenOpenRequest request)
    {
    }

    public void OnClosed()
    {
    }

    public void Draw(IModUiCanvas canvas)
    {
        int width = 360;
        int height = 180;
        int x = (canvas.ScreenWidth - width) / 2;
        int y = (canvas.ScreenHeight - height) / 2;
        canvas.DrawRect(x, y, width, height, new ModColor(12, 20, 38, 238));
        canvas.DrawRect(x + 10, y + 10, width - 20, 2, new ModColor(92, 216, 255));
        canvas.DrawText("SKY ALTAR", x + 22, y + 28, scale: 2);
        canvas.DrawText("A generic modal screen owned by the mod", x + 22, y + 76);
        canvas.DrawText("Press Escape or click to close", x + 22, y + 110);
    }

    public ModActionResult HandleInput(ModUiInput input)
    {
        bool escape = input.Kind == ModUiInputKind.Key &&
                      input.Phase == ModInputPhase.Pressed &&
                      input.KeyCode == 256;
        bool clicked = input.Kind == ModUiInputKind.PointerButton &&
                       input.Phase == ModInputPhase.Pressed &&
                       input.PointerButton == ModPointerButton.Primary;
        if (!escape && !clicked)
        {
            return ModActionResult.Pass;
        }

        ui.Close();
        audio.Play(new ModSoundPlayRequest(Ids.ChimeSound, Pitch: 0.8f));
        return ModActionResult.Handled;
    }
}

internal sealed class StatusHud : IModHudLayer
{
    public void Draw(IModUiCanvas canvas)
    {
        canvas.DrawRect(8, 8, 250, 28, new ModColor(11, 21, 40, 215));
        canvas.DrawText("TOTAL CONVERSION / SKYLANDS", 16, 16);
    }
}

internal sealed class LegacyStatusOverlay : IModOverlay
{
    public void Draw(IModUiCanvas canvas)
    {
        canvas.DrawText("Mod API v2", 16, 42);
    }
}

internal sealed class OpenAltarInputHandler(IModClientUiRegistry ui, IModAudioRegistry audio)
    : IModInputHandler
{
    public ModActionResult Handle(ModInputActionEvent input)
    {
        if (input.Phase != ModInputPhase.Pressed)
        {
            return ModActionResult.Pass;
        }

        CompassUseBehavior.OpenAltar(ui, audio);
        return ModActionResult.Handled;
    }
}

internal sealed class OpenAltarCommand(IModClientUiRegistry ui, IModAudioRegistry audio)
    : IModCommandHandler
{
    public ModActionResult Execute(ModCommandInvocation invocation, IModCommandOutput output)
    {
        CompassUseBehavior.OpenAltar(ui, audio);
        output.Reply("Sky Altar opened.");
        return ModActionResult.Handled;
    }
}

internal sealed class SkyMarkerRender : IModRenderCallback
{
    public void Record(ModRenderViewSnapshot view, IModRenderCommandBuffer commands)
    {
        ModVector3 camera = view.CameraPosition;
        commands.DrawLine(
            new ModVector3(camera.X - 0.35f, camera.Y - 0.25f, camera.Z - 1.5f),
            new ModVector3(camera.X + 0.35f, camera.Y - 0.25f, camera.Z - 1.5f),
            new ModColor(92, 216, 255, 190),
            width: 0.025f);
        commands.DrawBillboard(
            Ids.SkyStoneTexture,
            new ModVector3(camera.X, camera.Y + 0.2f, camera.Z - 2f),
            width: 0.28f,
            height: 0.28f,
            tint: new ModColor(150, 235, 255, 190));
    }
}
