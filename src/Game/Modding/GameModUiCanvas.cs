using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>
/// Bounds-checked command recorder for the public UI API. Geometry and text are emitted in their
/// respective renderer batches after mod callbacks return, so colored panels never degrade into
/// white font-atlas rectangles.
/// </summary>
public sealed class GameModUiCanvas : IModUiCanvas
{
    private const int MaxTextLength = 512;
    private const int MaxCommands = 2048;
    private readonly List<RectCommand> rectangles = [];
    private readonly List<TextCommand> text = [];

    public GameModUiCanvas(int screenWidth, int screenHeight)
    {
        ScreenWidth = Math.Max(1, screenWidth);
        ScreenHeight = Math.Max(1, screenHeight);
    }

    public int ScreenWidth { get; }

    public int ScreenHeight { get; }

    public void DrawText(string value, int x, int y, int scale = 1)
    {
        ArgumentNullException.ThrowIfNull(value);
        scale = Math.Clamp(scale, 1, 8);
        if (text.Count >= MaxCommands || value.Length == 0 || x >= ScreenWidth || y >= ScreenHeight
            || y + (TextRenderer.LineHeight * scale) <= 0)
        {
            return;
        }

        text.Add(new TextCommand(
            value.Length <= MaxTextLength ? value : value[..MaxTextLength],
            x,
            y,
            scale));
    }

    public void DrawRect(int x, int y, int width, int height) =>
        DrawRect(x, y, width, height, ModColor.White);

    public void DrawRect(int x, int y, int width, int height, ModColor color)
    {
        if (rectangles.Count >= MaxCommands || width <= 0 || height <= 0) return;

        int left = Math.Clamp(x, 0, ScreenWidth);
        int top = Math.Clamp(y, 0, ScreenHeight);
        int right = (int)Math.Clamp((long)x + width, 0L, ScreenWidth);
        int bottom = (int)Math.Clamp((long)y + height, 0L, ScreenHeight);
        if (right <= left || bottom <= top) return;

        rectangles.Add(new RectCommand(
            left,
            top,
            right - left,
            bottom - top,
            new Vector4(
                color.R / 255f,
                color.G / 255f,
                color.B / 255f,
                color.A / 255f)));
    }

    public void Render(SpriteRenderer sprites, TextRenderer renderer, RenderStats stats)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(stats);

        if (rectangles.Count > 0)
        {
            sprites.Begin(ScreenWidth, ScreenHeight);
            foreach (RectCommand rectangle in rectangles)
            {
                sprites.DrawRect(
                    rectangle.X,
                    rectangle.Y,
                    rectangle.Width,
                    rectangle.Height,
                    rectangle.Color);
            }
            sprites.End();
        }

        if (text.Count > 0)
        {
            renderer.Begin(ScreenWidth, ScreenHeight);
            foreach (TextCommand command in text)
            {
                renderer.DrawText(command.Value, command.X, command.Y, command.Scale);
            }
            renderer.End(stats);
        }
    }

    private readonly record struct RectCommand(int X, int Y, int Width, int Height, Vector4 Color);

    private readonly record struct TextCommand(string Value, int X, int Y, int Scale);
}
