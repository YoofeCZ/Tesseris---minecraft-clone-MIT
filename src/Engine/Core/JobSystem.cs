using System.Collections.Concurrent;

namespace Tesseris.Engine.Core;

/// <summary>
/// Pool pracovních vláken pro věci, které nesmí zdržovat hlavní vlákno — generování
/// a meshování chunků.
///
/// Pravidlo, které se nesmí porušit: <b>na worker vláknech se nikdy nevolá OpenGL</b>.
/// Kontext patří hlavnímu vláknu; workery smí sahat jen na data. Hotová práce se předává
/// zpátky přes <see cref="ConcurrentQueue{T}"/>, která je bez zámků, takže hlavní vlákno
/// nikdy nečeká na worker ani naopak.
///
/// Práce má tři oddělené fronty. Interakce hráče předbíhá běžný streaming a vzdálený LOD
/// běží jako pozadí; uvnitř každé priority zůstává pořadí FIFO.
/// </summary>
public enum JobPriority
{
    Background,
    Normal,
    Interactive,
}

public sealed class JobSystem : IDisposable
{
    private readonly Thread[] _workers;
    private readonly ConcurrentQueue<Action> _interactiveQueue = new();
    private readonly ConcurrentQueue<Action> _normalQueue = new();
    private readonly ConcurrentQueue<Action> _backgroundQueue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    private int _pending;

    /// <param name="workerCount">
    /// Počet vláken. Nula nebo méně znamená automaticky: počet logických jader mínus dvě.
    /// </param>
    /// <remarks>
    /// <para><b>Odečítají se dvě jádra, ne jedno.</b> Původně se nechávalo jedno „pro hlavní
    /// vlákno", jenže na něj se musí vejít i ovladač grafiky, který má vlastní vlákna, plus
    /// operační systém. Hlavní vlákno pak o procesor soupeří — a prohrává.</para>
    ///
    /// <para><b>Naměřeno, jak to vypadá:</b> při rychlém letu se přepočítá LOD, do fronty
    /// spadne přes čtyři sta dlaždic a jedenáct workerů obsadí jedenáct z dvanácti jader.
    /// Snímek pak trvá až <b>163 ms</b>, ale <b>žádná měřená fáze nenaměří nic</b> — protože
    /// fáze měří dobu, kdy vlákno běží, a ono neběželo vůbec. Úklid paměti v tom nebyl
    /// (nula ze čtyřiceti jednoho záseku).</para>
    /// </remarks>
    public JobSystem(int workerCount = 0)
    {
        int count = workerCount > 0 ? workerCount : Math.Max(1, Environment.ProcessorCount - 2);

        _workers = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            var thread = new Thread(WorkerLoop)
            {
                // Na pozadí, aby zapomenutý worker nedržel proces naživu.
                IsBackground = true,

                // NIŽŠÍ PRIORITA NEŽ HLAVNÍ VLÁKNO, a tohle je ta důležitější polovina
                // opravy. Samotné ubrání jádra nestačí: jakmile je práce dost, workery
                // zaplní i to zbylé. S nižší prioritou je plánovač odstaví, kdykoli má
                // hlavní vlákno co dělat — a stavba dlaždic si vezme jen to, co zbude.
                //
                // Snímek je přednější než to, jak rychle doroste obzor: díra v dálce je
                // nepříjemná, ale cuknutí na třicet snímků je horší.
                Priority = ThreadPriority.BelowNormal,

                Name = $"tesseris-worker-{i}",
            };

            _workers[i] = thread;
            thread.Start();
        }

        Log.Info($"Job systém spuštěn s {count} vlákny (logických jader {Environment.ProcessorCount}).");
    }

    public int WorkerCount => _workers.Length;

    /// <summary>Kolik úkolů čeká nebo se právě zpracovává.</summary>
    public int PendingJobs => Volatile.Read(ref _pending);

    /// <summary>Zařadí práci. Vrací se okamžitě.</summary>
    public void Submit(Action job) => Submit(job, JobPriority.Normal);

    /// <summary>
    /// Zařadí práci s výslovnou prioritou. Interaktivní úlohy hráče předbíhají běžný
    /// streaming a vzdálené pozadí; už běžící práce se bezpečně nechá dokončit.
    /// </summary>
    public void Submit(Action job, JobPriority priority)
    {
        ArgumentNullException.ThrowIfNull(job);
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);

        Interlocked.Increment(ref _pending);

        switch (priority)
        {
            case JobPriority.Interactive:
                _interactiveQueue.Enqueue(job);
                break;
            case JobPriority.Normal:
                _normalQueue.Enqueue(job);
                break;
            case JobPriority.Background:
                _backgroundQueue.Enqueue(job);
                break;
            default:
                Interlocked.Decrement(ref _pending);
                throw new ArgumentOutOfRangeException(nameof(priority));
        }

        _available.Release();
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();

        // Každé vlákno musí dostat šanci se probudit a všimnout si zrušení.
        _available.Release(_workers.Length);

        foreach (Thread worker in _workers)
        {
            // Krátký limit: workery jsou na pozadí, takže zaseknutý úkol nesmí blokovat
            // ukončení procesu. Když se do limitu nevejde, prostě se nechá běžet.
            worker.Join(TimeSpan.FromSeconds(2));
        }

        _available.Dispose();
        _shutdown.Dispose();
    }

    private void WorkerLoop()
    {
        CancellationToken token = _shutdown.Token;

        while (true)
        {
            try
            {
                _available.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!TryDequeue(out Action job))
            {
                continue;
            }

            try
            {
                job();
            }
            catch (Exception ex)
            {
                // Výjimka na worker vlákně by jinak shodila celý proces a nebylo by poznat proč.
                Log.Error($"Úkol na worker vlákně skončil výjimkou: {ex}");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private bool TryDequeue(out Action job)
    {
        if (_interactiveQueue.TryDequeue(out Action? interactive))
        {
            job = interactive!;
            return true;
        }

        if (_normalQueue.TryDequeue(out Action? normal))
        {
            job = normal!;
            return true;
        }

        if (_backgroundQueue.TryDequeue(out Action? background))
        {
            job = background!;
            return true;
        }

        job = null!;
        return false;
    }
}
