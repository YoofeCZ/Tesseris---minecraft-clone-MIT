using Tesseris.ModApi;

namespace Tesseris.Game.Modding;

/// <summary>Immutable main-thread tick information exposed to mods.</summary>
public readonly record struct GameTickContext(ulong Tick, TimeSpan Delta) : IGameTickContext;
