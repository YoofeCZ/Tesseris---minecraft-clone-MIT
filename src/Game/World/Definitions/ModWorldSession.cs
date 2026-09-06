using Tesseris.ModApi;

namespace Tesseris.Game.World.Definitions;

public enum ModWorldSessionState
{
    Open,
    Closed,
}

public interface IModWorldSessionLifecycle
{
    void Opened(ModWorldRuntime runtime);

    void Switching(ModWorldRuntime from, ModWorldRuntime to);

    void Switched(ModWorldRuntime current);

    void Closed(ModWorldRuntime runtime);
}

public sealed class ModWorldSession : IDisposable
{
    private readonly int ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly IModWorldSessionLifecycle? lifecycle;

    internal ModWorldSession(ModWorldRuntime runtime, IModWorldSessionLifecycle? lifecycle)
    {
        Current = runtime;
        this.lifecycle = lifecycle;
        lifecycle?.Opened(runtime);
    }

    public ModWorldRuntime Current { get; private set; }

    public ModWorldSessionState State { get; private set; } = ModWorldSessionState.Open;

    public ModWorldRuntime Switch(ResourceId dimensionId)
    {
        EnsureOpen();
        ModWorldRuntime next = Current.SelectDimension(dimensionId);
        if (next.Dimension.Id == Current.Dimension.Id)
        {
            return Current;
        }

        lifecycle?.Switching(Current, next);
        Current = next;
        lifecycle?.Switched(next);
        return next;
    }

    public void Close()
    {
        EnsureThread();
        if (State == ModWorldSessionState.Closed)
        {
            return;
        }

        State = ModWorldSessionState.Closed;
        lifecycle?.Closed(Current);
    }

    public void Dispose() => Close();

    private void EnsureOpen()
    {
        EnsureThread();
        if (State != ModWorldSessionState.Open)
        {
            throw new InvalidOperationException("The world session is closed.");
        }
    }

    private void EnsureThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId)
        {
            throw new InvalidOperationException("World dimension lifecycle must run on its opening thread.");
        }
    }
}
