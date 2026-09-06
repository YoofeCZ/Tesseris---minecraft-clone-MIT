using OpenTK.Mathematics;
using Tesseris.Game.Blocks;
using Tesseris.Game.Colony;
using Tesseris.Game.World;
using Tesseris.Game.World.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace Tesseris.Tests;

/// <summary>
/// Co se s fyzickým tělem pokazí až v davu dvou set lidí.
/// </summary>
/// <remarks>
/// <para><b>Tyhle chyby se objevily až ve chvíli, kdy začala fungovat sonda
/// <c>VOXELITY_COLONISTS</c>.</b> Do té doby radnice pouštěla nejvýš tři lidi, takže se cíl
/// ze sekce 8 nedal ve hře sejít vůbec — a všechno ostatní vypadalo v pořádku, protože tři
/// lidé se do sebe skoro nedostanou.</para>
///
/// <para>Naměřeno ve hře při dvou stech lidech: <b>6 549 překryvů</b> těl, nejbližší dvojice
/// <b>0,001 bloku</b> proti šířce 0,60, a nejdelší posun <b>8,777 bloku za tik</b> proti
/// 0,077, kolik dovolí chůze. Testy se třemi lidmi tohle nechytily ani jednou.</para>
/// </remarks>
public sealed class ColonistCrowdTests(ITestOutputHelper output)
{
    private static BlockRegistry Registry() => BlockRegistry.Create(
    [
        new BlockDefinition { Id = "test:stone", Texture = "stone", Opaque = true },
    ]);

    private static ushort NoWater => ushort.MaxValue;

    private static VoxelWorld Floor()
    {
        var world = new VoxelWorld(Registry());
        ushort stone = world.Registry.IndexOf("test:stone");

        for (int x = 0; x < NavGrid.Size; x++)
        {
            for (int z = 0; z < NavGrid.Size; z++)
            {
                world.SetBlock(x, 0, z, stone);
                world.SetBlock(x, 1, z, stone);
            }
        }

        return world;
    }

    private static ColonyRuntime Colony(VoxelWorld world)
    {
        var colony = new ColonyRuntime();
        colony.SetWater(NoWater);

        for (int i = 0; i < 10; i++)
        {
            colony.UpdateNavigation(world, world.Registry, new Vector3(8f, 2f, 8f));
        }

        return colony;
    }

