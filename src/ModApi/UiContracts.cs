namespace Tesseris.ModApi;

/// <summary>Registers UI overlays owned by a mod.</summary>
public interface IModUiRegistry
{
    /// <summary>
    /// Registers an overlay. Lower priorities are drawn first and therefore appear behind overlays
    /// with a higher priority. Equal priorities are ordered by <paramref name="id"/>.
    /// </summary>
    void Register(ResourceId id, int priority, IModOverlay overlay);

    /// <summary>
    /// Registers an openable modal screen owned by this mod. IDs must use the registering mod's
    /// namespace. A screen receives input and drawing callbacks only on the game thread.
    /// </summary>
    void RegisterScreen(ResourceId id, IModScreen screen)
        => throw new NotSupportedException("This mod host does not expose modal screens.");

    /// <summary>Opens a registered screen, replacing the current modal screen. Returns false for an unknown ID.</summary>
    bool OpenScreen(ResourceId id)
        => throw new NotSupportedException("This mod host does not expose modal screens.");

    /// <summary>Closes the current modal screen. Returns false when no screen is open.</summary>
    bool CloseScreen()
        => throw new NotSupportedException("This mod host does not expose modal screens.");

    ResourceId? OpenScreenId => null;
}

/// <summary>A renderer-independent UI extension rendered once per frame.</summary>
public interface IModOverlay
{
    void Draw(IModUiCanvas canvas);
}

/// <summary>
/// A renderer-independent modal interface. Instances are registered once and can be opened many
/// times. All methods run on the game thread; input is delivered in deterministic queue order.
/// </summary>
public interface IModScreen
{
    void OnOpened();

    void OnClosed();

    void Draw(IModUiCanvas canvas);

    ModActionResult HandleInput(ModUiInput input);
}

[Flags]
public enum ModUiModifiers
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    Super = 1 << 3
}

public enum ModPointerButton
{
    None,
    Primary,
    Secondary,
    Middle,
    Back,
    Forward
}

public enum ModUiInputKind
{
    PointerMove,
    PointerButton,
    Key,
    Text,
    Wheel
}

/// <summary>
/// A generic UI input event. Key codes are stable integer codes for the current ModApi version;
/// mods should bind them from observed input rather than referencing engine-specific enums.
/// Fields unrelated to <see cref="Kind"/> retain their default values.
/// </summary>
public readonly record struct ModUiInput(
    ModUiInputKind Kind,
    ModInputPhase Phase,
    int PointerX,
    int PointerY,
    ModPointerButton PointerButton = ModPointerButton.None,
    int KeyCode = 0,
    string? Text = null,
    float WheelX = 0,
    float WheelY = 0,
    ModUiModifiers Modifiers = ModUiModifiers.None);

/// <summary>Non-premultiplied 8-bit RGBA color.</summary>
public readonly record struct ModColor(byte R, byte G, byte B, byte A = byte.MaxValue)
{
    public static ModColor White => new(byte.MaxValue, byte.MaxValue, byte.MaxValue);

    public static ModColor Black => new(0, 0, 0);
}

/// <summary>
/// The deliberately small drawing surface exposed to managed mods. Coordinates are screen pixels;
/// implementations clip primitives to the current screen and never expose engine graphics objects.
/// </summary>
public interface IModUiCanvas
{
    int ScreenWidth { get; }

    int ScreenHeight { get; }

    void DrawText(string text, int x, int y, int scale = 1);

    void DrawRect(int x, int y, int width, int height);

    /// <summary>Draws a solid colored rectangle. Older hosts may fall back to their default color.</summary>
    void DrawRect(int x, int y, int width, int height, ModColor color)
        => DrawRect(x, y, width, height);
}
