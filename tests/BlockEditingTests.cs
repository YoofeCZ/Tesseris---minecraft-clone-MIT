using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Player;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Testy úpravy bloků a hotbaru.
///
/// Nejdůležitější je, že úprava nemění zveřejněný chunk, ale vyrábí jeho kopii — na tom stojí
/// bezpečnost souběžného meshingu.
/// </summary>
public sealed class BlockEditingTests
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
        new BlockDefinition { Id = "test:dirt", Texture = "dirt", Opaque = true },
        new BlockDefinition { Id = "test:glass", Texture = "glass", Opaque = false },
    ]);

    [Fact]
    public void Uprava_nechava_puvodni_chunk_beze_zmeny()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        ushort dirt = world.Registry.IndexOf("test:dirt");

        world.SetBlock(5, 5, 5, stone);

        // Takhle drží odkaz meshovací úloha, která začala před úpravou.
        Chunk snapshot = world.GetChunk(Vector3i.Zero)!;
        Assert.Equal(stone, snapshot.GetBlock(5, 5, 5));

        world.SetBlock(5, 5, 5, dirt);

        // Rozpracovaná úloha musí dál vidět původní obsah.
        Assert.Equal(stone, snapshot.GetBlock(5, 5, 5));

        // Svět už vrací nový.
        Assert.Equal(dirt, world.GetBlock(5, 5, 5));
        Assert.NotSame(snapshot, world.GetChunk(Vector3i.Zero));
    }

    [Fact]
    public void Zapis_stejne_hodnoty_chunk_nevymeni()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        world.SetBlock(1, 2, 3, stone);
        Chunk before = world.GetChunk(Vector3i.Zero)!;

        world.SetBlock(1, 2, 3, stone);

        // Zbytečná kopie by při držení tlačítka vyráběla odpad na každý frame.
        Assert.Same(before, world.GetChunk(Vector3i.Zero));
    }

    [Fact]
    public void Uprava_vraci_souradnici_dotceneho_chunku()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        Assert.Equal(new Vector3i(0, 0, 0), world.SetBlock(5, 5, 5, stone));
        Assert.Equal(new Vector3i(1, 0, 0), world.SetBlock(32, 5, 5, stone));
        Assert.Equal(new Vector3i(-1, -1, -1), world.SetBlock(-1, -1, -1, stone));
    }

    [Fact]
    public void Kopie_chunku_je_nezavisla()
    {
        var original = new Chunk();
        original.SetBlock(1, 1, 1, 5);
        original.SetBlock(2, 2, 2, 7);

        Chunk copy = original.Clone();

        Assert.Equal(5, copy.GetBlock(1, 1, 1));
        Assert.Equal(7, copy.GetBlock(2, 2, 2));

        copy.SetBlock(1, 1, 1, 9);

        Assert.Equal(5, original.GetBlock(1, 1, 1));
        Assert.Equal(9, copy.GetBlock(1, 1, 1));
    }

    [Fact]
    public void Kopie_homogenniho_chunku_zustane_homogenni()
    {
        var original = new Chunk(fill: 3);
        Chunk copy = original.Clone();

        Assert.True(copy.IsHomogeneous);
        Assert.Equal(3, copy.HomogeneousBlock);

        copy.SetBlock(0, 0, 0, 8);

        Assert.True(original.IsHomogeneous);
        Assert.False(copy.IsHomogeneous);
    }

    [Fact]
    public void Kopie_zachova_sirku_indexu_i_paletu()
    {
        var original = new Chunk();
        for (int i = 0; i < 20; i++)
        {
            original.SetBlock(i, 0, 0, (ushort)(i + 1));
        }

        Chunk copy = original.Clone();

        Assert.Equal(original.BitsPerIndex, copy.BitsPerIndex);
        Assert.Equal(original.PaletteCount, copy.PaletteCount);

        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(original.GetBlock(i, 0, 0), copy.GetBlock(i, 0, 0));
        }
    }

    [Fact]
    public void Hotbar_nabidne_vsechny_bloky_krome_vzduchu()
    {
        BlockRegistry registry = Registry();
        var hotbar = new Hotbar(registry);

        // Kbelík na prvním místě, za ním bloky bez vzduchu. Kbelík není blok - vodu nejde
        // vzít ani položit rukou, takže je jediná cesta, jak ji přenést.
        Assert.Equal(registry.Count, hotbar.SlotCount);

        for (int i = 1; i < hotbar.SlotCount; i++)
        {
            Assert.NotEqual(BlockRegistry.Air, hotbar.BlockAt(i));
        }

        hotbar.Select(Hotbar.BucketSlot);
        Assert.True(hotbar.BucketSelected);
        Assert.False(hotbar.BucketFull);
    }

    [Fact]
    public void Hotbar_vybere_slot_a_mimo_rozsah_ignoruje()
    {
        var hotbar = new Hotbar(Registry());

        hotbar.Select(2);
        Assert.Equal(2, hotbar.SelectedIndex);

        hotbar.Select(99);
        Assert.Equal(2, hotbar.SelectedIndex);

        hotbar.Select(-1);
        Assert.Equal(2, hotbar.SelectedIndex);
    }

    [Fact]
    public void Hotbar_se_pri_rolovani_pretoci()
    {
        var hotbar = new Hotbar(Registry());
        int slots = hotbar.SlotCount;

        hotbar.Select(0);
        hotbar.Scroll(-1);
        Assert.Equal(slots - 1, hotbar.SelectedIndex);

        hotbar.Scroll(1);
        Assert.Equal(0, hotbar.SelectedIndex);

        hotbar.Scroll(slots);
        Assert.Equal(0, hotbar.SelectedIndex);
    }

    [Fact]
    public void Kbelik_se_naplni_a_vyprazdni()
    {
        var hotbar = new Hotbar(Registry());
        hotbar.Select(Hotbar.BucketSlot);

        Assert.True(hotbar.BucketSelected);
        Assert.Equal("prázdný kbelík", hotbar.SelectedName);

        hotbar.FillBucket();
        Assert.True(hotbar.BucketFull);
        Assert.Equal("kbelík s vodou", hotbar.SelectedName);

        hotbar.EmptyBucket();
        Assert.False(hotbar.BucketFull);
    }

    [Fact]
    public void Hotbar_zna_jmeno_vybraneho_bloku_bez_jmenneho_prostoru()
    {
        var hotbar = new Hotbar(Registry());

        // Slot nula je kbelík, bloky začínají za ním. Registry řadí abecedně, takže první
        // blok je test:dirt.
        hotbar.Select(1);

        Assert.Equal("dirt", hotbar.SelectedName);
        Assert.DoesNotContain(":", hotbar.SelectedName, StringComparison.Ordinal);
    }

    [Fact]
    public void Souvisly_pohled_na_chunk_prezije_stovky_uprav()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");
        ushort dirt = world.Registry.IndexOf("test:dirt");

        world.SetBlock(0, 0, 0, stone);
        Chunk snapshot = world.GetChunk(Vector3i.Zero)!;

        // Simuluje meshovací úlohu, která si vzala odkaz a pak dlouho čte, zatímco
        // hlavní vlákno mění blok za blokem.
        for (int i = 1; i < 300; i++)
        {
            world.SetBlock(i % Chunk.Size, (i / Chunk.Size) % Chunk.Size, i % 7, i % 2 == 0 ? stone : dirt);

            // Původní snímek se nesmí hnout ani při růstu palety.
            Assert.Equal(stone, snapshot.GetBlock(0, 0, 0));
        }
    }
}