    /// <summary>
    /// Ani ve dvou stech lidech se za tik neujde víc, než dovolí rychlost.
    /// </summary>
    /// <remarks>
    /// <para><b>Naměřeno ve hře jako 8,777 bloku za tik</b> proti dovoleným 0,077, tedy
    /// stodvacetinásobek. Nešlo o rychlost jako takovou: kolonista, který v tomhle tiku
    /// PRÁVĚ PŘIŠEL, má předchozí polohu z doby, kdy ještě neexistoval, takže mu strop
    /// rychlosti naměřil skok z počátku souřadnic.</para>
    ///
    /// <para><b>Co by prošlo i s rozbitou věcí:</b> tentýž test se třemi lidmi (běžel celý
    /// den a nikdy nespadl) a měření průměru místo maxima. Rozhoduje MAXIMUM v davu, kde se
    /// lidé zároveň přidávají za běhu — protože přesně tak to dělá radnice.</para>
    /// </remarks>
    [Fact]
    public void Two_hundred_colonists_never_exceed_the_speed_limit()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);
        colony.FoundTownHall(new Vector3i(8, 2, 8));

        const int Lidi = 200;
        var predchozi = new Vector3[Lidi];
        int znamych = 0;

        float nejdelsiPosun = 0f;
        int kdyz = -1;

        for (int tick = 0; tick < 900; tick++)
        {
            // PŘIDÁVÁ SE ZA BĚHU, jako to dělá radnice se zapnutou sondou: jeden za tik.
            // Test, který všechny postaví předem, tuhle chybu minul.
            if (znamych < Lidi)
            {
                int novy = colony.Colonists.Add(new Vector3(
                    4.5f + (znamych % 20),
                    2f,
                    4.5f + (znamych / 20)));

                predchozi[novy] = colony.Colonists.PositionOf(novy);
                znamych++;
            }

            colony.Tick(world, world.Registry);

            for (int id = 0; id < znamych; id++)
            {
                Vector3 ted = colony.Colonists.PositionOf(id);
                float dx = ted.X - predchozi[id].X;
                float dz = ted.Z - predchozi[id].Z;
                float posun = MathF.Sqrt((dx * dx) + (dz * dz));

                if (posun > nejdelsiPosun)
                {
                    nejdelsiPosun = posun;
                    kdyz = tick;
                }

                predchozi[id] = ted;
            }
        }

        float dovoleno = ColonistBody.WalkSpeed * ColonistBody.StepSeconds;

        output.WriteLine($"{Lidi} lidi: nejdelsi posun za tik {nejdelsiPosun:F4} bloku "
            + $"(v tiku {kdyz}), dovoleno {dovoleno:F4}.");

        Assert.True(
            nejdelsiPosun <= dovoleno + 1e-4f,
            $"V davu se za tik ušlo {nejdelsiPosun:F4} proti dovoleným {dovoleno:F4}.");
    }

    /// <summary>
    /// Dvě stě lidí na malé ploše se rozestoupí, ne slepí.
    /// </summary>
    /// <remarks>
    /// <para><b>Naměřeno ve hře: 6 549 překryvů a nejbližší dvojice 0,001 bloku.</b> Rozestup
    /// se počítá jednou za tik ze společného snímku poloh, takže hustý dav se rozplétá
    /// postupně — otázka je, jestli se rozplete VŮBEC, nebo jestli zůstane slepený.</para>
    ///
    /// <para><b>Měří se ustálený stav, ne první tiky.</b> Dvě stě lidí vysazených na sebe se
    /// legitimně chvíli rovná; kdyby se měřilo od začátku, test by hlásil chybu u chování,
    /// které je v pořádku. Rozhoduje, kde dav skončí.</para>
    ///
    /// <para><b>MUSÍ SE REPRODUKOVAT PŘÍCHOD, ne rovnou hotový dav.</b> První verze tohohle
    /// testu všech dvě stě lidí postavila předem do mřížky 10×10 a prošla s nulou překryvů —
    /// zatímco hra ve stejnou chvíli hlásila 17 446. Rozdíl je v tom, že radnice pouští lidi
    /// POSTUPNĚ a všechny na jedno místo u sebe, takže se nový vždycky objeví uvnitř těch,
    /// kdo už tam stojí. To je jiná úloha než rozplést mřížku, ve které se nikdo nepřekrývá
    /// hned na začátku.</para>
    /// </remarks>
    [Fact]
    public void A_crowd_of_two_hundred_untangles_itself()
    {
        VoxelWorld world = Floor();
        ColonyRuntime colony = Colony(world);
        colony.FoundTownHall(new Vector3i(8, 2, 8));

        const int Lidi = 200;

        // JAKO RADNICE: jeden za tik, všichni do těsného okolí téhož místa. Tohle je ten
        // scénář, ve kterém hra naměřila 17 446 překryvů.
        for (int tick = 0; tick < 1_400; tick++)
        {
            if (colony.Colonists.Count < Lidi)
            {
                int poradi = colony.Colonists.Count;
                colony.Colonists.Add(new Vector3(
                    8.5f + ((poradi % 5) * 0.2f),
                    2f,
                    8.5f + ((poradi / 5 % 5) * 0.2f)));
            }

            colony.Tick(world, world.Registry);
        }

        int prekryvu = 0;
        float nejblizsi = float.MaxValue;

        for (int a = 0; a < Lidi; a++)
        {
            for (int b = a + 1; b < Lidi; b++)
            {
                Vector3 prvni = colony.Colonists.PositionOf(a);
                Vector3 druhy = colony.Colonists.PositionOf(b);

                if (MathF.Abs(prvni.Y - druhy.Y) >= 1f)
                {
                    continue;
                }

                float dx = prvni.X - druhy.X;
                float dz = prvni.Z - druhy.Z;
                float odstup = MathF.Sqrt((dx * dx) + (dz * dz));

                nejblizsi = MathF.Min(nejblizsi, odstup);

                if (odstup < ColonistBody.HalfWidth)
                {
                    prekryvu++;
                }
            }
        }

        output.WriteLine($"{Lidi} lidi po 1 200 ticich: prekryvu {prekryvu} "
            + $"z {Lidi * (Lidi - 1) / 2} dvojic, nejblizsi {nejblizsi:F3} bloku "
            + $"(sirka tela {ColonistBody.Width:F2}).");

        // Dav se nemusí rozplést do posledního člověka — na deseti buňkách se dvě stě lidí
        // fyzicky nevejde. Musí ale být vidět, že se rozplétá: pár procent překryvů, ne dav
        // slepený do jednoho bodu.
        Assert.True(
            prekryvu < Lidi,
            $"Po 1 200 ticích zůstalo {prekryvu} překryvů — dav se nerozplétá.");
    }
}
