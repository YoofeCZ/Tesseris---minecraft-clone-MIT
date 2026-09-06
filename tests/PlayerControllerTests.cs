using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Player;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy fyziky hráče. Fyzika nesahá na GL ani na vstup, takže se dá celá odsimulovat
/// proti ručně postavenému světu.
/// </summary>
public sealed class PlayerControllerTests
{
    private const float Step = 1f / 60f;

    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    /// <summary>Rovná podlaha na y = 0, tedy povrch ve výšce 1.</summary>
    private static VoxelWorld FlatWorld(int extent = 24)
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = -extent; x <= extent; x++)
        {
            for (int z = -extent; z <= extent; z++)
            {
                world.SetBlock(x, 0, z, stone);
            }
        }

        return world;
    }

    /// <summary>
    /// Podlaha s tunelem od x = 2 do x = 6. Jeho strop tvoří horní půlky bloků,
    /// takže mezi podlahou a stropem zbývá přesně 1,5 bloku.
    /// </summary>
    private static VoxelWorld LowTunnelWorld()
    {
        VoxelWorld world = FlatWorld();
        ushort stone = world.Registry.IndexOf("test:stone");
        byte upperHalf = PieceMask.Empty;

        for (int z = 0; z <= 1; z++)
        {
            for (int x = 2; x <= 5; x++)
            {
                world.SetBlock(x, 2, z, stone);
            }
        }

        for (int k = 0; k < PieceMask.Steps; k++)
        {
            for (int i = 0; i < PieceMask.Steps; i++)
            {
                upperHalf = PieceMask.With(upperHalf, i, 1, k);
            }
        }

        for (int z = 0; z <= 1; z++)
        {
            for (int x = 2; x <= 5; x++)
            {
                Chunk chunk = world.GetChunk(VoxelWorld.ToChunkPosition(x, 2, z))!;
                chunk.SetPieces(x & Chunk.SizeMask, 2 & Chunk.SizeMask, z & Chunk.SizeMask, upperHalf);
            }
        }

        return world;
    }

    private static void Simulate(PlayerController player, VoxelWorld world, int frames,
        Vector3 wish = default, bool jump = false, bool sprint = false, bool crouch = false)
    {
        for (int i = 0; i < frames; i++)
        {
            player.Update(world, wish, jump, sprint, 0f, Step, crouch);
        }
    }

    [Fact]
    public void Hrac_dopadne_na_podlahu_a_zastavi_se()
    {
        VoxelWorld world = FlatWorld();
        var player = new PlayerController(new Vector3(0.5f, 12f, 0.5f));

        Simulate(player, world, 180);

        Assert.True(player.OnGround, "Hráč po dopadu nestojí na zemi.");
        Assert.Equal(1f, player.Position.Y, 2);
        Assert.Equal(0f, player.Velocity.Y, 3);
    }

    [Fact]
    public void Stojici_hrac_se_nepropada()
    {
        VoxelWorld world = FlatWorld();
        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        Simulate(player, world, 600);

        Assert.Equal(1f, player.Position.Y, 2);
        Assert.True(player.OnGround);
    }

    [Fact]
    public void Chuze_dopredu_hrace_posune()
    {
        VoxelWorld world = FlatWorld();
        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        Simulate(player, world, 60, wish: new Vector3(1f, 0f, 0f));

        // Za sekundu chůze má ujít zhruba rychlost chůze.
        Assert.InRange(player.Position.X - 0.5f, PlayerController.WalkSpeed * 0.8f, PlayerController.WalkSpeed * 1.2f);
        Assert.Equal(1f, player.Position.Y, 2);
    }

    [Fact]
    public void Sprint_je_rychlejsi_nez_chuze()
    {
        VoxelWorld world = FlatWorld(60);

        var walker = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        var runner = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        Simulate(walker, world, 60, wish: new Vector3(1f, 0f, 0f));
        Simulate(runner, world, 60, wish: new Vector3(1f, 0f, 0f), sprint: true);

        Assert.True(runner.Position.X > walker.Position.X * 1.5f);
    }

    [Fact]
    public void Crouch_zpomali_ale_nezastavi_chuzi()
    {
        VoxelWorld world = FlatWorld(60);

        var walker = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        var croucher = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        Simulate(walker, world, 1);
        Simulate(croucher, world, 1, crouch: true);
        Simulate(walker, world, 60, wish: Vector3.UnitX);
        Simulate(croucher, world, 60, wish: Vector3.UnitX, crouch: true);

        float walked = walker.Position.X - 0.5f;
        float crouched = croucher.Position.X - 0.5f;

        Assert.True(crouched > 0.5f, "Crouch hráče úplně zastavil.");
        Assert.Equal(walked * PlayerController.CrouchSpeedMultiplier, crouched, 1);
        Assert.True(croucher.Crouching);
        Assert.Equal(
            croucher.Position.Y + croucher.CurrentEyeHeight,
            croucher.EyePosition.Y,
            3);
    }

    [Fact]
    public void Kamera_prechazi_do_crouche_a_zpet_plynule()
    {
        VoxelWorld world = FlatWorld();
        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        Simulate(player, world, 1);
        player.Update(world, Vector3.Zero, false, false, 0f, Step, crouch: true);

        Assert.InRange(
            player.CurrentEyeHeight,
            PlayerController.CrouchEyeHeight + 0.01f,
            PlayerController.EyeHeight - 0.01f);

        Simulate(player, world, 60, crouch: true);
        Assert.Equal(PlayerController.CrouchEyeHeight, player.CurrentEyeHeight, 3);

        player.Update(world, Vector3.Zero, false, false, 0f, Step, crouch: false);

        Assert.InRange(
            player.CurrentEyeHeight,
            PlayerController.CrouchEyeHeight + 0.01f,
            PlayerController.EyeHeight - 0.01f);

        Simulate(player, world, 60);
        Assert.Equal(PlayerController.EyeHeight, player.CurrentEyeHeight, 3);
    }

    [Fact]
    public void Crouch_dovoli_projit_tunelem_vysokym_jeden_a_pul_bloku()
    {
        VoxelWorld world = LowTunnelWorld();
        var croucher = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        var standing = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        Simulate(croucher, world, 150, wish: Vector3.UnitX, crouch: true);
        Simulate(standing, world, 150, wish: Vector3.UnitX);

        Assert.True(croucher.Position.X > 4f, $"Skrčený hráč zůstal před tunelem na x = {croucher.Position.X:F2}.");
        Assert.True(standing.Position.X < 1.71f, $"Stojící hráč prošel nízkým tunelem na x = {standing.Position.X:F2}.");
        Assert.True(croucher.Crouching);
        Assert.Equal(PlayerController.CrouchHeight, croucher.Bounds.Size.Y, 3);
    }

    [Fact]
    public void Pusteni_crouche_pod_nizkym_stropem_hrace_nenarovna()
    {
        VoxelWorld world = LowTunnelWorld();
        var player = new PlayerController(new Vector3(3.5f, 1f, 0.5f));

        Simulate(player, world, 10, crouch: true);
        player.Update(world, Vector3.Zero, false, false, 0f, Step, crouch: false);

        Assert.False(PlayerController.IsFree(world, PlayerController.BoundsAt(player.Position)));
        Assert.True(player.Crouching, "Hráč se pod stropem pokusil narovnat.");
        Assert.Equal(PlayerController.CrouchHeight, player.Bounds.Size.Y, 3);
    }

    [Fact]
    public void Po_opusteni_nizkeho_tunelu_se_hrac_sam_narovna()
    {
        VoxelWorld world = LowTunnelWorld();
        var player = new PlayerController(new Vector3(3.5f, 1f, 0.5f));

        Simulate(player, world, 10, crouch: true);
        Simulate(player, world, 180, wish: Vector3.UnitX, crouch: false);

        Assert.True(player.Position.X > 6.3f, $"Hráč neopustil tunel, x = {player.Position.X:F2}.");
        Assert.False(player.Crouching, "Hráč zůstal skrčený i mimo tunel.");
        Assert.Equal(PlayerController.Height, player.Bounds.Size.Y, 3);
        Assert.Equal(PlayerController.EyeHeight, player.CurrentEyeHeight, 3);
    }

    [Fact]
    public void Crouch_nepusti_hrace_pres_hranu_bloku()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        world.SetBlock(0, 0, 0, stone);

        var croucher = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        var walker = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        // První snímek zjistí podlahu; ochrana hrany platí jen hráči, který na ní opravdu stojí.
        Simulate(croucher, world, 1, crouch: true);
        Simulate(walker, world, 1);

        Simulate(croucher, world, 600, wish: Vector3.UnitX, crouch: true);
        Simulate(walker, world, 180, wish: Vector3.UnitX);

        Assert.True(croucher.OnGround, "Crouch dovolil pád z hrany.");
        Assert.Equal(1f, croucher.Position.Y, 2);
        Assert.InRange(croucher.Position.X, 1.08f, 1.14f);
        Assert.True(walker.Position.Y < 0f, "Běžná chůze byla omylem také přilepená k hraně.");
    }

    [Fact]
    public void Crouch_udrzi_i_roh_jedineho_bloku()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        world.SetBlock(0, 0, 0, stone);

        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        Vector3 diagonal = Vector3.Normalize(Vector3.UnitX + Vector3.UnitZ);

        Simulate(player, world, 1, crouch: true);
        Simulate(player, world, 600, wish: diagonal, crouch: true);

        Assert.True(player.OnGround, "Crouch sklouzl z rohu bloku.");
        Assert.Equal(1f, player.Position.Y, 2);
        Assert.InRange(player.Position.X, 1.08f, 1.14f);
        Assert.InRange(player.Position.Z, 1.08f, 1.14f);
    }

    [Fact]
    public void Chuze_do_rohu_neni_rychlejsi_nez_rovne()
    {
        VoxelWorld world = FlatWorld(60);

        var straight = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        var diagonal = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        Simulate(straight, world, 60, wish: new Vector3(1f, 0f, 0f));
        Simulate(diagonal, world, 60, wish: new Vector3(1f, 0f, 1f));

        float straightDistance = straight.Position.X - 0.5f;
        float diagonalDistance = (diagonal.Position - new Vector3(0.5f, 1f, 0.5f)).Length;

        Assert.Equal(straightDistance, diagonalDistance, 1);
    }

    [Fact]
    public void Zed_hrace_zastavi()
    {
        VoxelWorld world = FlatWorld();
        ushort stone = world.Registry.IndexOf("test:stone");

        // Zeď vysoká tři bloky na x = 5, tedy mimo dosah step-upu.
        for (int y = 1; y <= 3; y++)
        {
            for (int z = -5; z <= 5; z++)
            {
                world.SetBlock(5, y, z, stone);
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        Simulate(player, world, 180, wish: new Vector3(1f, 0f, 0f));

        // Hráč má poloviční šířku 0,3, takže se zastaví těsně před x = 5.
        Assert.InRange(player.Position.X, 4.5f, 4.71f);
    }

    [Fact]
    public void Plny_blok_se_bez_skoku_nevyjde()
    {
        VoxelWorld world = FlatWorld();
        ushort stone = world.Registry.IndexOf("test:stone");

        // Schod vysoký celý blok: povrch stoupá z y = 1 na y = 2.
        for (int x = 5; x <= 12; x++)
        {
            for (int z = -5; z <= 5; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        Simulate(player, world, 180, wish: new Vector3(1f, 0f, 0f));

        // Step-up je 0,6, blok má 1,0 — sám od sebe se na něj hráč nedostane.
        // Stejně se chová Minecraft: plný blok se přeskakuje, vyjít jde jen půlblok.
        Assert.InRange(player.Position.X, 4.5f, 4.71f);
        Assert.Equal(1f, player.Position.Y, 2);
    }

    [Fact]
    public void Se_skokem_hrac_na_blok_vystoupi()
    {
        VoxelWorld world = FlatWorld();
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 5; x <= 12; x++)
        {
            for (int z = -5; z <= 5; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        // Plošina musí sahat až na okraj světa. Kratší by ji hráč za dobu testu přeběhl
        // a spadl by z druhého konce, takže by se výška měřila zase dole.
        for (int x = 13; x <= 24; x++)
        {
            for (int z = -5; z <= 5; z++)
            {
                world.SetBlock(x, 1, z, stone);
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        // Držený skok i pohyb vpřed: jakmile je hráč na zemi, odrazí se.
        for (int i = 0; i < 300; i++)
        {
            player.Update(world, new Vector3(1f, 0f, 0f), jump: true, sprint: false, 0f, Step);
        }

        Assert.True(player.Position.X > 6f, $"Hráč zůstal na x = {player.Position.X:F2}.");

        // Skok se pustí a počká se na dopad — jinak by se výška měřila uprostřed odrazu.
        Simulate(player, world, 120);

        Assert.True(player.OnGround);
        Assert.Equal(2f, player.Position.Y, 2);
    }

    [Fact]
    public void Prilis_vysoky_schod_hrac_nevyjde()
    {
        VoxelWorld world = FlatWorld();
        ushort stone = world.Registry.IndexOf("test:stone");

        // Dva bloky, tedy víc než step-up 0,6.
        for (int x = 5; x <= 12; x++)
        {
            for (int z = -5; z <= 5; z++)
            {
                world.SetBlock(x, 1, z, stone);
                world.SetBlock(x, 2, z, stone);
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        Simulate(player, world, 180, wish: new Vector3(1f, 0f, 0f));

        Assert.InRange(player.Position.X, 4.5f, 4.71f);
        Assert.Equal(1f, player.Position.Y, 2);
    }

    [Fact]
    public void Skok_hrace_zvedne_a_vrati_na_zem()
    {
        VoxelWorld world = FlatWorld();
        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        // Ustálit se na zemi.
        Simulate(player, world, 10);
        Assert.True(player.OnGround);

        player.Update(world, Vector3.Zero, jump: true, sprint: false, 0f, Step);
        Assert.False(player.OnGround);

        float highest = player.Position.Y;
        for (int i = 0; i < 120; i++)
        {
            player.Update(world, Vector3.Zero, jump: false, sprint: false, 0f, Step);
            highest = MathF.Max(highest, player.Position.Y);
        }

        Assert.True(highest > 2.0f, $"Skok dosáhl jen {highest:F2}, což je pod jeden blok.");
        Assert.True(player.OnGround, "Hráč se po skoku nevrátil na zem.");
        Assert.Equal(1f, player.Position.Y, 2);
    }

    [Fact]
    public void Ve_vzduchu_se_skakat_neda()
    {
        VoxelWorld world = FlatWorld();
        var player = new PlayerController(new Vector3(0.5f, 8f, 0.5f));

        // Padá, tedy není na zemi — držený skok nesmí nic udělat.
        Simulate(player, world, 5, jump: true);
        Assert.False(player.OnGround);
        Assert.True(player.Velocity.Y < 0f, "Hráč ve vzduchu vyskočil.");
    }

    [Fact]
    public void Strop_hrace_zastavi()
    {
        VoxelWorld world = FlatWorld();
        ushort stone = world.Registry.IndexOf("test:stone");

        // Strop tak nízko, že se do něj skok trefí.
        for (int x = -3; x <= 3; x++)
        {
            for (int z = -3; z <= 3; z++)
            {
                world.SetBlock(x, 3, z, stone);
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));
        Simulate(player, world, 10);

        player.Update(world, Vector3.Zero, jump: true, sprint: false, 0f, Step);
        Simulate(player, world, 30);

        // Hlava hráče je ve výšce 1,8, strop začíná na 3 — výš než 1,2 se nedostane.
        Assert.True(player.Position.Y <= 1.21f, $"Hráč prošel stropem na y = {player.Position.Y:F2}.");
    }

    [Fact]
    public void Vysoka_rychlost_neprotuneluje_tenkou_podlahu()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        // Jediná vrstva bloků. Bez dělení pohybu na podkroky by ji pád z výšky prolétl.
        for (int x = -3; x <= 3; x++)
        {
            for (int z = -3; z <= 3; z++)
            {
                world.SetBlock(x, 0, z, stone);
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 60f, 0.5f));
        Simulate(player, world, 600);

        Assert.True(player.Position.Y >= 0.99f, $"Hráč propadl na y = {player.Position.Y:F2}.");
        Assert.True(player.OnGround);
    }

    [Fact]
    public void Jeden_dlouhy_frame_taky_neprotuneluje()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = -3; x <= 3; x++)
        {
            for (int z = -3; z <= 3; z++)
            {
                world.SetBlock(x, 0, z, stone);
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 40f, 0.5f));

        // Deset framů po půl sekundě: extrémní zadrhnutí.
        for (int i = 0; i < 40; i++)
        {
            player.Update(world, Vector3.Zero, false, false, 0f, 0.5f);
        }

        Assert.True(player.Position.Y >= 0.99f, $"Hráč propadl na y = {player.Position.Y:F2}.");
    }

    [Fact]
    public void Pruchod_zdmi_ignoruje_kolize()
    {
        VoxelWorld world = FlatWorld();
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int y = 1; y <= 6; y++)
        {
            for (int z = -5; z <= 5; z++)
            {
                world.SetBlock(5, y, z, stone);
            }
        }

        var player = new PlayerController(new Vector3(0.5f, 2f, 0.5f)) { NoClip = true };
        Simulate(player, world, 120, wish: new Vector3(1f, 0f, 0f));

        Assert.True(player.Position.X > 6f, "Průchod zdmi neprošel zdí.");
    }

    [Fact]
    public void Oci_jsou_nad_chodidly()
    {
        var player = new PlayerController(new Vector3(1f, 10f, 2f));

        Assert.Equal(new Vector3(1f, 10f + PlayerController.EyeHeight, 2f), player.EyePosition);
    }

    [Fact]
    public void Kvadr_hrace_ma_zadane_rozmery()
    {
        var player = new PlayerController(new Vector3(0f, 0f, 0f));

        Assert.Equal(PlayerController.Width, player.Bounds.Size.X, 4);
        Assert.Equal(PlayerController.Height, player.Bounds.Size.Y, 4);
        Assert.Equal(PlayerController.Width, player.Bounds.Size.Z, 4);
    }

    [Fact]
    public void Blok_pod_nohama_se_za_prekryv_nepovazuje()
    {
        var player = new PlayerController(new Vector3(0.5f, 1f, 0.5f));

        // Podlaha, na které hráč stojí.
        Assert.False(player.Overlaps(new Vector3i(0, 0, 0)));

        // Blok v úrovni nohou už ano.
        Assert.True(player.Overlaps(new Vector3i(0, 1, 0)));

        // Blok v úrovni hlavy taky.
        Assert.True(player.Overlaps(new Vector3i(0, 2, 0)));

        // Blok nad hlavou ne — hráč je vysoký 1,8, takže do y = 3 nedosáhne.
        Assert.False(player.Overlaps(new Vector3i(0, 3, 0)));
    }

    [Fact]
    public void Teleport_vynuluje_rychlost()
    {
        VoxelWorld world = FlatWorld();
        var player = new PlayerController(new Vector3(0.5f, 20f, 0.5f));

        Simulate(player, world, 20);
        Assert.True(player.Velocity.Y < 0f);

        player.Teleport(new Vector3(3f, 5f, 4f));

        Assert.Equal(new Vector3(3f, 5f, 4f), player.Position);
        Assert.Equal(Vector3.Zero, player.Velocity);
    }

    [Fact]
    public void Prazdny_svet_nechá_hrace_padat()
    {
        var world = new VoxelWorld(Registry());
        var player = new PlayerController(new Vector3(0.5f, 50f, 0.5f));

        Simulate(player, world, 60);

        Assert.False(player.OnGround);
        Assert.True(player.Position.Y < 45f);
    }
}
