using OpenTK.Mathematics;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Denní cyklus.
///
/// <para>Hlavní věc, kterou testy hlídají, je <b>že slunce neprojde nadhlavníkem</b>.
/// Komentář u <see cref="DayCycle"/> to tvrdil odjakživa, ale vzorec tvrdil něco jiného:
/// náklon rovinu dráhy jen otáčel kolem svislice, takže v poledne vycházelo přesně
/// <c>(0, 1, 0)</c>. Stálo to mrtvé polední osvětlení a hlavně nestabilní osy stínové
/// mapy, protože kolmé slunce je pro <c>LookAt</c> degenerovaný případ.</para>
/// </summary>
public sealed class DayCycleTests
{
    private static Vector3 SunAt(float timeOfDay) => new DayCycle { TimeOfDay = timeOfDay }.SunDirection;

    [Fact]
    public void MinecraftovyCyklusTrvaDvacetMinutAMaPresneUseky()
    {
        Assert.Equal(1200f, DayCycle.DayLength);

        Assert.Equal(0f, new DayCycle { TimeOfDay = 5f / 24f }.Daylight);
        Assert.InRange(new DayCycle { TimeOfDay = 5.5f / 24f }.Daylight, 0.49f, 0.51f);
        Assert.Equal(1f, new DayCycle { TimeOfDay = 6f / 24f }.Daylight);
        Assert.Equal(1f, new DayCycle { TimeOfDay = 18f / 24f }.Daylight);
        Assert.InRange(new DayCycle { TimeOfDay = 18.5f / 24f }.Daylight, 0.49f, 0.51f);
        Assert.Equal(0f, new DayCycle { TimeOfDay = 19f / 24f }.Daylight);
        Assert.Equal(0f, new DayCycle { TimeOfDay = 0f }.Daylight);
    }

    /// <summary>
    /// Nejvyšší možná výška slunce odpovídá kulminaci na 66°, ne na 90°. Hodnota je
    /// zároveň důvod, proč <see cref="ShadowFrame.Up"/> nikdy nesáhne po záložní ose.
    /// </summary>
    [Fact]
    public void SlunceNikdyNeprojdeNadhlavnikem()
    {
        float highest = 0f;

        for (int step = 0; step <= 2000; step++)
        {
            highest = MathF.Max(highest, MathF.Abs(SunAt(step / 2000f).Y));
        }

        Assert.InRange(highest, 0.90f, 0.92f);
    }

    [Fact]
    public void VychodJeNaVychodeAZapadNaZapade()
    {
        Vector3 sunrise = SunAt(0.25f);
        Vector3 sunset = SunAt(0.75f);

        Assert.Equal(0f, sunrise.Y, 3);
        Assert.Equal(1f, sunrise.X, 3);

        Assert.Equal(0f, sunset.Y, 3);
        Assert.Equal(-1f, sunset.X, 3);
    }

    [Fact]
    public void PulnocJeNejhloubejiPodObzorem()
    {
        Assert.True(SunAt(0f).Y < -0.9f);
        Assert.False(new DayCycle { TimeOfDay = 0f }.IsDay);
    }

    [Fact]
    public void SmerJeVzdyckyJednotkovy()
    {
        for (int step = 0; step <= 500; step++)
        {
            Assert.Equal(1f, SunAt(step / 500f).Length, 4);
        }
    }

    /// <summary>Měsíc stojí proti slunci, takže když je den, měsíc je pod obzorem.</summary>
    [Fact]
    public void MesicStojiProtiSlunci()
    {
        var day = new DayCycle { TimeOfDay = 0.5f };

        Assert.Equal(-day.SunDirection, day.MoonDirection);
        Assert.True(day.IsDay);
    }

    [Fact]
    public void CasSeZabaluje()
    {
        var day = new DayCycle { TimeOfDay = 0.99f, Running = true, Speed = 1f };

        day.Update(DayCycle.DayLength * 0.05f);

        Assert.InRange(day.TimeOfDay, 0f, 1f);
    }

    [Fact]
    public void ZastavenyCasSeNeposouva()
    {
        var day = new DayCycle { TimeOfDay = 0.4f, Running = false };

        day.Update(10f);

        Assert.Equal(0.4f, day.TimeOfDay, 5);
    }

    [Fact]
    public void Zamcene_slunce_stoji_ale_biologicky_cas_bezi()
    {
        var day = new DayCycle
        {
            TimeOfDay = 0.5f,
            Running = true,
            Speed = 32f,
            SunFrozen = true,
        };

        day.Update(10f);

        Assert.Equal(0.5f, day.TimeOfDay, 5);
        Assert.Equal(320f, day.BiologicalSeconds(10f), precision: 4);
        Assert.Equal(320f, day.GrowthSeconds(10f), precision: 4);

        day.Speed = 64f;
        day.NatureSpeed = 2f;
        Assert.Equal(1280f, day.BiologicalSeconds(10f), precision: 4);

        day.SunFrozen = false;
        day.Update(1f);
        Assert.Equal(0.5f + (64f / DayCycle.DayLength), day.TimeOfDay, precision: 5);
    }

    [Fact]
    public void RychlostSlunceJeZarovenRychlostiBiologickehoCasu()
    {
        var day = new DayCycle { TimeOfDay = 0.5f, Running = true, Speed = 32f };

        Assert.Equal(16f, day.BiologicalSeconds(0.5f), precision: 4);
        Assert.Equal(16f, day.GrowthSeconds(0.5f), precision: 4);

        day.TimeOfDay = 0f;
        Assert.Equal(16f, day.BiologicalSeconds(0.5f), precision: 4);
        Assert.Equal(0f, day.GrowthSeconds(0.5f));

        day.Running = false;
        Assert.Equal(0f, day.BiologicalSeconds(10f));
        Assert.Equal(0f, day.GrowthSeconds(10f));
    }
}
