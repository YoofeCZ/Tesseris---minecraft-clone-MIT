using OpenTK.Mathematics;
using Tesseris.Engine.Rendering;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// Čtení ručních modelů z Blockbenche.
/// </summary>
/// <remarks>
/// <para>Formát má několik míst, kde se dá naletět, a všechna jsou tichá: souřadnice
/// v textuře jsou v <b>šestnáctinách</b> bez ohledu na její rozlišení, model smí sahat
/// <b>ven z bloku</b> a kvádr se smí <b>natočit</b>. Chyba v kterémkoli z toho se projeví
/// jen tím, že model vypadá divně — hledá se pak v geometrii místo v převodu.</para>
///
/// <para><b>Testuje se na skutečných souborech</b> ve <c>assets/models</c>, a pokud možno
/// na všech naráz. Testy přišpendlené na jeden konkrétní model se rozsypou, jakmile ho
/// někdo vymění — což se při první výměně sad taky stalo.</para>
/// </remarks>
public sealed class ItemModelFileTests
{
    private const float Half = 0.22f;

    private static string ModelsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "assets", "models");

    private static string ModelPath(string name) => Path.Combine(ModelsDirectory, name + ".json");

    /// <summary>Všechny modely, které ve hře opravdu jsou. Prázdná složka je chyba testu.</summary>
    public static TheoryData<string> AllModels()
    {
        var data = new TheoryData<string>();

        foreach (string path in Directory.GetFiles(ModelsDirectory, "*.json"))
        {
            data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    private static ItemModelFile Load(string name)
    {
        ItemModelFile? model = ItemModelFile.TryLoad(ModelPath(name), Half);

        Assert.NotNull(model);
        return model;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(ItemShape shape)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (ItemShape.Face face in shape.Faces)
        {
            foreach (Vector3 point in new[] { face.P0, face.P1, face.P2, face.P3 })
            {
                min = Vector3.ComponentMin(min, point);
                max = Vector3.ComponentMax(max, point);
            }
        }

        return (min, max);
    }

    /// <summary>Každý model ve složce se přečte a má z čeho se kreslit.</summary>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void Model_se_precte(string name)
    {
        ItemModelFile model = Load(name);

        Assert.Equal(name, model.Name);
        Assert.NotEmpty(model.Shape.Faces);
        Assert.True(model.Bands >= 1, "Model nemá jedinou vrstvu textury.");
    }

    /// <summary>
    /// Model se vejde do svých mezí.
    /// </summary>
    /// <remarks>
    /// Modely sahají ven z bloku — nářadí bývá vyšší než 16 jednotek a některé kvádry mají
    /// zápornou souřadnici. Bez zmenšení by hráč držel v ruce něco většího než blok,
    /// na který sahá. Rozsah se přitom musí počítat až z NATOČENÝCH rohů: kvádr otočený
    /// o 45° zabírá po úhlopříčce víc než jeho zadání.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void Model_se_vejde_do_svych_mezi(string name)
    {
        foreach (ItemShape.Face face in Load(name).Shape.Faces)
        {
            foreach (Vector3 point in new[] { face.P0, face.P1, face.P2, face.P3 })
            {
                Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z));

                Assert.InRange(point.X, -Half - 1e-3f, Half + 1e-3f);
                Assert.InRange(point.Y, -Half - 1e-3f, Half + 1e-3f);
                Assert.InRange(point.Z, -Half - 1e-3f, Half + 1e-3f);
            }
        }
    }

    /// <summary>
    /// Model je vystředěný kolem počátku.
    /// </summary>
    /// <remarks>
    /// Formát počítá souřadnice od rohu. Kdyby se střed nepřesunul, točil by se předmět
    /// na zemi kolem svého rohu a v ruce by visel mimo dlaň.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void Model_je_vystredeny(string name)
    {
        (Vector3 min, Vector3 max) = Bounds(Load(name).Shape);

        Vector3 centre = (min + max) * 0.5f;

        Assert.Equal(0f, centre.X, 3);
        Assert.Equal(0f, centre.Y, 3);
        Assert.Equal(0f, centre.Z, 3);
    }

    /// <summary>
    /// Nejdelší strana sedí přesně na zadanou velikost.
    /// </summary>
    /// <remarks>
    /// Zmenšuje se podle nejdelší strany, takže právě ta musí vyjít na dvojnásobek poloviny.
    /// Kdyby se škálovalo podle jiné osy, byl by předmět podle natočení pokaždé jinak velký.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void Nejdelsi_strana_sedi_na_zadanou_velikost(string name)
    {
        (Vector3 min, Vector3 max) = Bounds(Load(name).Shape);

        Vector3 size = max - min;

        Assert.Equal(Half * 2f, MathF.Max(size.X, MathF.Max(size.Y, size.Z)), 3);
    }

    /// <summary>
    /// Souřadnice v textuře leží uvnitř dlaždice.
    /// </summary>
    /// <remarks>
    /// Přepočítávají se ze šestnáctin přes skutečný rozměr obrázku. Kdyby se ten mezikrok
    /// vynechal, ležely by u větší textury všechny stěny namačkané v rohu — model by se
    /// nakreslil, jen by měl úplně jinou texturu.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void Souradnice_v_texture_lezi_uvnitr_dlazdice(string name)
    {
        foreach (ItemShape.Face face in Load(name).Shape.Faces)
        {
            foreach (Vector2 uv in new[] { face.U0, face.U1, face.U2, face.U3 })
            {
                Assert.InRange(uv.X, -1e-3f, 1f + 1e-3f);
                Assert.InRange(uv.Y, -1e-3f, 1f + 1e-3f);
            }
        }
    }

    /// <summary>Textura, na kterou se model odkazuje, opravdu leží ve složce textur.</summary>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void Textura_modelu_existuje(string name)
    {
        foreach (string layer in Load(name).LayerNames())
        {
            string file = layer[..layer.IndexOf('#', StringComparison.Ordinal)];
            string path = Path.Combine(TextureArray.TextureFolder, file + ".png");

            Assert.True(File.Exists(path), $"Model '{name}' chce texturu, která chybí: {path}");
        }
    }

    /// <summary>
    /// Klíč `particle` nezakládá vrstvu navíc.
    /// </summary>
    /// <remarks>
    /// Je to barva odletujících kousků, ne textura geometrie, a ukazuje na týž soubor —
    /// vrstva navíc by znamenala tutéž texturu v atlasu dvakrát. Všechny čtyři nástroje ho
    /// v souboru mají, takže by se to jinak projevilo na každém z nich.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void Particle_nezaklada_vrstvu_navic(string name)
    {
        ItemModelFile model = Load(name);

        Assert.Equal(model.Bands, model.LayerNames().Distinct().Count());
    }

    /// <summary>
    /// Natočený kvádr se opravdu natočí.
    /// </summary>
    /// <remarks>
    /// <para>Krumpáč má kvádry pod −45°, −22,5°, 22,5° i 45°. Bez natočení by z nich byly
    /// kvádry zarovnané s osami — model by se nakreslil, jen by vypadal jinak, než modelář
    /// v Blockbenchi viděl.</para>
    ///
    /// <para>Pozná se to na tom, že stěna neleží v rovině kolmé k ose: u zarovnaného kvádru
    /// mají všechny čtyři rohy stěny jednu souřadnici stejnou.</para>
    /// </remarks>
    [Theory]
    [InlineData("pickaxe")]
    [InlineData("axe")]
    [InlineData("shovel")]
    [InlineData("sword")]
    public void Natoceny_kvadr_se_opravdu_natoci(string name)
    {
        bool tilted = false;

        foreach (ItemShape.Face face in Load(name).Shape.Faces)
        {
            if (!Same(face.P0.X, face.P1.X, face.P2.X, face.P3.X)
                && !Same(face.P0.Y, face.P1.Y, face.P2.Y, face.P3.Y)
                && !Same(face.P0.Z, face.P1.Z, face.P2.Z, face.P3.Z))
            {
                tilted = true;
                break;
            }
        }

        Assert.True(tilted, $"Model '{name}' nemá jedinou natočenou stěnu — rotace se ignoruje.");

        static bool Same(float a, float b, float c, float d) =>
            MathF.Abs(a - b) < 1e-4f && MathF.Abs(a - c) < 1e-4f && MathF.Abs(a - d) < 1e-4f;
    }

    /// <summary>
    /// Žádné dvě stěny neleží přesně na sobě.
    /// </summary>
    /// <remarks>
    /// <para><b>Tohle bylo to blikání.</b> Modely nářadí mají díly s nulovou tloušťkou —
    /// ostří, hroty, plotny. Přední a zadní stěna takového dílu leží přesně na sobě a každá
    /// nese jiný kus textury, takže se o každý pixel perou a kresba mezi nimi přeskakuje.
    /// Projevilo se to jen na nástrojích: ruka ani blok plochý díl nemají.</para>
    ///
    /// <para>Porovnává se střed stěny a její normála: dvě stěny se stejným středem jsou
    /// na sobě bez ohledu na to, jak mají zadané rohy.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllModels))]
    public void Zadne_dve_steny_nelezi_na_sobe(string name)
    {
        ItemShape.Face[] faces = Load(name).Shape.Faces;

        var centres = new List<Vector3>();

        foreach (ItemShape.Face face in faces)
        {
            Vector3 centre = (face.P0 + face.P1 + face.P2 + face.P3) * 0.25f;

            foreach (Vector3 other in centres)
            {
                Assert.False(
                    (centre - other).LengthSquared < 1e-10f,
                    $"Model '{name}' má dvě stěny na sobě u {centre} — budou se prát o hloubku.");
            }

            centres.Add(centre);
        }
    }

    /// <summary>Dosazení vrstev atlasu přepíše čísla pruhů.</summary>
    [Fact]
    public void Dosazeni_vrstev_prepise_pruhy()
    {
        ItemModelFile model = Load("axe");

        model.Resolve(_ => 77);

        Assert.All(model.Shape.Faces, face => Assert.Equal(77, face.Layer));
    }

    /// <summary>Ve složce jsou všechny čtyři nástroje.</summary>
    [Theory]
    [InlineData("axe")]
    [InlineData("pickaxe")]
    [InlineData("shovel")]
    [InlineData("sword")]
    public void Slozka_obsahuje_naradi(string name)
    {
        List<ItemModelFile> models = ItemModelFile.LoadAll(ModelsDirectory, Half);

        Assert.Contains(models, m => m.Name == name);
    }

    /// <summary>Chybějící soubor nespadne, jen nic nevrátí.</summary>
    [Fact]
    public void Chybejici_soubor_nespadne()
    {
        Assert.Null(ItemModelFile.TryLoad(ModelPath("tenhle-model-neexistuje"), Half));
    }

    /// <summary>Prázdná složka není chyba — hra se dá hrát i bez ručních modelů.</summary>
    [Fact]
    public void Prazdna_slozka_neni_chyba()
    {
        Assert.Empty(ItemModelFile.LoadAll(Path.Combine(AppContext.BaseDirectory, "nic-tu-neni"), Half));
    }
}
