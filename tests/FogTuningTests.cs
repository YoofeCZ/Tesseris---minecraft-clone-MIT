using Tesseris.Game.UI;
using Xunit;

namespace Tesseris.Tests;

public sealed class FogTuningTests
{
    [Fact]
    public void Manual_offsets_survive_moving_automatic_fog()
    {
        var fog = new FogTuning();
        fog.UpdateAutomatic(240f, 520f, 1000f);
        fog.SetStart(280f);
        fog.SetEnd(600f);

        fog.UpdateAutomatic(300f, 700f, 1200f);

        Assert.Equal(340f, fog.Start);
        Assert.Equal(780f, fog.End);
    }

    [Fact]
    public void Untouched_fog_follows_automatic_distances_exactly()
    {
        var fog = new FogTuning();

        fog.UpdateAutomatic(320f, 900f, 1200f);

        Assert.Equal(320f, fog.Start);
        Assert.Equal(900f, fog.End);
    }

    [Fact]
    public void Fog_range_stays_ordered_and_inside_shader_limits()
    {
        var fog = new FogTuning();
        fog.UpdateAutomatic(200f, 500f, 600f);
        fog.SetStart(float.MaxValue);
        fog.SetEnd(float.MinValue);

        Assert.Equal(536f, fog.Start);
        Assert.Equal(600f, fog.End);
        Assert.Equal(FogTuning.MinimumSpan, fog.End - fog.Start);
    }
}
