namespace Tesseris.ModApi;

public sealed record ModScreenOpenRequest(
    ResourceId ScreenId,
    ModSerializedValue? Arguments = null);

public interface IModScreenV2
{
    void OnOpened(ModScreenOpenRequest request);

    void OnClosed();

    void Draw(IModUiCanvas canvas);

    ModActionResult HandleInput(ModUiInput input);
}

public interface IModScreenFactory
{
    IModScreenV2 Create();
}

public enum ModHudAnchor
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight
}

public interface IModHudLayer
{
    void Draw(IModUiCanvas canvas);
}

/// <summary>
/// Factory-based client UI. Registration is configuration-time and owner-namespaced. One factory creates
/// one session instance, so state is never shared accidentally between players or repeated opens.
/// </summary>
public interface IModClientUiRegistry
{
    void RegisterScreen(ResourceId id, IModScreenFactory factory);

    void RegisterHud(ResourceId id, ModHudAnchor anchor, int priority, IModHudLayer layer);

    bool Open(ModScreenOpenRequest request);

    bool Close();
}
