using Tesseris.Engine.Core;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy job systĂ©mu. SoubÄ›ĹľnĂ˝ kĂłd se ĹˇpatnÄ› testuje, protoĹľe chyba se projevĂ­ jen obÄŤas â€”
/// proto se tady pracuje s velkĂ˝mi poÄŤty Ăşloh a s ÄŤekĂˇnĂ­m na udĂˇlost mĂ­sto na ÄŤas.
/// </summary>
public sealed class JobSystemTests
{
    /// <summary>Kolik nejdĂ©le se ÄŤekĂˇ, neĹľ se Ăşloha stihne. Ĺ tÄ›dĹ™e, aĹĄ test nespadne na pomalĂ©m stroji.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void Vychozi_pocet_vlaken_nechava_dve_jadra_hlavnimu_vlaknu()
    {
        using var jobs = new JobSystem();

        // Dvě jádra zůstanou volná, ne jedno: jedno bere hlavní vlákno a druhé ovladač
        // grafiky, který si při odesílání práce bere vlastní čas.
        Assert.Equal(Math.Max(1, Environment.ProcessorCount - 2), jobs.WorkerCount);
    }

    [Fact]
    public void Pocet_vlaken_jde_urcit()
    {
        using var jobs = new JobSystem(workerCount: 3);

        Assert.Equal(3, jobs.WorkerCount);
    }

    [Fact]
    public void Odeslana_uloha_se_provede()
    {
        using var jobs = new JobSystem(workerCount: 2);
        using var done = new ManualResetEventSlim();

        jobs.Submit(done.Set);

        Assert.True(done.Wait(Patience), "Ăšloha se nespustila vÄŤas.");
    }

    [Fact]
    public void Vsechny_ulohy_se_provedou_prave_jednou()
    {
        const int count = 5000;

        using var jobs = new JobSystem(workerCount: 4);
        using var finished = new CountdownEvent(count);

        int sum = 0;

        for (int i = 0; i < count; i++)
        {
            jobs.Submit(() =>
            {
                Interlocked.Increment(ref sum);
                finished.Signal();
            });
        }

        Assert.True(finished.Wait(Patience), "Ne vĹˇechny Ăşlohy dobÄ›hly vÄŤas.");
        Assert.Equal(count, Volatile.Read(ref sum));
    }

    [Fact]
    public void Vyjimka_v_uloze_nezabije_pool()
    {
        using var jobs = new JobSystem(workerCount: 2);
        using var done = new ManualResetEventSlim();

        // VĂ˝jimka na worker vlĂˇknÄ› by bez odchycenĂ­ shodila celĂ˝ proces.
        jobs.Submit(() => throw new InvalidOperationException("schvĂˇlnÄ›"));
        jobs.Submit(done.Set);

        Assert.True(done.Wait(Patience), "Po vĂ˝jimce pĹ™estal pool pĹ™ijĂ­mat prĂˇci.");
    }

    [Fact]
    public void Pocet_rozpracovanych_klesne_na_nulu()
    {
        using var jobs = new JobSystem(workerCount: 3);
        using var finished = new CountdownEvent(200);

        for (int i = 0; i < 200; i++)
        {
            // Ne pĹ™Ă­mo finished.Signal: to pĹ™etĂ­ĹľenĂ­ vracĂ­ bool a jako Action se nepĹ™eloĹľĂ­.
            jobs.Submit(() => finished.Signal());
        }

        Assert.True(finished.Wait(Patience));

        // PoÄŤĂ­tadlo se sniĹľuje aĹľ po dobÄ›hnutĂ­ Ăşlohy, takĹľe se chvĂ­li mĹŻĹľe liĹˇit.
        SpinWait.SpinUntil(() => jobs.PendingJobs == 0, Patience);
        Assert.Equal(0, jobs.PendingJobs);
    }

    [Fact]
    public void Ulohy_bezi_opravdu_soubezne()
    {
        using var jobs = new JobSystem(workerCount: 4);
        using var allArrived = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();

        // KdyĹľ by se Ăşlohy zpracovĂˇvaly po jednĂ©, ÄŤtvrtĂˇ by se nikdy nedostala na Ĺ™adu
        // a odpoÄŤet by nedobÄ›hl.
        for (int i = 0; i < 4; i++)
        {
            jobs.Submit(() =>
            {
                allArrived.Signal();
                release.Wait(Patience);
            });
        }

        Assert.True(allArrived.Wait(Patience), "Ăšlohy nebÄ›Ĺľely soubÄ›ĹľnÄ›.");
        release.Set();
    }

    [Fact]
    public void Po_uvolneni_uz_nejde_zadavat()
    {
        var jobs = new JobSystem(workerCount: 2);
        jobs.Dispose();

        Assert.Throws<ObjectDisposedException>(() => jobs.Submit(() => { }));
    }

    [Fact]
    public void Opakovane_uvolneni_nevadi()
    {
        var jobs = new JobSystem(workerCount: 2);

        jobs.Dispose();
        jobs.Dispose();
    }

    [Fact]
    public void Interaktivni_uloha_predbehne_drive_zadane_fronty()
    {
        using var jobs = new JobSystem(workerCount: 1);
        using var workerStarted = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var finished = new CountdownEvent(3);

        jobs.Submit(() =>
        {
            workerStarted.Set();
            releaseWorker.Wait(Patience);
        });

        Assert.True(workerStarted.Wait(Patience), "Blokovací úloha se nespustila včas.");

        var order = new int[3];
        int next = -1;

        void Record(int value)
        {
            int index = Interlocked.Increment(ref next);
            order[index] = value;
            finished.Signal();
        }

        jobs.Submit(() => Record(1), JobPriority.Background);
        jobs.Submit(() => Record(2), JobPriority.Normal);
        jobs.Submit(() => Record(3), JobPriority.Interactive);

        releaseWorker.Set();

        Assert.True(finished.Wait(Patience), "Prioritní úlohy se nedokončily včas.");
        Assert.Equal([3, 2, 1], order);
    }
}

