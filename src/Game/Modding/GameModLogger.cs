using Tesseris.Engine.Core;
using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>Routes public mod API messages through the engine's thread-safe logger.</summary>
public sealed class GameModLogger : IModLogger
{
    public static readonly GameModLogger Instance = new();

    private GameModLogger()
    {
    }

    public void Info(string message) => Log.Info($"[mod] {message}");

    public void Warning(string message) => Log.Warn($"[mod] {message}");

    public void Error(string message, Exception? exception = null) =>
        Log.Error(exception is null ? $"[mod] {message}" : $"[mod] {message}: {exception}");
}
