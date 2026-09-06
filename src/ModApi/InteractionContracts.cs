namespace Tesseris.ModApi;

/// <summary>The kind of ordinary player block interaction being reported.</summary>
public enum ModBlockActionKind
{
    Break,
    Place
}

/// <summary>Whether a block interaction is about to happen or has successfully completed.</summary>
public enum ModBlockActionPhase
{
    Before,
    After
}

/// <summary>
/// Public, game-independent information about a normal player break or place action.
/// A before callback may set <see cref="Cancel"/> to stop the action. An after callback is only a
/// notification and attempting to assign <see cref="Cancel"/> throws <see cref="InvalidOperationException"/>.
/// </summary>
public interface IModBlockActionContext
{
    ModBlockActionPhase Phase { get; }

    ModBlockActionKind Kind { get; }

    int X { get; }

    int Y { get; }

    int Z { get; }

    /// <summary>
    /// For break actions, the block being broken. For place actions, the block being placed.
    /// </summary>
    ResourceId BlockId { get; }

    /// <summary>The stable ID of the selected item, or <see langword="null"/> when none is held.</summary>
    ResourceId? HeldItemId { get; }

    /// <summary>
    /// Gets or cancels a before action. Once a callback cancels an action, later callbacks are not invoked.
    /// After-action contexts cannot be cancelled and reject every assignment.
    /// </summary>
    bool Cancel { get; set; }
}
