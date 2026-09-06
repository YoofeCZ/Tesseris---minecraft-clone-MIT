using Tesseris.Game.UI;
using Xunit;

namespace Tesseris.Tests;

public sealed class PlayerOptionsTests
{
    [Fact]
    public void Options_round_trip_and_clamp_unsafe_values()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"tesseris-options-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "options.json");
        try
        {
            var options = new PlayerOptions
            {
                MouseSensitivity = 10f,
                FieldOfView = 500f,
                ViewDistance = 200,
                MasterVolume = -1f,
                VSync = false,
                FullView = true,
                GraphicsQuality = PlayerOptions.HighGraphics,
            };
            options.Save(path);

            PlayerOptions loaded = PlayerOptions.Load(path);
            Assert.Equal(0.3f, loaded.MouseSensitivity);
            Assert.Equal(110f, loaded.FieldOfView);
            Assert.Equal(32, loaded.ViewDistance);
            Assert.Equal(0f, loaded.MasterVolume);
            Assert.False(loaded.VSync);
            Assert.True(loaded.FullView);
            Assert.Equal(PlayerOptions.HighGraphics, loaded.GraphicsQuality);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Legacy_options_migrate_from_ultra_like_defaults_to_balanced()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"ViewDistance\":16,\"VSync\":true}");
            PlayerOptions options = PlayerOptions.Load(path);
            Assert.Equal(PlayerOptions.BalancedGraphics, options.GraphicsQuality);
            Assert.Equal(10, options.ViewDistance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    // Práh nejvyššího profilu je OSM GiB, ne šestnáct. Šestnáct nesplnila ani RTX 4070 SUPER
    // s dvanácti, přestože na nejvyšší profil má výkonu dost (naměřeno 446 FPS). Karty
    // s osmi a víc gigabajty jsou dnes běžný střed pole — viz PlayerOptions.RecommendGraphicsProfile.
    [InlineData(4, false, PlayerOptions.LowGraphics)]
    [InlineData(6, false, PlayerOptions.BalancedGraphics)]
    [InlineData(7, false, PlayerOptions.BalancedGraphics)]
    [InlineData(8, false, PlayerOptions.HighGraphics)]
    [InlineData(12, false, PlayerOptions.HighGraphics)]
    [InlineData(24, false, PlayerOptions.HighGraphics)]
    [InlineData(6, true, PlayerOptions.LowGraphics)]
    [InlineData(16, true, PlayerOptions.BalancedGraphics)]
    public void Hardware_profile_uses_vram_and_portability(
        int memoryGiB,
        bool portability,
        int expected)
    {
        ulong bytes = (ulong)memoryGiB * 1024UL * 1024UL * 1024UL;
        Assert.Equal(expected, PlayerOptions.RecommendGraphicsProfile(bytes, portability));
    }

    [Fact]
    public void Applying_low_profile_sets_coherent_low_cost_options()
    {
        var options = new PlayerOptions();
        options.ApplyGraphicsProfile(PlayerOptions.LowGraphics, automatic: true);

        Assert.True(options.AutomaticGraphics);
        Assert.Equal(8, options.ViewDistance);
        Assert.Equal(1, options.MsaaSamples);
        Assert.Equal(1024, options.MapDetailSize);
        Assert.False(options.Shadows);
        Assert.Equal(1, options.CloudQuality);
        Assert.Equal(1, options.FogQuality);
        Assert.False(options.Bloom);
        Assert.False(options.Fxaa);
    }

    [Fact]
    public void Invalid_file_falls_back_to_defaults()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not json");
            PlayerOptions options = PlayerOptions.Load(path);
            Assert.Equal(70f, options.FieldOfView);
            Assert.Equal(0.7f, options.MasterVolume);
            Assert.Equal(PlayerOptions.BalancedGraphics, options.GraphicsQuality);
            Assert.Equal(10, options.ViewDistance);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
