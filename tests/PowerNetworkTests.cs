using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Power;
using Xunit;

namespace Tesseris.Tests;

public sealed class PowerNetworkTests
{
    [Fact]
    public void Na_jedne_stene_mohou_byt_ctyri_barevne_oddelené_kanaly()
    {
        var network = new PowerNetwork();
        var support = new Vector3i(4, 8, 12);

        foreach (CableColor color in Enum.GetValues<CableColor>())
        {
            Assert.True(network.ToggleSurface(support, BlockFace.PosZ, color));
            Assert.True(network.HasSurface(support, BlockFace.PosZ, color));
        }

        Assert.Equal(4, network.Surface.Count);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, network.Surface.OrderBy(c => c.Color).Select(c => c.Lane));
    }

    [Fact]
    public void Polozeni_existujiciho_kabelu_ho_neodebere()
    {
        var network = new PowerNetwork();
        var support = new Vector3i(2, 3, 4);

        Assert.True(network.AddSurface(support, BlockFace.PosY, CableColor.Red));
        Assert.False(network.AddSurface(support, BlockFace.PosY, CableColor.Red));

        Assert.True(network.HasSurface(support, BlockFace.PosY, CableColor.Red));
        Assert.Single(network.Surface);
    }

    [Fact]
    public void Kleste_vrati_presne_barvy_na_zasazene_plose()
    {
        var network = new PowerNetwork();
        var support = new Vector3i(2, 3, 4);
        network.AddSurface(support, BlockFace.PosY, CableColor.Red);
        network.AddSurface(support, BlockFace.PosY, CableColor.Blue);
        network.AddSurface(support, BlockFace.PosX, CableColor.Green);

        SurfaceCable[] removed = network.TakeSurface(support, BlockFace.PosY);

        Assert.Equal(
            new[] { CableColor.Red, CableColor.Blue },
            removed.OrderBy(cable => cable.Color).Select(cable => cable.Color));
        Assert.False(network.HasSurface(support, BlockFace.PosY, CableColor.Red));
        Assert.False(network.HasSurface(support, BlockFace.PosY, CableColor.Blue));
        Assert.True(network.HasSurface(support, BlockFace.PosX, CableColor.Green));
    }

    [Fact]
    public void Kleste_vrati_jednu_civku_za_kazdy_odpojeny_venkovni_vodic()
    {
        var network = new PowerNetwork();
        var terminal = Vector3i.Zero;
        var first = new Vector3i(5, 0, 0);
        var second = new Vector3i(0, 0, 5);
        var unrelatedA = new Vector3i(20, 0, 0);
        var unrelatedB = new Vector3i(25, 0, 0);
        network.TryAddOverhead(terminal, first, PowerTerminalKind.Relay, PowerTerminalKind.Relay);
        network.TryAddOverhead(terminal, second, PowerTerminalKind.Relay, PowerTerminalKind.Relay);
        network.TryAddOverhead(unrelatedA, unrelatedB, PowerTerminalKind.Relay, PowerTerminalKind.Relay);

        OverheadWire[] removed = network.TakeOverheadAt(terminal);

        Assert.Equal(2, removed.Length);
        Assert.Single(network.Overhead);
        Assert.Equal(new OverheadWire(unrelatedA, unrelatedB), network.Overhead[0]);
    }

    [Fact]
    public void Zniceni_nosneho_bloku_vrati_kabely_ze_vsech_jeho_sten()
    {
        var network = new PowerNetwork();
        var support = new Vector3i(8, 9, 10);
        var neighbour = support + Vector3i.UnitX;
        network.AddSurface(support, BlockFace.PosY, CableColor.Red);
        network.AddSurface(support, BlockFace.PosX, CableColor.Blue);
        network.AddSurface(support, BlockFace.NegZ, CableColor.Yellow);
        network.AddSurface(neighbour, BlockFace.PosY, CableColor.Green);

        SurfaceCable[] removed = network.TakeSurfaceAtBlock(support);

        Assert.Equal(
            new[] { CableColor.Red, CableColor.Blue, CableColor.Yellow },
            removed.OrderBy(cable => cable.Color).Select(cable => cable.Color));
        Assert.Single(network.Surface);
        Assert.True(network.HasSurface(neighbour, BlockFace.PosY, CableColor.Green));
    }

    [Fact]
    public void Povrchove_kabely_se_spoji_jen_ve_stejne_rovině_a_barve()
    {
        var network = new PowerNetwork();
        var a = new SurfaceCable(new Vector3i(0, 0, 0), BlockFace.PosZ, CableColor.Red, 0);
        var beside = new SurfaceCable(new Vector3i(1, 0, 0), BlockFace.PosZ, CableColor.Red, 0);
        var throughWall = new SurfaceCable(new Vector3i(0, 0, 1), BlockFace.PosZ, CableColor.Red, 0);
        var otherColor = beside with { Color = CableColor.Blue, Lane = 1 };

        Assert.True(network.IsSurfaceConnected(a, beside));
        Assert.False(network.IsSurfaceConnected(a, throughWall));
        Assert.False(network.IsSurfaceConnected(a, otherColor));
    }

    [Fact]
    public void Stejna_barva_se_na_jednom_bloku_spoji_pres_pravy_roh()
    {
        var network = new PowerNetwork();
        var wall = new SurfaceCable(Vector3i.Zero, BlockFace.PosZ, CableColor.Blue, 1);
        var ceiling = new SurfaceCable(Vector3i.Zero, BlockFace.PosY, CableColor.Blue, 1);
        var opposite = new SurfaceCable(Vector3i.Zero, BlockFace.NegZ, CableColor.Blue, 1);

        Assert.True(network.IsSurfaceConnected(wall, ceiling));
        Assert.False(network.IsSurfaceConnected(wall, opposite));
    }

    [Fact]
    public void Roh_se_spoji_i_kdyz_kliknuti_priradi_steny_sousednim_blokum()
    {
        var network = new PowerNetwork();
        var top = new SurfaceCable(Vector3i.Zero, BlockFace.PosY, CableColor.Yellow, 2);
        var neighbouringWall = new SurfaceCable(-Vector3i.UnitX, BlockFace.PosX, CableColor.Yellow, 2);

        Assert.True(network.IsSurfaceConnected(top, neighbouringWall));
        Assert.True(PowerNetwork.TrySharedEdge(top, neighbouringWall, out SurfaceEdge edge));
        Assert.Equal(new Vector3(0f, 1f, 0.5f), edge.Centre);
    }

    [Fact]
    public void Venkovni_vodic_ma_delkovy_limit_a_nezdvojuje_se()
    {
        var network = new PowerNetwork();
        var a = new Vector3i(0, 10, 0);
        var b = new Vector3i(12, 10, 0);

        Assert.True(network.TryAddOverhead(a, b, PowerTerminalKind.Relay, PowerTerminalKind.Relay));
        Assert.False(network.TryAddOverhead(b, a, PowerTerminalKind.Relay, PowerTerminalKind.Relay));
        Assert.False(network.TryAddOverhead(a, new Vector3i(60, 10, 0), PowerTerminalKind.Relay, PowerTerminalKind.Relay));
    }

    [Fact]
    public void Pripojka_je_pruchozi_ale_treti_vetev_vyzaduje_rozvodku()
    {
        var network = new PowerNetwork();
        var connector = Vector3i.Zero;

        Assert.True(network.TryAddOverhead(
            connector, new Vector3i(5, 0, 0), PowerTerminalKind.Connector, PowerTerminalKind.Relay));
        Assert.True(network.TryAddOverhead(
            connector, new Vector3i(0, 0, 5), PowerTerminalKind.Connector, PowerTerminalKind.Relay));
        Assert.False(network.TryAddOverhead(
            connector, new Vector3i(-5, 0, 0), PowerTerminalKind.Connector, PowerTerminalKind.Relay));
        Assert.False(network.HasFreePort(connector, PowerTerminalKind.Connector));
        Assert.Equal(
            OverheadAddResult.FirstFull,
            network.ValidateOverhead(
                connector,
                new Vector3i(-5, 0, 0),
                PowerTerminalKind.Connector,
                PowerTerminalKind.Relay));
        Assert.Equal(2, network.Overhead.Count);
    }

    [Fact]
    public void Neuspesny_druhy_konec_neprida_zadny_polovicni_vodic()
    {
        var network = new PowerNetwork();
        var first = Vector3i.Zero;
        var tooFar = new Vector3i(49, 0, 0);

        Assert.Equal(
            OverheadAddResult.TooLong,
            network.AddOverhead(first, tooFar, PowerTerminalKind.Relay, PowerTerminalKind.Relay));
        Assert.Empty(network.Overhead);
        Assert.Equal(0, network.Degree(first));
    }

    [Fact]
    public void Souvisly_kabel_nema_ctvercovou_tecku_na_kazdem_bloku()
    {
        var network = new PowerNetwork();
        network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, CableColor.Red);
        network.ToggleSurface(Vector3i.UnitX, BlockFace.PosY, CableColor.Red);
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        // Jediny hranol mezi dvema středy. Drive tu navic byly dva siroke ctvercove uzly.
        Assert.Equal(36, mesh.IndexCount);
    }

    [Fact]
    public void Barevne_drahy_zustanou_oddelene_v_obou_směrech_plochy()
    {
        AssertLaneSeparation(Vector3i.UnitX, coordinate: 2);
        AssertLaneSeparation(Vector3i.UnitZ, coordinate: 0);
    }

    [Fact]
    public void Krizeni_dvou_barev_dostane_jednu_spolecnou_krabicku()
    {
        var network = new PowerNetwork();
        AddStraight(network, CableColor.Red, Vector3i.UnitX);
        AddStraight(network, CableColor.Blue, Vector3i.UnitZ);
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        Assert.True(HasLayer(mesh, 21));
        Assert.Equal(24, CountLayerVertices(mesh, 21));
        Assert.True(MaxCoordinate(mesh, 21, 1) > MaxCoordinate(mesh, 10, 1));
    }

    [Fact]
    public void Ctverbarevne_plus_dostane_prave_jednu_krabicku()
    {
        var network = new PowerNetwork();
        foreach (CableColor color in Enum.GetValues<CableColor>())
        {
            network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, color);
            network.ToggleSurface(Vector3i.UnitX, BlockFace.PosY, color);
            network.ToggleSurface(-Vector3i.UnitX, BlockFace.PosY, color);
            network.ToggleSurface(Vector3i.UnitZ, BlockFace.PosY, color);
            network.ToggleSurface(-Vector3i.UnitZ, BlockFace.PosY, color);
        }
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        Assert.Equal(24, CountLayerVertices(mesh, 21));
    }

    [Fact]
    public void Ctverbarevne_tecko_dostane_prave_jednu_krabicku()
    {
        var network = new PowerNetwork();
        foreach (CableColor color in Enum.GetValues<CableColor>())
        {
            network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, color);
            network.ToggleSurface(Vector3i.UnitX, BlockFace.PosY, color);
            network.ToggleSurface(-Vector3i.UnitX, BlockFace.PosY, color);
            network.ToggleSurface(Vector3i.UnitZ, BlockFace.PosY, color);
        }
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        Assert.Equal(24, CountLayerVertices(mesh, 21));
    }

    [Fact]
    public void Dva_asymetricke_protinajici_se_kabely_dostanou_krabicku()
    {
        var network = new PowerNetwork();
        network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, CableColor.Red);
        network.ToggleSurface(Vector3i.UnitX, BlockFace.PosY, CableColor.Red);
        network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, CableColor.Blue);
        network.ToggleSurface(-Vector3i.UnitZ, BlockFace.PosY, CableColor.Blue);
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        Assert.Equal(24, CountLayerVertices(mesh, 21));
    }

    [Fact]
    public void Paralelni_kabely_krabicku_nedostanou()
    {
        var network = new PowerNetwork();
        AddStraight(network, CableColor.Red, Vector3i.UnitX);
        AddStraight(network, CableColor.Blue, Vector3i.UnitX);
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        Assert.False(HasLayer(mesh, 21));
    }

    [Fact]
    public void Jediny_zatacejici_kabel_krabicku_nedostane()
    {
        var network = new PowerNetwork();
        network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, CableColor.Yellow);
        network.ToggleSurface(Vector3i.UnitX, BlockFace.PosY, CableColor.Yellow);
        network.ToggleSurface(Vector3i.UnitZ, BlockFace.PosY, CableColor.Yellow);
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        Assert.False(HasLayer(mesh, 21));
    }

    [Fact]
    public void Vicebarevny_ohyb_pres_hranu_krabicku_nedostane()
    {
        var network = new PowerNetwork();
        foreach (CableColor color in new[] { CableColor.Red, CableColor.Blue })
        {
            network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, color);
            network.ToggleSurface(-Vector3i.UnitX, BlockFace.PosX, color);
        }
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        Assert.False(HasLayer(mesh, 21));
    }

    [Fact]
    public void Vicebarevna_zatacka_na_plose_krabicku_nedostane()
    {
        var network = new PowerNetwork();
        foreach (CableColor color in new[] { CableColor.Red, CableColor.Blue })
        {
            network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, color);
            network.ToggleSurface(Vector3i.UnitX, BlockFace.PosY, color);
            network.ToggleSurface(Vector3i.UnitZ, BlockFace.PosY, color);
        }
        var mesh = new Tesseris.Game.World.MeshBuffer();

        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        Assert.False(HasLayer(mesh, 21));
    }

    [Fact]
    public void Sit_se_ulozi_a_nacte_beze_ztraty()
    {
        string folder = Path.Combine(Path.GetTempPath(), "tesseris-power-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, PowerNetwork.FileName);

        var saved = new PowerNetwork();
        saved.ToggleSurface(new Vector3i(2, 3, 4), BlockFace.NegX, CableColor.Green);
        saved.TryAddOverhead(
            new Vector3i(1, 2, 3), new Vector3i(8, 4, 3),
            PowerTerminalKind.Relay, PowerTerminalKind.Transformer);
        saved.Save(path);

        var loaded = new PowerNetwork();
        Assert.True(loaded.Load(path));
        Assert.Equal(saved.Surface, loaded.Surface);
        Assert.Equal(saved.Overhead, loaded.Overhead);

        Directory.Delete(folder, recursive: true);
    }

    private static void AssertLaneSeparation(Vector3i direction, int coordinate)
    {
        var network = new PowerNetwork();
        foreach (CableColor color in new[] { CableColor.Red, CableColor.Blue })
        {
            network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, color);
            network.ToggleSurface(direction, BlockFace.PosY, color);
        }

        var mesh = new Tesseris.Game.World.MeshBuffer();
        PowerMeshBuilder.Build(network, mesh, [10, 11, 12, 13], 20, 21);

        float red = MeanCoordinate(mesh, layer: 10, coordinate: coordinate);
        float blue = MeanCoordinate(mesh, layer: 11, coordinate: coordinate);
        Assert.True(MathF.Abs(red - blue) > 0.08f, $"Dráhy se překryly: {red} vs {blue}.");
    }

    private static void AddStraight(PowerNetwork network, CableColor color, Vector3i direction)
    {
        network.ToggleSurface(-direction, BlockFace.PosY, color);
        network.ToggleSurface(Vector3i.Zero, BlockFace.PosY, color);
        network.ToggleSurface(direction, BlockFace.PosY, color);
    }

    private static float MeanCoordinate(Tesseris.Game.World.MeshBuffer mesh, int layer, int coordinate)
    {
        ReadOnlySpan<float> vertices = mesh.Vertices;
        float sum = 0f;
        int count = 0;

        for (int offset = 0; offset < vertices.Length; offset += Tesseris.Game.World.MeshBuffer.FloatsPerVertex)
        {
            if ((int)vertices[offset + 5] != layer)
            {
                continue;
            }

            sum += vertices[offset + coordinate];
            count++;
        }

        Assert.True(count > 0);
        return sum / count;
    }

    private static float MaxCoordinate(Tesseris.Game.World.MeshBuffer mesh, int layer, int coordinate)
    {
        ReadOnlySpan<float> vertices = mesh.Vertices;
        float maximum = float.NegativeInfinity;

        for (int offset = 0; offset < vertices.Length; offset += Tesseris.Game.World.MeshBuffer.FloatsPerVertex)
        {
            if ((int)vertices[offset + 5] == layer)
            {
                maximum = MathF.Max(maximum, vertices[offset + coordinate]);
            }
        }

        Assert.True(float.IsFinite(maximum));
        return maximum;
    }

    private static bool HasLayer(Tesseris.Game.World.MeshBuffer mesh, int layer)
    {
        ReadOnlySpan<float> vertices = mesh.Vertices;
        for (int offset = 0; offset < vertices.Length; offset += Tesseris.Game.World.MeshBuffer.FloatsPerVertex)
        {
            if ((int)vertices[offset + 5] == layer)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountLayerVertices(Tesseris.Game.World.MeshBuffer mesh, int layer)
    {
        ReadOnlySpan<float> vertices = mesh.Vertices;
        int count = 0;
        for (int offset = 0; offset < vertices.Length; offset += Tesseris.Game.World.MeshBuffer.FloatsPerVertex)
        {
            if ((int)vertices[offset + 5] == layer)
            {
                count++;
            }
        }

        return count;
    }
}
