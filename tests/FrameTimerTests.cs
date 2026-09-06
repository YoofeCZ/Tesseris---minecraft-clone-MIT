using Tesseris.Engine.Core;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy statistiky frame timů. Testuje se statická metoda nad spanem, takže výsledky
/// nezávisí na tom, jak rychle zrovna běží testovací stroj.
/// </summary>
public sealed class FrameTimerTests
{
    [Fact]
    public void Prazdna_sada_vraci_nuly()
    {
        FrameStats stats = FrameTimer.SummarizeSamples([]);

        Assert.Equal(0, stats.SampleCount);
        Assert.Equal(0.0, stats.MaxMs);
        Assert.Equal(0, stats.OverBudget);
    }

    [Fact]
    public void Percentily_odpovidaji_metode_nearest_rank()
    {
        // 1..100 ms; nearest-rank: p50 = 50. prvek, p95 = 95., p99 = 99.
        double[] samples = new double[100];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = i + 1;
        }

        FrameStats stats = FrameTimer.SummarizeSamples(samples);

        Assert.Equal(100, stats.SampleCount);
        Assert.Equal(1.0, stats.MinMs);
        Assert.Equal(50.0, stats.P50Ms);
        Assert.Equal(95.0, stats.P95Ms);
        Assert.Equal(99.0, stats.P99Ms);
        Assert.Equal(100.0, stats.MaxMs);
        Assert.Equal(50.5, stats.MeanMs, 6);
    }

    [Fact]
    public void Poradi_vzorku_na_vysledku_nezalezi()
    {
        FrameStats ascending = FrameTimer.SummarizeSamples([1.0, 2.0, 3.0, 4.0, 5.0]);
        FrameStats shuffled = FrameTimer.SummarizeSamples([4.0, 1.0, 5.0, 3.0, 2.0]);

        Assert.Equal(ascending.P50Ms, shuffled.P50Ms);
        Assert.Equal(ascending.MinMs, shuffled.MinMs);
        Assert.Equal(ascending.MaxMs, shuffled.MaxMs);
    }

    [Fact]
    public void Pocita_framy_nad_rozpoctem()
    {
        // Rozpočet 16.67 ms: nad ním leží 20 a 33, tedy dva vzorky.
        FrameStats stats = FrameTimer.SummarizeSamples([5.0, 20.0, 8.0, 33.0, 16.0], 16.67);

        Assert.Equal(2, stats.OverBudget);
    }

    [Fact]
    public void Jediny_vzorek_da_stejnou_hodnotu_ve_vsech_percentilech()
    {
        FrameStats stats = FrameTimer.SummarizeSamples([7.5]);

        Assert.Equal(1, stats.SampleCount);
        Assert.Equal(7.5, stats.MinMs);
        Assert.Equal(7.5, stats.P50Ms);
        Assert.Equal(7.5, stats.P99Ms);
        Assert.Equal(7.5, stats.MaxMs);
    }

    [Fact]
    public void Vstupni_pole_zustane_neserazene()
    {
        // Kdyby metoda řadila přímo vstup, přepsala by volajícímu jeho data.
        double[] samples = [3.0, 1.0, 2.0];

        FrameTimer.SummarizeSamples(samples);

        Assert.Equal([3.0, 1.0, 2.0], samples);
    }

    [Fact]
    public void Novy_timer_jeste_nema_zadny_vzorek()
    {
        var timer = new FrameTimer();

        Assert.Equal(0, timer.FrameCount);
        Assert.Equal(0, timer.TotalStats().SampleCount);
    }

    [Fact]
    public void Prvni_BeginFrame_jeste_nezaznamena_vzorek()
    {
        var timer = new FrameTimer();

        // Z jednoho okamžiku nejde spočítat délku framu — potřebují se dva.
        timer.BeginFrame();

        Assert.Equal(0, timer.FrameCount);

        timer.EndCpuWork();
        timer.BeginFrame();

        Assert.Equal(1, timer.FrameCount);
        Assert.Equal(1, timer.TotalStats().SampleCount);
    }
}
