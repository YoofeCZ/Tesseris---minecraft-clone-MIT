using OpenTK.Mathematics;
using Tesseris.Engine.MathLib;
using Tesseris.Engine.Rendering;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

/// <summary>
/// Převod chunku na trojúhelníky greedy meshingem.
///
/// Pracuje nad <b>odsazeným objemem 34³</b>, tedy nad kopií chunku s jednovoxelovým lemem
/// ze sousedů. Díky tomu nemusí u každého voxelu řešit, jestli náhodou nekouká za hranici
/// chunku, a stínění rohů vychází správně i na okrajích. Lem o jeden voxel stačí, protože
/// vzorky pro stínění leží nejvýš o jedno políčko úhlopříčně od stěny — a zároveň to
/// znamená, že se v celé horké smyčce nemusí kontrolovat meze.
///
/// Adresování je přírůstkové. Pro každou osu se dopředu spočítají tři kroky (podél řezu,
/// podél u a podél v) a index se pak jen posouvá sčítáním. Původní verze počítala plný
/// trojrozměrný index pro každou ze 101 376 buněk masky a meshing kvůli tomu trval
/// šestkrát déle, než je cíl.
///
/// Mesher nic nekreslí ani nesahá na grafické API. Je to čistá funkce nad polem, takže se dá testovat
/// bez okna a ve F2 poběží na worker vlákně.
/// </summary>
public static class ChunkMesher
{
    /// <summary>Šířka lemu na každé straně.</summary>
    public const int Pad = 1;

    /// <summary>Hrana odsazeného objemu.</summary>
    public const int PaddedSize = Chunk.Size + (2 * Pad);

    /// <summary>Počet prvků odsazeného objemu.</summary>
    public const int PaddedVolume = PaddedSize * PaddedSize * PaddedSize;

    private const int LayerStride = PaddedSize * PaddedSize;

    /// <summary>
    /// Kolik článků rostliny se vejde do příznaku ve stínění.
    ///
    /// <para>Tři bity na pořadí článku a tři na celkovou výšku. Nejvyšší generovaná
    /// rostlina je chaluha o pěti blocích, takže osmička je rezerva. Až se výška zvedne
    /// nad osm, musí se rozšířit i váhy v <see cref="EmitCross"/> a v rozbalování
    /// ve vertex shaderech.</para>
    /// </summary>
    private const int MaxPlantSegments = 8;

    /// <summary>
    /// Příznak ve stínění, který říká „tohle je drobný porost, ne listí".
    /// </summary>
    /// <remarks>
    /// Leží nad všemi ostatními poli (maximum bez něj je 255), takže se rozbaluje jako
    /// první. Rozbalování ve vertex shaderech na tom pořadí závisí — kdyby se přidalo
    /// pole vyšší, musí se přidat i tam, a to nejvýš.
    /// </remarks>
    public const float PlantFlag = 256f;

    /// <summary>Výřezová karta ležící na zemi; nehýbe se větrem a nevrhá stín.</summary>
    public const float GroundClutterFlag = 512f;

    /// <summary>Pevná výřezová geometrie (žebříky, tabulky skla, dveře) bez větru.</summary>
    public const float StaticCutoutFlag = 768f;

    /// <summary>
    /// Příznak procedurálního plamene v cutout shaderu. Leží nad poli rostlin a
    /// statické geometrie; samostatné blokové světlo už má vlastní vertex atribut.
    /// </summary>
    public const float AnimatedFlameFlag = 1024f;

    /// <summary>
    /// Priznak pevne casti svetelneho zdroje. Shader diky nemu nenecha dreveny drik
    /// pochodne zcernat ve stejne jeskyni, kterou pochoden sama osvetluje.
    /// </summary>
    public const float TorchStickFlag = 2048f;

    /// <summary>
    /// Jednotka statického blokového světla zabaleného do atributu stínování.
    /// Leží nad všemi staršími příznaky; vertex shader ji musí rozbalit jako první.
    /// </summary>
    public const float BlockLightFlag = 1024f;

    /// <summary>Index prvku (0, 0, 0), tedy posun daný lemem.</summary>
    private const int BaseIndex = Pad + (Pad * PaddedSize) + (Pad * LayerStride);

    /// <summary>Index do odsazeného objemu. Souřadnice smí být od -1 do 32 včetně.</summary>
    public static int PaddedIndex(int x, int y, int z) =>
        (x + Pad) + ((z + Pad) * PaddedSize) + ((y + Pad) * LayerStride);

    /// <summary>
    /// Sestaví geometrii chunku.
    /// </summary>
    /// <param name="padded">Odsazený objem 34³ indexovaný přes <see cref="PaddedIndex"/>.</param>
    /// <param name="registry">Registry kvůli průhlednosti a vrstvám textur.</param>
    /// <param name="opaque">Buffer pro neprůhlednou geometrii. Metoda ho na začátku vyprázdní.</param>
    /// <param name="transparent">Buffer pro průhlednou geometrii. Metoda ho na začátku vyprázdní.</param>
    /// <param name="chunkPosition">
    /// Pozice chunku ve světě. Používají ji jen rostliny, které se podle světové souřadnice
    /// natáčejí a mění velikost. Výchozí nula stačí testům, kterým na tom nezáleží — jen
    /// se pak vzor opakuje po 32 blocích.
    /// </param>
    /// <param name="cutout">
    /// Buffer pro rostliny. Kreslí se <b>se zápisem do hloubky</b> a bez míchání, protože
    /// tráva je buď plná, nebo díra — nic mezi tím. Kdyby šla do průhledného průchodu,
    /// stébla by se navzájem nezakrývala a v louce by se stínovaly stovky ploch přes sebe.
    /// Když se nepředá, rostliny skončí v průhledném bufferu (testy).
    /// </param>
    /// <param name="water">
    /// Buffer pro kapaliny.
    ///
    /// <para>Voda má <b>vlastní buffer ze stejného důvodu jako tráva</b>: potřebuje jiný
    /// shader, ne jiný stav. Hladina se nestínuje texturou, ale Fresnelem, odrazem oblohy
    /// a odleskem slunce — to všechno závisí na směru pohledu, takže to nejde zapéct do
    /// vrcholu ani do textury. Ve společném průhledném průchodu se sklem by se to muselo
    /// v shaderu rozlišovat podle indexu vrstvy, což je přesně ta křehkost, kvůli které
    /// se oddělil i výřezový průchod.</para>
    ///
    /// <para>Když se nepředá, voda skončí v průhledném bufferu (testy).</para>
    /// </param>
    public static void Build(
        ReadOnlySpan<ushort> padded, BlockRegistry registry, MeshBuffer opaque, MeshBuffer transparent,
        Vector3i chunkPosition = default, MeshBuffer? cutout = null, MeshBuffer? water = null,
        ReadOnlySpan<byte> pieces = default,
        ReadOnlySpan<ushort> extraBlocks = default, ReadOnlySpan<byte> extraMasks = default,
        ReadOnlySpan<byte> fluid = default,
        byte[]? blockLightStorage = null,
        ReadOnlySpan<int> skyEntry = default,
        byte[]? skyLightStorage = null,
        ReadOnlySpan<byte> preparedBlockLight = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(opaque);
        ArgumentNullException.ThrowIfNull(transparent);

        cutout?.Clear();
        water?.Clear();

        if (padded.Length < PaddedVolume)
        {
            throw new ArgumentException($"Odsazený objem musí mít aspoň {PaddedVolume} prvků.", nameof(padded));
        }

        opaque.Clear();
        transparent.Clear();

        ReadOnlySpan<bool> opacity = registry.OpacityTable;
        ReadOnlySpan<BlockShape> shapes = registry.ShapeTable;
        ReadOnlySpan<bool> aquatic = registry.AquaticTable;
        ReadOnlySpan<bool> containsWater = registry.WaterTable;
        byte[] blockLightArray = blockLightStorage is { Length: >= PaddedVolume }
            ? blockLightStorage
            : new byte[PaddedVolume];
        Span<byte> blockLight = blockLightArray.AsSpan(0, PaddedVolume);
        if (preparedBlockLight.Length >= PaddedVolume)
        {
            preparedBlockLight[..PaddedVolume].CopyTo(blockLight);
        }
        else
        {
            BuildBlockLight(padded, opacity, registry.EmissionTable, blockLight);
        }
        byte[] skyLightArray = skyLightStorage is { Length: >= PaddedVolume }
            ? skyLightStorage
            : new byte[PaddedVolume];
        Span<byte> skyLight = skyLightArray.AsSpan(0, PaddedVolume);
        BuildSkyLight(padded, opacity, registry, skyEntry, skyLight);
        ushort waterBlock = registry.TryIndexOf("tesseris:water", out ushort registeredWater)
            ? registeredWater
            : BlockRegistry.Air;

        // Na zásobníku, ne na haldě: meshování poběží ve F2 na worker vláknech pro každý
        // streamovaný chunk a dvě pole po 12 kB na chunk by zbytečně tlačila na GC.
        Span<MaskCell> positive = stackalloc MaskCell[Chunk.Size * Chunk.Size];
        Span<MaskCell> negative = stackalloc MaskCell[Chunk.Size * Chunk.Size];

        for (int axis = 0; axis < 3; axis++)
        {
            (int strideD, int strideU, int strideV) = Strides(axis);

            // Řezy vedou mezi voxelem na 'slice' a voxelem na 'slice + 1'. Krajní hodnoty -1
            // a 31 pokrývají i stěny na hranici chunku, kde je protějšek už v lemu.
            for (int slice = -1; slice < Chunk.Size; slice++)
            {
                (bool anyPositive, bool anyNegative) = BuildMasks(
                    padded, pieces, opacity, shapes, aquatic, containsWater, waterBlock,
                    blockLight, skyLight,
                    fluid, chunkPosition.Y * Chunk.Size,
                    axis, slice, strideD, strideU, strideV,
                    positive, negative);

                // Prázdný řez se přeskočí celý. Ve světě, kde je většina voxelů buď plná,
                // nebo prázdná, nemá drtivá většina řezů jedinou viditelnou stěnu a projít
                // kvůli tomu tisíc buněk masky by stálo víc než všechno ostatní dohromady.
                if (anyPositive)
                {
                    EmitMask(
                        positive, registry, axis, slice + 1, positiveFacing: true,
                        opaque, transparent, water, cutout);
                }

                if (anyNegative)
                {
                    EmitMask(
                        negative, registry, axis, slice + 1, positiveFacing: false,
                        opaque, transparent, water, cutout);
                }
            }
        }

        EmitShapes(
            padded, pieces, extraBlocks, extraMasks, registry, shapes,
            opaque, cutout ?? transparent, chunkPosition, skyLight);
    }

    /// <summary>
    /// Vysází tvary, které nejsou krychle: zkřížené plochy rostlin a tenké sloupky kmenů.
    ///
    /// <para><b>Proč mimo greedy meshing.</b> Ten stojí na tom, že se sousední shodné stěny
    /// slučují do obdélníků — u trávy není co slučovat, protože každé stéblo je samostatný
    /// kříž. Zároveň by se musely řešit stěny krychle, které rostlina nemá.</para>
    ///
    /// <para>Rostliny se kreslí do <b>výřezového</b> bufferu: textura má díry a stébla se
    /// překrývají. Každá plocha jde do meshe <b>dvakrát s opačným navíjením</b>, protože
    /// průchod zahazuje odvrácené stěny a jednostranná tráva by z jedné strany zmizela.
    /// Sloupek je naopak uzavřené těleso z plné textury, takže patří do neprůhledného
    /// bufferu a navíjení mu stačí jedno.</para>
    ///
    /// <para>Prochází se jen vlastní objem chunku, ne lem: rostlina v sousedním chunku
    /// patří tomu sousedovi.</para>
    /// </summary>
    private static void EmitShapes(
        ReadOnlySpan<ushort> padded, ReadOnlySpan<byte> pieces,
        ReadOnlySpan<ushort> extraBlocks, ReadOnlySpan<byte> extraMasks,
        BlockRegistry registry, ReadOnlySpan<BlockShape> shapes,
        MeshBuffer opaque, MeshBuffer target, Vector3i chunkPosition,
        ReadOnlySpan<byte> skyLight = default)
    {
        // Kde v obou bufferech tvary začínají. Jas se jim dopočítá až nakonec, viz
        // ApplyShapeLight — proplétat světlo skrz deset různých vysílačů tvarů by
        // znamenalo změnit deset signatur a v každé na to nezapomenout.
        int opaqueFrom = opaque.VertexCount;
        int targetFrom = target.VertexCount;

        int baseX = chunkPosition.X * Chunk.Size;
        int baseY = chunkPosition.Y * Chunk.Size;
        int baseZ = chunkPosition.Z * Chunk.Size;

        ReadOnlySpan<bool> opacity = registry.OpacityTable;
        ReadOnlySpan<bool> aquatic = registry.AquaticTable;
        ReadOnlySpan<bool> containsWater = registry.WaterTable;

        for (int y = 0; y < Chunk.Size; y++)
        {
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    int index = PaddedIndex(x, y, z);
                    ushort block = padded[index];

                    BlockShape shape = shapes[block];
                    byte mask = pieces.IsEmpty ? PieceMask.Full : pieces[index];
                    byte extraMask = extraMasks.IsEmpty ? PieceMask.Empty : extraMasks[index];

                    if (shape == BlockShape.Cube && mask == PieceMask.Full && extraMask == PieceMask.Empty)
                    {
                        continue;
                    }

                    // PŘÍZNAK „JE NAD TÍM VODA" MUSÍ DOSTAT I ROSTLINA.
                    //
                    // Sonduje se od bloku SAMOTNÉHO, ne od souseda: rostlina stojí přímo ve
                    // vodě, takže první, co má sonda potkat, je kapalina nad ní.
                    //
                    // Bez tohohle se podvodní porost kreslil úplně bez vodního útlumu.
                    // Držel si sytě zelenou barvu, zatímco všechno kolem něj zesvětlalo do
                    // tyrkysova — na snímku z toho byly tmavé shluky, které vypadaly jako
                    // černé skvrny v textuře. Chyba přitom nebyla v textuře vůbec.
                    bool submerged = aquatic[block]
                        || IsSubmerged(padded, opacity, containsWater, index + LayerStride, baseY);

                    // CHOMÁČ LISTÍ. Několik ploch místo krychle - viz EmitFoliage.
                    if (shape == BlockShape.Foliage && mask == PieceMask.Full)
                    {
                        EmitFoliage(
                            PickTarget(registry, block, opaque, target),
                            registry, block, x, y, z,
                            baseX + x, baseY + y, baseZ + z,
                            registry.OverhangOf(block),
                            submerged);

                        continue;
                    }

                    if (shape == BlockShape.GroundClutter && mask == PieceMask.Full)
                    {
                        EmitGroundClutter(
                            PickTarget(registry, block, opaque, target),
                            registry, block,
                            x, y, z, baseX + x, baseY + y, baseZ + z);
                        continue;
                    }

                    if (shape == BlockShape.HytaleModel && mask == PieceMask.Full)
                    {
                        EmitHytaleModel(
                            PickTarget(registry, block, opaque, target), registry, block, x, y, z);
                        continue;
                    }

                    if (shape == BlockShape.Torch)
                    {
                        MeshBuffer torchTarget = PickTarget(registry, block, opaque, target);
                        EmitTorch(
                            torchTarget, registry.FaceLayer(block, BlockFace.PosY),
                            new Vector3(x, y, z), mask);
                        continue;
                    }

                    if (shape == BlockShape.Chest)
                    {
                        if (PieceMask.ChestIsAnimating(mask))
                        {
                            continue;
                        }

                        BuildingPartAnimationMesher.AddChest(
                            PickTarget(registry, block, opaque, target), registry, block,
                            new Vector3(x, y, z), openAmount: 0f, mask);
                        continue;
                    }

                    // SLOUPEK KMENE. Vlastní tvar, ne dílky: dílek leží vždy v půlce bloku,
                    // takže by kmen nikdy nevyšel doprostřed a strom by stál u kraje.
                    if (shape is BlockShape.Door or BlockShape.Trapdoor or BlockShape.Ladder)
                    {
                        // Během krátké interaktivní animace panel kreslí dynamický renderer.
                        // Statická kopie v chunku by jinak zůstala pod otáčejícím se panelem.
                        if ((shape == BlockShape.Door && PieceMask.DoorIsAnimating(mask))
                            || (shape == BlockShape.Trapdoor && PieceMask.TrapdoorIsAnimating(mask)))
                        {
                            continue;
                        }

                        MeshBuffer panelTarget = PickTarget(registry, block, opaque, target);
                        bool capTop = true;
                        bool capBottom = true;
                        if (shape == BlockShape.Door)
                        {
                            string doorId = registry.Definition(block).Id;
                            string aboveId = registry.Definition(padded[index + LayerStride]).Id;
                            string belowId = registry.Definition(padded[index - LayerStride]).Id;
                            capTop = doorId != "tesseris:wooden_door"
                                || aboveId != "tesseris:wooden_door_top";
                            capBottom = doorId != "tesseris:wooden_door_top"
                                || belowId != "tesseris:wooden_door";
                        }

                        foreach (Aabb panel in PieceMask.Colliders(shape, mask))
                        {
                            bool rotateCaps = shape == BlockShape.Trapdoor
                                && !PieceMask.TrapdoorIsOpen(mask)
                                && !PieceMask.TrapdoorIsAlongZ(mask);
                            EmitBox(
                                panelTarget, registry, block, x, y, z,
                                x + panel.Min.X, y + panel.Min.Y, z + panel.Min.Z,
                                x + panel.Max.X, y + panel.Max.Y, z + panel.Max.Z,
                                capTop, capBottom, sides: AllSides, submerged,
                                rotateCaps: rotateCaps,
                                preserveLocalSideUv: shape == BlockShape.Door,
                                reverseLocalSideUv: shape == BlockShape.Door
                                    && PieceMask.DoorUvReversed(mask));
                        }

                        continue;
                    }

                    if (shape == BlockShape.Post && mask == PieceMask.Full)
                    {
                        EmitBox(
                            PickTarget(registry, block, opaque, target),
                            registry, block, x, y, z,
                            x + PieceMask.Post.Min.X, y, z + PieceMask.Post.Min.Z,
                            x + PieceMask.Post.Max.X, y + 1f, z + PieceMask.Post.Max.Z,
                            capTop: shapes[padded[index + LayerStride]] != BlockShape.Post,
                            capBottom: shapes[padded[index - LayerStride]] != BlockShape.Post,
                            sides: AllSides,
                            submerged,

                            // Sloupek je jeden kus, takže nese celou dlaždici — jinak by z jeho
                            // letokruhů byl vidět jen roh.
                            stretch: true);

                        // LISTÍ KOLEM KMENE SE KRESLÍ JAKO RÁM, NE JAKO DÍLKY.
                        //
                        // Dílek je půlka bloku, sloupek zabírá prostřední polovinu — protnou se
                        // vždycky, ať se vybere kterýkoli dílek. Ve hře se listí viditelně bořilo
                        // do kmene. Rám sloupek obchází přesně (viz PieceMask.PostFrame).
                        if (extraMask != PieceMask.Empty)
                        {
                            ushort around = extraBlocks[index];
                            MeshBuffer frameTarget = PickTarget(registry, around, opaque, target);

                            foreach (Aabb frame in PieceMask.PostFrame())
                            {
                                EmitBox(
                                    frameTarget, registry, around, x, y, z,
                                    x + frame.Min.X, y + frame.Min.Y, z + frame.Min.Z,
                                    x + frame.Max.X, y + frame.Max.Y, z + frame.Max.Z,
                                    capTop: true, capBottom: true, sides: AllSides, submerged);
                            }
                        }

                        continue;
                    }

                    if (shape == BlockShape.Stairs)
                    {
                        EmitSmall(
                            PickTarget(registry, block, opaque, target),
                            padded, pieces, extraBlocks, extraMasks, shapes,
                            registry, block, PieceMask.StairOccupancy(mask), index,
                            x, y, z, submerged);
                        continue;
                    }

                    // BLOK ROZDĚLENÝ NA DÍLKY. Nese ho strom, který se sází na dvakrát jemnější
                    // mřížce, aby měl kmen poloviční šířku a listí se ho dotýkalo.
                    if (mask != PieceMask.Full || extraMask != PieceMask.Empty)
                    {
                        EmitSmall(
                            PickTarget(registry, block, opaque, target),
                            padded, pieces, extraBlocks, extraMasks, shapes,
                            registry, block, mask, index, x, y, z, submerged);

                        // DRUHY MATERIAL TEHOZ BLOKU. V korune je to listi kolem kmene: blok
                        // patri kmeni, ale zbyle dilky maji byt listi - bez toho zustane kolem
                        // kazdeho kmene dira o velikosti celeho bloku.
                        if (extraMask != PieceMask.Empty)
                        {
                            ushort extra = extraBlocks[index];

                            EmitSmall(
                                PickTarget(registry, extra, opaque, target),
                                padded, pieces, extraBlocks, extraMasks, shapes,
                                registry, extra, extraMask, index, x, y, z, submerged);
                        }

                        continue;
                    }

                    // HASH BEZ VÝŠKY U VODNÍCH ROSTLIN.
                    //
                    // Hash řídí posun trsu stranou a jeho výšku. U louky do něj patří i Y —
                    // sousední trsy mají stát každý jinak. Vodní rostlina ale stojí z několika
                    // bloků NAD SEBOU a ty musí tvořit jeden stvol: kdyby každé patro dostalo
                    // vlastní posun, rozjede se sloupec do schodů. Přesně to bylo na snímku.
                    uint hash = submerged
                        ? PlantHash(baseX + x, 0, baseZ + z)
                        : PlantHash(baseX + x, baseY + y, baseZ + z);

                    // KOŘEN, TĚLO A VRCHOLEK MAJÍ VLASTNÍ KRESBU.
                    //
                    // Vodní rostlina stojí z několika bloků nad sebou a každý z nich kreslil
                    // tutéž dlaždici. Ta je navržená jako PROSTŘEDNÍ článek — stvol vede od
                    // hrany k hraně — takže nejvyšší kus vypadal, jako by byl uříznutý.
                    //
                    // Blok už umí mít jinou texturu na každou stěnu, takže není potřeba nic
                    // přidávat: horní stěna nese vrcholek, spodní kořen a boční tělo. Který
                    // z nich se použije, pozná mesher podle toho, jestli je nad ním a pod ním
                    // táž rostlina. Bloky, které varianty nemají, dostanou všude tutéž vrstvu
                    // a chovají se jako dřív.
                    // KOLIKÁTÝ ČLÁNEK ZDOLA A JAK VYSOKÝ JE CELÝ SLOUPEC.
                    //
                    // Vítr potřebuje výšku vrcholu nad patou CELÉ rostliny, ne nad patou
                    // bloku. Bral ji z texturové souřadnice, jenže ta se v každém bloku
                    // vrací na nulu - chaluha se proto při větru lámala po článcích,
                    // každé patro mělo vlastní kořen.
                    //
                    // Sonda dá zároveň to, co dřív dělaly jednokrokové testy: nenulový
                    // běh znamená, že táž rostlina pokračuje.
                    int below = PlantRun(padded, index, block, -LayerStride);
                    int above = PlantRun(padded, index, block, LayerStride);

                    BlockFace part = above == 0 ? BlockFace.PosY
                        : below == 0 ? BlockFace.NegY
                        : BlockFace.PosX;

                    EmitCross(
                        target, x, y, z,
                        registry.FaceLayer(block, part),
                        baseX + x, baseY + y, baseZ + z,
                        submerged, below, Math.Min(below + above, MaxPlantSegments - 1));
                }
            }
        }
    
        // JAS TVARŮ AŽ TEĎ, PODLE POLOHY GEOMETRIE.
        //
        // Do téhle chvíle dostávaly rostliny, kmeny i listí jen jas stěny a světlo oblohy
        // neznaly vůbec. V lese z toho byla nesmyslná kombinace: země pod stromem ztmavla,
        // ale tráva na ní dál zářila naplno, jako by na ni svítilo vlastní slunce.
        ApplyShapeLight(opaque, opaqueFrom, padded, registry, skyLight);

        if (!ReferenceEquals(target, opaque))
        {
            ApplyShapeLight(target, targetFrom, padded, registry, skyLight);
        }
    }

    /// <summary>
    /// Domíchá do už vysázených vrcholů světlo oblohy podle toho, kde geometrie leží.
    /// </summary>
    /// <remarks>
    /// <para><b>Bere se MAXIMUM z osmi buněk kolem vrcholu, ne buňka pod ním.</b> Vrchol
    /// rostliny leží na hranici bloku a zaokrouhlení by ho stejně často trefilo do
    /// sousední horniny s nulovým světlem — po okrajích stébel by byly černé skvrny.
    /// Maximum je navíc totéž, co dělá <see cref="CornerLight"/> u stěn krychlí, takže
    /// tráva a země pod ní vyjdou stejně.</para>
    ///
    /// <para><b>Rozbalení příznaků.</b> Stínění nese kromě jasu i příznaky (voda, výška
    /// sloupce, článek rostliny), a ty jsou všechny násobky dvojky. Jas je jediné, co je
    /// pod dvojkou — proto se dá oddělit zaokrouhlením dolů na sudou hodnotu.</para>
    /// </remarks>
    private static void ApplyShapeLight(
        MeshBuffer mesh, int from, ReadOnlySpan<ushort> padded,
        BlockRegistry registry, ReadOnlySpan<byte> skyLight)
    {
        if (skyLight.Length < PaddedVolume || mesh.VertexCount <= from)
        {
            return;
        }

        ReadOnlySpan<bool> opacity = registry.OpacityTable;
        float[] vertices = mesh.RawVertices;

        for (int vertex = from; vertex < mesh.VertexCount; vertex++)
        {
            int at = vertex * MeshBuffer.FloatsPerVertex;

            int x = (int)MathF.Floor(vertices[at]);
            int y = (int)MathF.Floor(vertices[at + 1]);
            int z = (int)MathF.Floor(vertices[at + 2]);

            byte level = 0;

            for (int dz = -1; dz <= 0; dz++)
            for (int dy = -1; dy <= 0; dy++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int cx = Math.Clamp(x + dx, -Pad, Chunk.Size + Pad - 1);
                int cy = Math.Clamp(y + dy, -Pad, Chunk.Size + Pad - 1);
                int cz = Math.Clamp(z + dz, -Pad, Chunk.Size + Pad - 1);
                int index = PaddedIndex(cx, cy, cz);

                if (!opacity[padded[index]] && skyLight[index] > level)
                {
                    level = skyLight[index];
                }
            }

            float packed = vertices[at + 6];
            float flags = MathF.Floor(packed / 2f) * 2f;
            vertices[at + 6] = flags + ((packed - flags) * LuantiLight.Brightness(level));
        }
    }


    /// <summary>
    /// Dvě svislé plochy zkřížené přes střed bloku, každá z obou stran.
    ///
    /// <para><b>Plochy jdou po ÚHLOPŘÍČKÁCH bloku, ne po libovolném úhlu.</b> Zkoušel jsem
    /// natáčet každý trs jinam, aby louka nevypadala razítkovaně — dopadlo to špatně:
    /// při některých úhlech obě plochy skoro splynuly s pohledem a z rostliny byl jediný
    /// plochý plát. Blokové hry to dělají po úhlopříčkách právě proto, že se pak obě
    /// plochy vidí vždycky obě.</para>
    ///
    /// <para>Rozměr je taky po úhlopříčce, ne půl bloku: plocha měří <c>1,17</c> bloku,
    /// takže rostlina vyplní blok. S poloměrem 0,44 byla o třetinu užší a působila drobně.</para>
    ///
    /// <para>Rozmanitost dělá <b>výška a posun</b>, ne natočení — obojí z hashe světové
    /// souřadnice, aby se vzor neopakoval po dvaatřiceti blocích.</para>
    /// </summary>
    private static void EmitCross(
        MeshBuffer target, int x, int y, int z, float layer,
        int worldX, int worldY, int worldZ, bool submerged,
        int segment, int column)
    {
        // ROZMĚRY SI MESHER NEPOČÍTÁ SÁM. Bere je z PlantShape, protože podle týchž
        // čísel míří paprsek — jinak by zaměřovač trefil kytku i půl bloku vedle stébla.
        (PlantShape.Plane first, PlantShape.Plane second) =
            PlantShape.Planes(worldX, worldY, worldZ, submerged);

        // Rostliny nestíní ani nejsou stíněné — jas se bere z vrchní stěny, aby splynuly
        // s trávou pod sebou.
        //
        // PŘÍZNAKY SE VEZOU VE STÍNĚNÍ, PROTOŽE JAS SÁM JE VŽDYCKY 0 AŽ 1.
        //
        //    +2       nad rostlinou je voda
        //    +4  * n  n = kolikátý článek zdola (0 až 7)
        //   +32  * m  m = výška celého sloupce mínus jedna (0 až 7)
        //  +256       je to drobný porost, ne listí
        //
        // Maximum vyjde 511. Stínění je plný float (vertex atribut je R32Sfloat, ne
        // normalizovaný bajt), takže i v pásmu [256, 512) je krok kolem 3e-5 - o dva
        // řády jemněji, než je kvantum osmibitového výstupu. Vlastní atribut by stál
        // čtyři bajty na každém vrcholu v celém světě, a přitom by ho potřeboval
        // jenom porost.
        //
        // <b>Poslední příznak odlišuje trávu a kytky od listí.</b> V datech byly do téhle
        // chvíle k nerozeznání: jednoblokový trs má článek i sloupec nulový, takže mu
        // vyšlo přesně totéž stínění jako chomáči listí. Stínový průchod přitom potřebuje
        // vědět, co je co — drobný porost do stínové mapy nepatří, listí ano.
        //
        // Rostliny nestíní ani nejsou stíněné - jas se bere z vrchní stěny, aby splynuly
        // s trávou pod sebou.
        float shade = FaceShading.ForAxis(1, positive: true)
            + (submerged ? 2f : 0f)
            + (segment * 4f)
            + (column * 32f)
            + PlantFlag;

        Plane(target, x + first.From.X, z + first.From.Y, x + first.To.X, z + first.To.Y,
            y + first.Bottom, y + first.Top, layer, shade);

        Plane(target, x + second.From.X, z + second.From.Y, x + second.To.X, z + second.To.Y,
            y + second.Bottom, y + second.Top, layer, shade);
    }

    private static void EmitGroundClutter(
        MeshBuffer target, BlockRegistry registry, ushort block, int x, int y, int z,
        int worldX, int worldY, int worldZ)
    {
        GroundClutterModel? model = registry.GroundModelOf(block);
        if (model is null)
        {
            return;
        }

        float angle = GroundClutterShape.AngleRadians(worldX, worldY, worldZ);
        var offset = new Vector3(x, y, z);
        float layer = registry.FaceLayer(block, BlockFace.PosY);

        foreach (GroundClutterModel.Face face in model.Faces)
        {
            float shade = face.Shade + GroundClutterFlag;
            target.AddQuad(
                offset + GroundClutterShape.TransformPoint(face.P0, angle),
                offset + GroundClutterShape.TransformPoint(face.P1, angle),
                offset + GroundClutterShape.TransformPoint(face.P2, angle),
                offset + GroundClutterShape.TransformPoint(face.P3, angle),
                face.U0, face.U1, face.U2, face.U3,
                layer, shade, shade, shade, shade, false);
        }
    }

    private static void EmitHytaleModel(
        MeshBuffer target, BlockRegistry registry, ushort block, int x, int y, int z)
    {
        if (registry.HytaleFacesOf(block) is not { } faces)
        {
            return;
        }

        var origin = new Vector3(x + 0.5f, y, z + 0.5f);
        float layer = registry.FaceLayer(block, BlockFace.PosY);

        foreach (ItemShape.Face face in faces)
        {
            target.AddQuad(
                origin + face.P0, origin + face.P1, origin + face.P2, origin + face.P3,
                face.U0, face.U1, face.U2, face.U3,
                layer, face.Shade, face.Shade, face.Shade, face.Shade, false);
        }
    }

    /// <summary>
    /// Chomáč listí: čtyři svislé plochy v různých směrech uvnitř jednoho bloku.
    ///
    /// <para><b>Proč zrovna takhle.</b> Krychlové listí je vidět jako kostku, ať se textura
    /// kreslí jakkoli — hrana bloku prostě je hrana. Cross s dvěma plochami je zase na korunu
    /// málo: plochy se navzájem nezakrývají a mezi nimi je vidět obloha. Šest ploch, které se
    /// navzájem prostupují, dá objem, protože z každého směru je vždycky nějaká plocha
    /// natočená skoro čelem.</para>
    ///
    /// <para><b>Čtyři svislé plochy</b>: dvě po úhlopříčkách bloku a dvě po osách, tedy
    /// natočené o 45 stupňů proti nim. Z každého směru je tak vždycky nějaká plocha skoro
    /// čelem.</para>
    ///
    /// <para><b>Plochy přesahují za hranice bloku</b> o <c>overhang</c>. Sousední listy se tím
    /// prostoupí a mřížka zmizí - to je celý důvod, proč koruna přestane vypadat jako
    /// poskládaná z kostek. Data zůstávají bloková: kope se po celých blocích.</para>
    ///
    /// <para>Natočení ani výšky se nerandomizují z hashe jako u trávy. Rostlina stojí na zemi
    /// a soused vedle ní má vypadat jinak; listí je naopak souvislá hmota a náhodné posuny
    /// by v ní udělaly díry.</para>
    /// </summary>
    private static void EmitFoliage(
        MeshBuffer target, BlockRegistry registry, ushort block, int x, int y, int z,
        int worldX, int worldY, int worldZ, float overhang, bool submerged)
    {
        // Rostliny nestíní ani nejsou stíněné - jas se bere z vrchní stěny.
        // Dvojka navíc říká vertex shaderu, že je nad blokem voda (viz EmitCross).
        float shade = FaceShading.ForAxis(1, positive: true) + (submerged ? 2f : 0f);

        // TŘI VARIANTY TEXTURY NA JEDEN CHOMÁČ.
        //
        // Textury listí jsou nepravidelné karty, ne dlaždice - kdyby všechny čtyři plochy
        // nesly tutéž, byl by na každém bloku vidět dvakrát tentýž obrys a koruna by
        // vypadala razítkovaně. Varianty se berou ze stěn "top", "side" a "bottom", protože
        // chomáč žádné skutečné stěny nemá a ty tři sloty jsou tak volné.
        Span<float> variants =
        [
            registry.FaceLayer(block, BlockFace.PosY),
            registry.FaceLayer(block, BlockFace.PosX),
            registry.FaceLayer(block, BlockFace.NegY),
        ];

        // Pořadí se posouvá podle světové polohy, aby dva sousední chomáče neměly stejnou
        // plochu natočenou stejným směrem.
        int shift = (int)(PlantHash(worldX, worldY, worldZ) % 3u);

        float lo = -overhang;
        float hi = 1f + overhang;

        // NATOČENÍ KŘÍŽŮ. Hodnoty vzešly z ručního skládání v náhledu, ne z odhadu:
        // tři kříže, každý dvě zkřížené plochy, každý jinak natočený a s vlastní texturou.
        //
        // Pořadí rotací kopíruje CSS transform, ve kterém se to skládalo: nejdřív Z,
        // pak Y, nakonec X. Jiné pořadí dá jiný výsledek a přestalo by to sedět s náhledem.
        ReadOnlySpan<float> anglesX = [109f, 52f, 0f];
        ReadOnlySpan<float> anglesY = [-15f, 54f, -9f];

        // Poloměr plochy. Přesah roztahuje kartu za hranice bloku, aby se sousední
        // chomáče prostoupily a mřížka zmizela.
        float half = 0.5f + overhang;
        var centre = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);

        // KŘÍŽE SE ROZESTOUPÍ OD STŘEDU.
        //
        // Kdyby všechny procházely týmž bodem, protínaly by se tam všechny najednou a na
        // průsečnicích by blikaly: dvě plochy ve stejné hloubce, hloubkový buffer nemá čím
        // rozhodnout, která je vpředu. Rozestup je malý (setiny bloku), takže se chomáč
        // nerozpadne, ale průsečíky se rozejdou do různých hloubek a blikání zmizí.
        //
        // Posun jde do tří různých směrů, ne po přímce - jinak by se kříže seřadily za sebe
        // a z boku by mezi nimi byla vidět mezera.
        ReadOnlySpan<Vector3> offsets =
        [
            new(0.035f, 0.020f, -0.028f),
            new(-0.030f, -0.026f, 0.033f),
            new(0.012f, 0.034f, 0.018f),
        ];

        // ROZHOZENÍ PODLE SVĚTOVÉ POLOHY BLOKU.
        //
        // Rozestup výš řeší jen kříže uvnitř JEDNOHO bloku. Sousední bloky mají ale tytéž
        // offsety i tytéž úhly, takže jejich plochy leží na stejných relativních místech -
        // a přesah 0,45 je do sebe zasune přesně. Dvě totožné plochy ve stejné hloubce
        // pak blikají při každém pohybu kamery.
        //
        // Jitter je v setinách bloku a bere se z hashe polohy, takže je stálý: chunk se
        // může přesíťovat kolikrát chce a listí zůstane, kde bylo.
        uint jitterHash = PlantHash(worldX * 7, worldY * 13, worldZ * 11);
        var jitter = new Vector3(
            (((jitterHash & 0xFFu) / 255f) - 0.5f) * 0.09f,
            ((((jitterHash >> 8) & 0xFFu) / 255f) - 0.5f) * 0.09f,
            ((((jitterHash >> 16) & 0xFFu) / 255f) - 0.5f) * 0.09f);

        // Natočení se rozhodí taky, jinak jsou všechny chomáče v koruně stejné.
        float spinJitter = ((((jitterHash >> 24) & 0xFFu) / 255f) - 0.5f) * 0.5f;

        for (int k = 0; k < 3; k++)
        {
            float layer = variants[(shift + k) % 3];
            float rx = anglesX[k] * MathF.PI / 180f;
            float ry = (anglesY[k] * MathF.PI / 180f) + spinJitter;

            Vector3 origin = centre + offsets[k] + jitter;

            // JEDNA PLOCHA NA KŘÍŽ, NE DVĚ.
            //
            // Druhá plocha kříže se odebrala: tři karty místo šesti. Objem drží natočení -
            // každá míří jinam, takže z libovolného směru je aspoň jedna skoro čelem.
            // Zároveň to je polovina trojúhelníků a zmizí průsečíky uvnitř kříže, které
            // byly hlavním zdrojem blikání.
            RotatedPlane(target, origin, half, rx, ry, 0f, layer, shade);
        }

        // ŽÁDNÉ VODOROVNÉ PLOCHY.
        //
        // Byly tu dvě, ve výškách 0,32 a 0,68, kvůli pohledu shora a zespodu. Jenže vodorovná
        // deska uprostřed bloku je vidět skrz svislé plochy jako placka procházející středem
        // koruny - a to vypadá hůř, než co měla vyřešit. Objem dělají svislé plochy tím,
        // že se prostupují; shora korunu zakryje listí z bloků nad ní.
    }

    /// <summary>
    /// Čtvercová plocha natočená v prostoru, viditelná z obou stran.
    ///
    /// <para>Rohy se spočítají v rovině XY a pak se otočí. Pořadí rotací je Z, Y, X -
    /// stejné jako u CSS <c>rotateX() rotateY() rotateZ()</c>, ve kterém se tvar skládal
    /// v náhledu. Při jiném pořadí vyjdou z týchž úhlů jiné plochy.</para>
    ///
    /// <para><paramref name="spin"/> otáčí plochu kolem její vlastní svislé osy ještě
    /// před natočením - tím vzniká z jedné plochy kříž.</para>
    /// </summary>
    private static void RotatedPlane(
        MeshBuffer target, Vector3 centre, float half,
        float rx, float ry, float spin, float layer, float shade)
    {
        Span<Vector3> corners =
        [
            new(-half, -half, 0f),
            new(half, -half, 0f),
            new(half, half, 0f),
            new(-half, half, 0f),
        ];

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 p = corners[i];

            // Vlastní otočení plochy kolem svislice (dělá z plochy kříž).
            (p.X, p.Z) = ((p.X * MathF.Cos(spin)) + (p.Z * MathF.Sin(spin)),
                (-p.X * MathF.Sin(spin)) + (p.Z * MathF.Cos(spin)));

            // Natočení celého kříže: Y, pak X. Složka Z je v zadání vždy nula.
            (p.X, p.Z) = ((p.X * MathF.Cos(ry)) + (p.Z * MathF.Sin(ry)),
                (-p.X * MathF.Sin(ry)) + (p.Z * MathF.Cos(ry)));

            (p.Y, p.Z) = ((p.Y * MathF.Cos(rx)) - (p.Z * MathF.Sin(rx)),
                (p.Y * MathF.Sin(rx)) + (p.Z * MathF.Cos(rx)));

            corners[i] = centre + p;
        }

        const float Bleed = HalfTexel;
        var uv0 = new Vector2(Bleed, 1f - Bleed);
        var uv1 = new Vector2(1f - Bleed, 1f - Bleed);
        var uv2 = new Vector2(1f - Bleed, Bleed);
        var uv3 = new Vector2(Bleed, Bleed);

        // Obě strany - karta listí musí být vidět zepředu i zezadu.
        target.AddQuad(corners[0], corners[1], corners[2], corners[3],
            uv0, uv1, uv2, uv3, layer, shade, shade, shade, shade, false);
        target.AddQuad(corners[1], corners[0], corners[3], corners[2],
            uv1, uv0, uv3, uv2, layer, shade, shade, shade, shade, false);
    }

    /// <summary>
    /// Vodorovná plocha viditelná z obou stran. Protějšek <see cref="Plane"/>, který umí
    /// jen svislé.
    /// </summary>
    private static void HorizontalPlane(
        MeshBuffer target, float ax, float az, float bx, float bz,
        float height, float layer, float shade)
    {
        var p00 = new Vector3(ax, height, az);
        var p10 = new Vector3(bx, height, az);
        var p11 = new Vector3(bx, height, bz);
        var p01 = new Vector3(ax, height, bz);

        var uv00 = new Vector2(HalfTexel, HalfTexel);
        var uv10 = new Vector2(1f - HalfTexel, HalfTexel);
        var uv11 = new Vector2(1f - HalfTexel, 1f - HalfTexel);
        var uv01 = new Vector2(HalfTexel, 1f - HalfTexel);

        // Obě strany: shora i zespodu. Bez druhé je koruna při pohledu vzhůru prázdná.
        target.AddQuad(p00, p10, p11, p01, uv00, uv10, uv11, uv01, layer, shade, shade, shade, shade, false);
        target.AddQuad(p00, p01, p11, p10, uv00, uv01, uv11, uv10, layer, shade, shade, shade, shade, false);
    }

    /// <summary>Půl texelu dlaždice. Používá se k zatažení UV, ať filtr nesáhne za hranu.</summary>
    private const float HalfTexel = 0.5f / 64f;

    /// <summary>Které boční stěny kvádru se mají kreslit. Bity v pořadí NegX, PosX, NegZ, PosZ.</summary>
    [Flags]
    private enum BoxSides
    {
        None = 0,
        NegX = 1,
        PosX = 2,
        NegZ = 4,
        PosZ = 8,
    }

    private const BoxSides AllSides = BoxSides.NegX | BoxSides.PosX | BoxSides.NegZ | BoxSides.PosZ;

    /// <summary>Je blok na téhle pozici rozdělený na dílky?</summary>
    private static bool HasPieces(ReadOnlySpan<byte> pieces, int index) =>
        !pieces.IsEmpty && pieces[index] != PieceMask.Full;

    /// <summary>Do kterého bufferu blok patří. Protějšek výběru v <see cref="EmitMask"/>.</summary>
    private static MeshBuffer PickTarget(BlockRegistry registry, ushort block, MeshBuffer opaque, MeshBuffer plants) =>
        registry.IsOpaqueRender(block) ? opaque : plants;

    /// <summary>
    /// Listí složené z dílků o poloviční hraně.
    ///
    /// <para><b>Proč dílky a ne jedna zmenšená krychle.</b> Jedna krychlička uprostřed bloku
    /// dala korunu z oddělených kostek plovoucích ve vzduchu — mezi středy sousedních bloků
    /// je vždycky celý blok, takže poloviční kostky se nikdy nedotknou. Osm dílků vyplní blok
    /// beze zbytku, takže koruna drží pohromadě, a přitom je z dvakrát jemnějších kostek než
    /// dřív. To je to, co k tenkému kmeni sedí.</para>
    ///
    /// <para><b>Část dílků chybí</b> podle hashe své světové souřadnice. Bez toho by byla
    /// koruna zase hladký kvádr, jen z menších kostek — chybějící dílky jí udělají prokousaný
    /// okraj. Hashuje se souřadnice dílku, ne bloku, takže se na ni shodnou i dva sousední
    /// chunky, aniž by si cokoli předávaly.</para>
    ///
    /// <para><b>Vnitřní stěny se zahazují</b>, a to i přes hranici bloku: dílek se ptá souseda,
    /// jestli existuje, a když ano, stěnu mezi nimi nekreslí. Bez toho by koruna stála na
    /// osminásobku trojúhelníků, z nichž drtivá většina by byla schovaná uvnitř.</para>
    /// </summary>
    private static void EmitSmall(
        MeshBuffer target, ReadOnlySpan<ushort> padded, ReadOnlySpan<byte> pieces,
        ReadOnlySpan<ushort> extraBlocks, ReadOnlySpan<byte> extraMasks,
        ReadOnlySpan<BlockShape> shapes,
        BlockRegistry registry, ushort block, byte mask, int index,
        int x, int y, int z, bool submerged)
    {
        const int Steps = PieceMask.Steps;
        const float Size = PieceMask.Size;

        for (int j = 0; j < Steps; j++)
        {
            for (int k = 0; k < Steps; k++)
            {
                for (int i = 0; i < Steps; i++)
                {
                    if (!PieceMask.Has(mask, i, j, k))
                    {
                        continue;
                    }

                    // Soused dílku může ležet v tomtéž bloku, nebo až v sousedním. Přes hranici
                    // se počítá jen tehdy, když je vedle TÝŽ blok — dvě různá listí se
                    // neslepují, jinak by mezi dubem a smrkem vznikla souvislá hmota.
                    BoxSides sides = BoxSides.None;

                    if (!Occupied(padded, pieces, extraBlocks, extraMasks, shapes, block, index, i - 1, j, k)) { sides |= BoxSides.NegX; }
                    if (!Occupied(padded, pieces, extraBlocks, extraMasks, shapes, block, index, i + 1, j, k)) { sides |= BoxSides.PosX; }
                    if (!Occupied(padded, pieces, extraBlocks, extraMasks, shapes, block, index, i, j, k - 1)) { sides |= BoxSides.NegZ; }
                    if (!Occupied(padded, pieces, extraBlocks, extraMasks, shapes, block, index, i, j, k + 1)) { sides |= BoxSides.PosZ; }

                    bool capTop = !Occupied(padded, pieces, extraBlocks, extraMasks, shapes, block, index, i, j + 1, k);
                    bool capBottom = !Occupied(padded, pieces, extraBlocks, extraMasks, shapes, block, index, i, j - 1, k);

                    if (sides == BoxSides.None && !capTop && !capBottom)
                    {
                        continue;
                    }

                    EmitBox(
                        target, registry, block, x, y, z,
                        x + (i * Size), y + (j * Size), z + (k * Size),
                        x + ((i + 1) * Size), y + ((j + 1) * Size), z + ((k + 1) * Size),
                        capTop, capBottom, sides, submerged);
                }
            }
        }
    }

    /// <summary>
    /// Existuje dílek na téhle pozici? Souřadnice smí přetéct mimo blok — pak se odpověď
    /// hledá u souseda, a jen když je to týž blok.
    /// </summary>
    /// <summary>
    /// Je sousedni dilek obsazeny tymz materialem? Souradnice smi pretect mimo blok - pak se
    /// odpoved hleda u souseda.
    ///
    /// <para>Hleda se v OBOU vrstvach bloku: v hlavni i v druhem materialu. Bez toho by mezi
    /// kmenem a listim uvnitr tehoz bloku zustala vnitrni stena, kterou stejne nikdo nevidi.</para>
    /// </summary>
    private static bool Occupied(
        ReadOnlySpan<ushort> padded, ReadOnlySpan<byte> pieces,
        ReadOnlySpan<ushort> extraBlocks, ReadOnlySpan<byte> extraMasks,
        ReadOnlySpan<BlockShape> shapes,
        ushort block, int index, int i, int j, int k)
    {
        const int Steps = PieceMask.Steps;

        int stepX = FloorDiv(i, Steps);
        int stepY = FloorDiv(j, Steps);
        int stepZ = FloorDiv(k, Steps);

        int neighbour = index + stepX + (stepZ * PaddedSize) + (stepY * LayerStride);

        if ((uint)neighbour >= (uint)padded.Length)
        {
            return false;
        }

        int li = i - (stepX * Steps);
        int lj = j - (stepY * Steps);
        int lk = k - (stepZ * Steps);

        if (padded[neighbour] == block)
        {
            byte mask = pieces.IsEmpty ? PieceMask.Full : pieces[neighbour];
            if (shapes[padded[neighbour]] == BlockShape.Stairs)
            {
                mask = PieceMask.StairOccupancy(mask);
            }

            if (PieceMask.Has(mask, li, lj, lk))
            {
                return true;
            }
        }

        if (extraMasks.IsEmpty || extraBlocks[neighbour] != block) return false;
        byte extraMask = extraMasks[neighbour];
        if (shapes[extraBlocks[neighbour]] == BlockShape.Stairs)
        {
            extraMask = PieceMask.StairOccupancy(extraMask);
        }
        return PieceMask.Has(extraMask, li, lj, lk);
    }

    /// <summary>Celočíselné dělení dolů. Pro záporná čísla se liší od <c>/</c>.</summary>
    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : ((value + 1) / divisor) - 1;

    /// <summary>
    /// Kvádr menší než blok. Sloupek kmene i jeden dílek listí.
    ///
    /// <para><b>Proč vůbec.</b> Kmen o šířce celého bloku vypadal u vysokého stromu jako
    /// sloup, ne jako strom, a zvýšení stromů ten nepoměr jen zdůraznilo. Jemnější listí
    /// k tomu patří: tenký kmen s korunou slepenou z celých kostek nesedí dohromady.
    /// Obojí drží proporci, aniž by se svět musel dělit na menší kostky — to se zkoušelo
    /// a nepřineslo to nic než poloviční snímkovou frekvenci.</para>
    ///
    /// <para><b>UV se odvozuje z polohy kvádru uvnitř bloku</b>, ne z celé dlaždice. Kdyby
    /// se přes poloviční šířku natáhla celá kresba, byla by kůra dvakrát hustší než na
    /// sousedním plném bloku a kmen by z ní vypadal jako z jiného materiálu. Takhle si texel
    /// drží tutéž velikost jako všude jinde ve světě a sousední dílky na sebe navazují.</para>
    ///
    /// <para>Stěny mají jen jedno navíjení: na rozdíl od rostliny je kvádr uzavřené těleso,
    /// takže dovnitř není odkud koukat.</para>
    /// </summary>
    private static void EmitBox(
        MeshBuffer target, BlockRegistry registry, ushort block, int blockX, int blockY, int blockZ,
        float x0, float y0, float z0, float x1, float y1, float z1,
        bool capTop, bool capBottom, BoxSides sides, bool submerged, bool stretch = false,
        bool rotateCaps = false, bool preserveLocalSideUv = false,
        bool reverseLocalSideUv = false)
    {
        // KDY SE TEXTURA NATÁHNE PŘES CELÝ KVÁDR A KDY SE VYŘÍZNE Z DLAŽDICE.
        //
        // Sloupek kmene je JEDEN KUS: jeho textura je kresba, ne dlažba, takže letokruhy
        // na vršku patří na něj celé. Kdyby se vyřízl jen ten výřez dlaždice, který sloupek
        // v bloku zabírá, byl by ze středu letokruhů vidět jen roh a kmen vypadal useknutý
        // našikmo.
        //
        // Dílky a rám listí jsou naopak KUSY JEDNOHO BLOKU. Tam se výřez vzít musí — jinak
        // dostane každý kousek celou dlaždici a kresba se přes něj roztáhne. Přesně tak
        // vypadalo listí kolem kmene: čtyři pruhy s natažlou texturou.
        float localX0 = stretch ? 0f : x0 - blockX;
        float localX1 = stretch ? 1f : x1 - blockX;
        float localZ0 = stretch ? 0f : z0 - blockZ;
        float localZ1 = stretch ? 1f : z1 - blockZ;

        // Svislá složka je obrácená: řádek 0 textury je vršek obrázku, ale leží nahoře ve světě.
        float vTop = stretch ? 0f : 1f - (y1 - blockY);
        float vBottom = stretch ? 1f : 1f - (y0 - blockY);

        float LocalSideU(float value) => reverseLocalSideUv ? 1f - value : value;

        // Příznak „je nad tím voda" se veze ve stínění, stejně jako u stěn krychle (viz EmitQuad).
        float wet = submerged ? 2f : 0f;
        float staticCutout = registry.IsCutout(block) ? StaticCutoutFlag : 0f;

        // Boky. Pořadí vrcholů jde proti směru hodinových ručiček při pohledu zvenčí,
        // aby stěna přežila zahazování odvrácených trojúhelníků.
        if (sides.HasFlag(BoxSides.NegX))
        {
            BoxQuad(
                target,
                new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0),
                preserveLocalSideUv ? LocalSideU(localZ0) : localZ0,
                preserveLocalSideUv ? LocalSideU(localZ1) : localZ1,
                vTop, vBottom,
                registry.FaceLayer(block, BlockFace.NegX),
                FaceShading.ForAxis(0, positive: false) + wet + staticCutout);
        }

        if (sides.HasFlag(BoxSides.PosX))
        {
            BoxQuad(
                target,
                new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1),
                preserveLocalSideUv ? LocalSideU(localZ1) : 1f - localZ1,
                preserveLocalSideUv ? LocalSideU(localZ0) : 1f - localZ0,
                vTop, vBottom,
                registry.FaceLayer(block, BlockFace.PosX),
                FaceShading.ForAxis(0, positive: true) + wet + staticCutout);
        }

        if (sides.HasFlag(BoxSides.NegZ))
        {
            BoxQuad(
                target,
                new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0),
                preserveLocalSideUv ? LocalSideU(localX1) : 1f - localX1,
                preserveLocalSideUv ? LocalSideU(localX0) : 1f - localX0,
                vTop, vBottom,
                registry.FaceLayer(block, BlockFace.NegZ),
                FaceShading.ForAxis(2, positive: false) + wet + staticCutout);
        }

        if (sides.HasFlag(BoxSides.PosZ))
        {
            BoxQuad(
                target,
                new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1),
                preserveLocalSideUv ? LocalSideU(localX0) : localX0,
                preserveLocalSideUv ? LocalSideU(localX1) : localX1,
                vTop, vBottom,
                registry.FaceLayer(block, BlockFace.PosZ),
                FaceShading.ForAxis(2, positive: true) + wet + staticCutout);
        }

        // Víčka. UV kopírují rozvržení vodorovné stěny krychle: u běží po Z, v po X.
        if (capTop)
        {
            float shade = FaceShading.ForAxis(1, positive: true) + wet + staticCutout;

            target.AddQuad(
                new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0),
                rotateCaps ? Uv(localX0, localZ0) : Uv(localZ0, localX0),
                rotateCaps ? Uv(localX0, localZ1) : Uv(localZ1, localX0),
                rotateCaps ? Uv(localX1, localZ1) : Uv(localZ1, localX1),
                rotateCaps ? Uv(localX1, localZ0) : Uv(localZ0, localX1),
                registry.FaceLayer(block, BlockFace.PosY), shade, shade, shade, shade, false);
        }

        if (capBottom)
        {
            float shade = FaceShading.ForAxis(1, positive: false) + wet + staticCutout;

            target.AddQuad(
                new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1),
                rotateCaps ? Uv(localX0, localZ1) : Uv(localZ1, localX0),
                rotateCaps ? Uv(localX0, localZ0) : Uv(localZ0, localX0),
                rotateCaps ? Uv(localX1, localZ0) : Uv(localZ0, localX1),
                rotateCaps ? Uv(localX1, localZ1) : Uv(localZ1, localX1),
                registry.FaceLayer(block, BlockFace.NegY), shade, shade, shade, shade, false);
        }
    }

    /// <summary>
    /// Jedna svislá stěna kvádru. <paramref name="p0"/> je levý dolní roh při pohledu zvenčí.
    /// </summary>
    private static void BoxQuad(
        MeshBuffer target, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        float uLow, float uHigh, float vTop, float vBottom, float layer, float shade)
    {
        target.AddQuad(
            p0, p1, p2, p3,
            Uv(uLow, vBottom), Uv(uHigh, vBottom), Uv(uHigh, vTop), Uv(uLow, vTop),
            layer, shade, shade, shade, shade, false);
    }

    /// <summary>
    /// UV zatažené o půl texelu od hran dlaždice.
    ///
    /// <para>Sampler má nastavené <b>opakování</b>, protože greedy meshing táhne UV přes
    /// několik bloků. Kvádr menší než blok ale opakování nechce: má přesně jednu dlaždici
    /// a filtr u okraje by sáhl na PROTĚJŠÍ hranu téže dlaždice. Přesně tohle dělalo nad
    /// každým trsem trávy viditelný kříž.</para>
    /// </summary>
    private static Vector2 Uv(float u, float v) => new(
        Math.Clamp(u, HalfTexel, 1f - HalfTexel),
        Math.Clamp(v, HalfTexel, 1f - HalfTexel));

    /// <summary>Hash pozice rostliny. Musí být stabilní, jinak by trs po přemeshování poskočil.</summary>
    private static uint PlantHash(int x, int y, int z)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)x) * 16777619u;
            h = (h ^ (uint)y) * 16777619u;
            h = (h ^ (uint)z) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            return h ^ (h >> 15);
        }
    }

    private static void Plane(
        MeshBuffer target, float ax, float az, float bx, float bz,
        float bottom, float topY, float layer, float shade)
    {
        var a0 = new Vector3(ax, bottom, az);
        var b0 = new Vector3(bx, bottom, bz);
        var b1 = new Vector3(bx, topY, bz);
        var a1 = new Vector3(ax, topY, az);

        // UV SE ZATÁHNE O PŮL TEXELU DOVNITŘ. Bez toho se přes hranu textury prosakuje.
        //
        // Sampler má nastavené OPAKOVÁNÍ, protože greedy meshing táhne UV přes několik bloků
        // a stěna z dvaceti kamenů potřebuje UV 0 až 20. Rostlina ale opakování nechce: má
        // přesně jednu dlaždici a filtrování u okraje sáhne za ni — a s opakováním vezme
        // texel z PROTĚJŠÍ hrany téže dlaždice.
        //
        // Ve hře to vypadalo takhle: nad každým trsem se na horní hraně bloku objevil kříž,
        // protože se do prázdna nahoře propsal hustý spodek rostliny. Nejvýrazněji u krátké
        // trávy, která má dole hustý trs, a slabě u vysoké, která má dole jen pár stébel —
        // přesně jak to bylo hlášeno.
        //
        // Zatažení o půl texelu zajistí, že se filtr nikdy nedotkne sousedního opakování.
        // Dlaždice má 64 texelů, takže je to 1/128 a na kresbě to není poznat.
        const float Bleed = 0.5f / 64f;

        float uLow = Bleed;
        float uHigh = 1f - Bleed;

        // Svislá složka UV je obrácená: řádek 0 textury je vršek obrázku, ale leží dole
        // v souřadnicích světa.
        var uvBottomA = new Vector2(uLow, uHigh);
        var uvBottomB = new Vector2(uHigh, uHigh);
        var uvTopB = new Vector2(uHigh, uLow);
        var uvTopA = new Vector2(uLow, uLow);

        target.AddQuad(a0, b0, b1, a1, uvBottomA, uvBottomB, uvTopB, uvTopA, layer, shade, shade, shade, shade, false);
        target.AddQuad(b0, a0, a1, b1, uvBottomB, uvBottomA, uvTopA, uvTopB, layer, shade, shade, shade, shade, false);
    }

    private static void EmitTorch(MeshBuffer target, float layer, Vector3 blockOrigin, byte state)
    {
        (Vector3 localBottom, Vector3 localTop) = PieceMask.TorchAxis(state);
        Vector3 bottom = blockOrigin + localBottom;
        Vector3 top = blockOrigin + localTop;

        // Skutecna prostorova tycka: pravidelny osmihran kolem osy pochodne. Na stene je
        // cela osa naklonena ven; na podlaze zustava svisla. UV bere jen spodni drevenou
        // cast puvodni dlazdice, takze se na tyc nikdy nepropsal plamen.
        Vector3 axis = Vector3.Normalize(top - bottom);
        Vector3 reference = MathF.Abs(Vector3.Dot(axis, Vector3.UnitY)) > 0.94f
            ? Vector3.UnitX
            : Vector3.UnitY;
        Vector3 sideA = Vector3.Normalize(Vector3.Cross(axis, reference));
        Vector3 sideB = Vector3.Normalize(Vector3.Cross(axis, sideA));
        const int Sides = 8;
        const float Radius = PieceMask.TorchRadius;
        float woodShade = 0.78f + StaticCutoutFlag + TorchStickFlag;

        for (int i = 0; i < Sides; i++)
        {
            float a0 = (MathF.Tau * i) / Sides;
            float a1 = (MathF.Tau * (i + 1)) / Sides;
            Vector3 r0 = (sideA * MathF.Cos(a0) + sideB * MathF.Sin(a0)) * Radius;
            Vector3 r1 = (sideA * MathF.Cos(a1) + sideB * MathF.Sin(a1)) * Radius;
            // Zdrojova textura je pruhledna karta a drevo zabira jen prostredni sloupec
            // x=20..43. Rozdeleni cele sirky mezi osm sten proto davalo nekterym stenam
            // pouze pruhledne pixely. Kazda stena valce musi pouzit cely dreveny pruh.
            const float u0 = 20.5f / 64f;
            const float u1 = 43.5f / 64f;
            const float vBottom = 61.5f / 64f;
            const float vTop = 31.5f / 64f;
            target.AddQuad(
                bottom + r0, bottom + r1, top + r1, top + r0,
                new Vector2(u0, vBottom), new Vector2(u1, vBottom),
                new Vector2(u1, vTop), new Vector2(u0, vTop),
                layer, woodShade, woodShade, woodShade, woodShade, false);
        }

        // Ohen je animovany kriz nad knotem. Tycka je samostatna prostorova geometrie,
        // takze zustava pevna a shader hybe pouze kresbou ohne.
        Vector3 flameCentre = top + new Vector3(0f, 0.17f, 0f);
        float flameShade = 1f + StaticCutoutFlag + AnimatedFlameFlag;
        const float HalfWidth = 0.23f;
        const float HalfHeight = 0.26f;
        Vector3 flameBottom = flameCentre - new Vector3(0f, HalfHeight, 0f);
        Vector3 flameTop = flameCentre + new Vector3(0f, HalfHeight, 0f);
        EmitTorchFlamePlane(target, flameBottom, flameTop, Vector3.UnitX * HalfWidth, layer, flameShade);
        EmitTorchFlamePlane(target, flameBottom, flameTop, Vector3.UnitZ * HalfWidth, layer, flameShade);
    }

    private static void EmitTorchFlamePlane(
        MeshBuffer target, Vector3 bottom, Vector3 top, Vector3 halfWidth, float layer, float shade)
    {
        const float Bleed = 0.5f / 64f;
        Vector3 p0 = bottom - halfWidth;
        Vector3 p1 = bottom + halfWidth;
        Vector3 p2 = top + halfWidth;
        Vector3 p3 = top - halfWidth;
        var uv0 = new Vector2(Bleed, 0.48f);
        var uv1 = new Vector2(1f - Bleed, 0.48f);
        var uv2 = new Vector2(1f - Bleed, Bleed);
        var uv3 = new Vector2(Bleed, Bleed);
        target.AddQuad(p0, p1, p2, p3, uv0, uv1, uv2, uv3, layer,
            shade, shade, shade, shade, false);
        target.AddQuad(p1, p0, p3, p2, uv1, uv0, uv3, uv2, layer,
            shade, shade, shade, shade, false);
    }

    /// <summary>
    /// Naplní masky stěn pro jeden řez. Obě strany se řeší najednou, aby se objem četl jen
    /// jednou; průhledný blok proti jinému průhlednému může vidět stěny z obou stran.
    /// </summary>
    /// <returns>Zda byla zapsána aspoň jedna stěna do kladné a do záporné masky.</returns>
    private static (bool Positive, bool Negative) BuildMasks(
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<byte> pieces,
        ReadOnlySpan<bool> opacity,
        ReadOnlySpan<BlockShape> shapes,
        ReadOnlySpan<bool> aquatic,
        ReadOnlySpan<bool> containsWater,
        ushort waterBlock,
        ReadOnlySpan<byte> blockLight,
        ReadOnlySpan<byte> skyLight,
        ReadOnlySpan<byte> fluid,
        int baseY,
        int axis,
        int slice,
        int strideD,
        int strideU,
        int strideV,
        Span<MaskCell> positive,
        Span<MaskCell> negative)
    {
        // Masky se schválně nevynulovávají: EmitMask po sobě každou spotřebovanou buňku
        // vymaže, takže do dalšího řezu vstupují už čisté. Ušetří to megabajt a půl
        // zbytečného přepisu paměti na každý chunk.
        bool anyPositive = false;
        bool anyNegative = false;

        int sliceStart = BaseIndex + (slice * strideD);

        for (int v = 0; v < Chunk.Size; v++)
        {
            int index = sliceStart + (v * strideV);

            for (int u = 0; u < Chunk.Size; u++, index += strideU)
            {
                ushort storedNear = padded[index];
                ushort storedFar = padded[index + strideD];
                ushort near = aquatic[storedNear] ? waterBlock : storedNear;
                ushort far = aquatic[storedFar] ? waterBlock : storedFar;

                // Křížové tvary (tráva, kytky) a sloupky (kmeny) do masky nepatří vůbec —
                // nevyplňují objem krychle a kreslí se vlastním průchodem. Bez tohohle by
                // z každého stébla byla průhledná krychle.
                // Do masky nepatří nic, co nevyplňuje svůj objem celý: kříže rostlin ani
                // bloky rozdělené na dílky. Greedy meshing stojí na tom, že blok objem vyplní.
                bool nearCross = !aquatic[storedNear]
                    && (shapes[near] is not BlockShape.Cube || HasPieces(pieces, index));
                bool farCross = !aquatic[storedFar]
                    && (shapes[far] is not BlockShape.Cube || HasPieces(pieces, index + strideD));

                // Zakrývá se jen tehdy, když blok svůj objem vyplní celý. Blok rozdělený na
                // dílky ani kříž rostliny sousedovi stěnu nevezme — kolem nich je vidět skrz.
                bool nearVisible = !nearCross
                    && IsFaceVisible(opacity, near, far, farOccludes: !farCross);
                bool farVisible = !farCross
                    && IsFaceVisible(opacity, far, near, farOccludes: !nearCross);

                if (!nearVisible && !farVisible)
                {
                    continue;
                }

                int cell = u + (v * Chunk.Size);

                if (nearVisible)
                {
                    // Vnějšek stěny je o jeden řez dál po směru osy.
                    positive[cell] = MakeCell(
                        padded, pieces, opacity, blockLight, skyLight,
                        near, index + strideD, strideU, strideV)
                        with
                        {
                            Depth = WaterDepthBand(padded, containsWater, near, axis, index, strideD),
                            Submerged = IsSubmerged(
                                padded, opacity, containsWater, index + strideD, baseY),
                        };

                    if (containsWater[near])
                    {
                        positive[cell] = WithSurface(
                            positive[cell], padded, containsWater, fluid,
                            index, +strideD, axis, strideU, strideV);
                    }

                    anyPositive = true;
                }

                if (farVisible)
                {
                    negative[cell] = MakeCell(
                        padded, pieces, opacity, blockLight, skyLight,
                        far, index, strideU, strideV)
                        with
                        {
                            Submerged = IsSubmerged(padded, opacity, containsWater, index, baseY),
                        };

                    if (containsWater[far])
                    {
                        negative[cell] = WithSurface(
                            negative[cell], padded, containsWater, fluid,
                            index + strideD, -strideD, axis, strideU, strideV);
                    }

                    anyNegative = true;
                }
            }
        }

        return (anyPositive, anyNegative);
    }

    /// <summary>Kolik pásem hloubky vody se rozlišuje. Víc jich není poznat.</summary>
    /// <summary>
    /// O kolik níž leží hladina proti stropu bloku. Dvě šestnáctiny, tedy 12,5 cm.
    ///
    /// <para>Veřejná schválně: stejné číslo potřebuje i renderer, aby shaderu poslal
    /// skutečnou výšku hladiny. Kdyby se rozešla, přestane sedět podvodní pohled.</para>
    /// </summary>
    public const float WaterSurfaceDrop = 2f / 16f;

    private const int WaterDepthBands = 6;

    /// <summary>Kolik bloků vody nad sebou už znamená nejtmavší pásmo.</summary>
    private const int DeepWater = 12;

    /// <summary>
    /// Pásmo hloubky pro vodní hladinu, nebo nula.
    ///
    /// <para>Počítá se jen pro <b>vrchní</b> stěnu vody, tedy pro hladinu. Boky a dno se
    /// netmaví: hráč je vidí zevnitř vody, kde už tmavne mlha.</para>
    ///
    /// <para>Hloubka se hledá scanem dolů po odsazeném objemu. Když voda sahá až za jeho
    /// okraj, bere se jako hluboká — mělčina u břehu, kde na tom vzhledově záleží, se do
    /// objemu vejde vždycky.</para>
    /// </summary>
    private static byte WaterDepthBand(
        ReadOnlySpan<ushort> padded, ReadOnlySpan<bool> containsWater,
        ushort block, int axis, int index, int strideD)
    {
        // Jen vrchní stěna vody: osa Y a stěna mířící nahoru.
        if (axis != 1 || !containsWater[block])
        {
            return 0;
        }

        int depth = 1;
        int probe = index - strideD;

        while (depth < DeepWater
            && probe >= 0
            && probe < padded.Length
            && containsWater[padded[probe]])
        {
            depth++;
            probe -= strideD;
        }

        // Pásmo 1 je nejmělčí, WaterDepthBands nejhlubší.
        return (byte)(1 + ((depth - 1) * (WaterDepthBands - 1) / (DeepWater - 1)));
    }

    /// <summary>
    /// Je nad touhle stěnou opravdu voda?
    ///
    /// <para><b>Proč to nestačí poznat z výšky.</b> Shadery braly za ponořené všechno pod
    /// úrovní moře, protože jinou informaci neměly. V jeskyni pod hladinou — a ta je pod ní
    /// skoro vždycky — tím pádem ležela přes suchý kámen modrá clona a stěny vypadaly, jako
    /// by byly zalité vodou, která tam není.</para>
    ///
    /// <para>Sonda jde od vnějšku stěny <b>vzhůru</b> a rozhoduje první věc, na kterou
    /// narazí: kapalina znamená ponořeno, neprůhledný blok znamená strop, tedy sucho.</para>
    ///
    /// <para>Když sonda vyjede z odsazeného objemu, aniž potká jedno nebo druhé, vrací se
    /// <c>true</c> — tedy <b>původní chování</b>. Mesher vidí jen svůj chunk plus blok okraje,
    /// takže o sloupci nad sebou nic neví; nechat rozhodnout shader podle úrovně moře je
    /// v tom případě lepší odhad než hádat sucho. Prakticky to znamená, že se opraví jeskyně
    /// a podzemí (mají strop), ale <b>ne suchá kotlina hlubší než chunk</b>, která je pod
    /// úrovní moře a otevřená k nebi.</para>
    /// </summary>
    private static bool IsSubmerged(
        ReadOnlySpan<ushort> padded, ReadOnlySpan<bool> opacity, ReadOnlySpan<bool> containsWater,
        int outsideIndex, int baseY)
    {
        int probe = outsideIndex;

        while (probe >= 0 && probe < padded.Length)
        {
            ushort above = padded[probe];

            if (containsWater[above])
            {
                return true;
            }

            if (opacity[above])
            {
                return false;
            }

            // NAD HLADINOU MOŘE UŽ VODA BÝT NEMŮŽE.
            //
            // Bez téhle zarážky sonda v suché jámě prošla vzduchem vzhůru, vyjela z kopie
            // chunku a vrátila „nevíme, tedy voda" — a jáma vykopaná ze břehu pod úroveň
            // moře dostala modrou clonu a kaustiky, přestože v ní nebyla ani kapka.
            //
            // Sonda končí u vzduchu nad hladinou: co je nad mořem a není to voda, sucho
            // prostě je. Zbývá jediný případ, kdy si sonda pořád neporadí — jeskyně, jejíž
            // strop je celý pod hladinou a zároveň mimo tenhle chunk. Tam se dál platí
            // původní domněnka.
            if (WorldYOf(probe, baseY) > TerrainGenerator.SeaLevel)
            {
                return false;
            }

            probe += LayerStride;
        }

        return true;
    }


    /// <summary>
    /// Rozsvítí sluneční světlo: nejdřív svisle shora dolů, pak do stran.
    /// </summary>
    /// <remarks>
    /// <para><b>Svislý průchod je ten, který dělá stín.</b> Sloupec začíná úrovní, se
    /// kterou slunce vstoupilo do odsazeného meshe shora, a každý blok cestou si vezme
    /// svoje — listí tři stupně, voda dva, pevná hornina všechno. Pod korunou proto
    /// zbude třeba dvanáct místo patnácti a je z toho stín, ne černá díra.</para>
    ///
    /// <para>Boční šíření pak dorovná jeskyně a převisy: každý krok stranou stojí jednu
    /// úroveň, takže vchod do jeskyně plynule tmavne.</para>
    /// </remarks>
    private static void BuildSkyLight(
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<bool> opacity,
        BlockRegistry registry,
        ReadOnlySpan<int> skyEntry,
        Span<byte> light)
    {
        light.Clear();

        if (skyEntry.Length >= PaddedSize * PaddedSize)
        {
            for (int z = 0; z < PaddedSize; z++)
            for (int x = 0; x < PaddedSize; x++)
            {
                int column = x + (z * PaddedSize);
                int level = Math.Clamp(skyEntry[column], 0, LuantiLight.LightSun);

                for (int y = PaddedSize - 1; y >= 0; y--)
                {
                    if (level <= 0)
                    {
                        break;
                    }

                    int index = column + (y * LayerStride);
                    ushort block = padded[index];

                    if (opacity[block])
                    {
                        break;
                    }

                    light[index] = (byte)level;
                    level -= registry.SunlightCost(block);
                }
            }
        }
        else
        {
            // Izolované mesher testy nemají světový sloupec ani horní chunky. Jejich
            // vzdušný lem proto představuje venkovní prostor, ne uzavřenou jeskyni.
            for (int index = 0; index < PaddedVolume; index++)
            {
                if (!opacity[padded[index]]) light[index] = 15;
            }
        }

        for (byte current = 15; current > 1; current--)
        {
            byte next = (byte)(current - 1);
            for (int index = 0; index < PaddedVolume; index++)
            {
                if (light[index] != current) continue;

                int y = index / LayerStride;
                int inLayer = index - (y * LayerStride);
                int z = inLayer / PaddedSize;
                int x = inLayer - (z * PaddedSize);
                SpreadLight(index - 1, x > 0, next, padded, opacity, light);
                SpreadLight(index + 1, x + 1 < PaddedSize, next, padded, opacity, light);
                SpreadLight(index - PaddedSize, z > 0, next, padded, opacity, light);
                SpreadLight(index + PaddedSize, z + 1 < PaddedSize, next, padded, opacity, light);
                SpreadLight(index - LayerStride, y > 0, next, padded, opacity, light);
                SpreadLight(index + LayerStride, y + 1 < PaddedSize, next, padded, opacity, light);
            }
        }
    }

    private static void BuildBlockLight(
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<bool> opacity,
        ReadOnlySpan<byte> emission,
        Span<byte> light)
    {
        light.Clear();

        for (int index = 0; index < PaddedVolume; index++)
        {
            ushort block = padded[index];
            byte level = block < emission.Length ? emission[block] : (byte)0;
            if (level == 0)
            {
                continue;
            }

            light[index] = level;
        }

        // Úrovně se procházejí sestupně. Zdroj s vyšší intenzitou tak vždy dorazí první
        // a každý voxel nepotřebuje položku v obří frontě ani opakované alokace.
        for (byte current = 15; current > 1; current--)
        {
            byte nextLevel = (byte)(current - 1);
            for (int index = 0; index < PaddedVolume; index++)
            {
                if (light[index] != current)
                {
                    continue;
                }

                int y = index / LayerStride;
                int inLayer = index - (y * LayerStride);
                int z = inLayer / PaddedSize;
                int x = inLayer - (z * PaddedSize);

                SpreadLight(index - 1, x > 0, nextLevel, padded, opacity, light);
                SpreadLight(index + 1, x + 1 < PaddedSize, nextLevel, padded, opacity, light);
                SpreadLight(index - PaddedSize, z > 0, nextLevel, padded, opacity, light);
                SpreadLight(index + PaddedSize, z + 1 < PaddedSize, nextLevel, padded, opacity, light);
                SpreadLight(index - LayerStride, y > 0, nextLevel, padded, opacity, light);
                SpreadLight(index + LayerStride, y + 1 < PaddedSize, nextLevel, padded, opacity, light);
            }
        }
    }

    private static void SpreadLight(
        int index,
        bool inside,
        byte level,
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<bool> opacity,
        Span<byte> light)
    {
        if (!inside || light[index] >= level || opacity[padded[index]])
        {
            return;
        }

        light[index] = level;
    }

    /// <summary>
    /// Světlo s přímým zastíněním. Dosah používá taxicab vzdálenost (15 až 1), ale každý
    /// cílový voxel navíc musí mít volnou přímou cestu ke zdroji. Pevná stěna proto nevytvoří
    /// jasnou kopii sama sebe na opačné straně.
    /// </summary>
    private static void BuildOccludedBlockLight(
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<bool> opacity,
        ReadOnlySpan<byte> emission,
        Span<byte> light)
    {
        light.Clear();
        for (int source = 0; source < PaddedVolume; source++)
        {
            ushort block = padded[source];
            byte level = block < emission.Length ? emission[block] : (byte)0;
            if (level == 0) continue;

            int sy = source / LayerStride;
            int sourceLayer = source - (sy * LayerStride);
            int sz = sourceLayer / PaddedSize;
            int sx = sourceLayer - (sz * PaddedSize);
            light[source] = Math.Max(light[source], level);
            int radius = level - 1;

            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = sy + dy;
                if ((uint)y >= PaddedSize) continue;
                int zr = radius - Math.Abs(dy);
                for (int dz = -zr; dz <= zr; dz++)
                {
                    int z = sz + dz;
                    if ((uint)z >= PaddedSize) continue;
                    int xr = zr - Math.Abs(dz);
                    for (int dx = -xr; dx <= xr; dx++)
                    {
                        int x = sx + dx;
                        if ((uint)x >= PaddedSize) continue;
                        int target = x + (z * PaddedSize) + (y * LayerStride);
                        byte candidate = (byte)(level - Math.Abs(dx) - Math.Abs(dy) - Math.Abs(dz));
                        if (candidate <= light[target] || opacity[padded[target]]) continue;
                        if (HasClearLightLine(padded, opacity, sx, sy, sz, x, y, z))
                        {
                            light[target] = candidate;
                        }
                    }
                }
            }
        }
    }

    private static bool HasClearLightLine(
        ReadOnlySpan<ushort> padded, ReadOnlySpan<bool> opacity,
        int sourceX, int sourceY, int sourceZ, int targetX, int targetY, int targetZ)
    {
        int dx = targetX - sourceX;
        int dy = targetY - sourceY;
        int dz = targetZ - sourceZ;
        int steps = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) * 4;
        for (int step = 1; step < steps; step++)
        {
            float t = step / (float)steps;
            int x = (int)MathF.Floor(sourceX + 0.5f + (dx * t));
            int y = (int)MathF.Floor(sourceY + 0.5f + (dy * t));
            int z = (int)MathF.Floor(sourceZ + 0.5f + (dz * t));
            int index = x + (z * PaddedSize) + (y * LayerStride);
            if (opacity[padded[index]]) return false;
        }

        return true;
    }

    /// <summary>Světová výška bloku podle jeho místa v odsazeném objemu.</summary>
    private static int WorldYOf(int paddedIndex, int baseY) =>
        ((paddedIndex - BaseIndex) / LayerStride) + baseY;

    /// <summary>
    /// Kolik bloků téže rostliny leží v zadaném směru nad sebou, nejvýš
    /// <see cref="MaxPlantSegments"/> - 1.
    ///
    /// <para>Krok je násobek <see cref="LayerStride"/>, takže sonda drží týž sloupec
    /// (x, z) a nemůže přetéct do vedlejšího - na rozdíl od kroku o jedničku nebo
    /// o <see cref="PaddedSize"/>.</para>
    ///
    /// <para><b>Dosah omezuje lem.</b> Odsazený objem má lem jeden voxel, takže sonda
    /// vidí reálná data souseda nanejvýš o blok za hranicí chunku. Generovaným
    /// rostlinám to nevadí - sloupec zapisuje vždycky ten chunk, ve kterém je jeho pata.
    /// Ručně postavený sloupec přes hranici se spočítá kratší; sečte se jen ta část,
    /// na kterou sonda dosáhne.</para>
    /// </summary>
    private static int PlantRun(ReadOnlySpan<ushort> padded, int index, ushort block, int step)
    {
        int run = 0;
        int probe = index + step;

        while (run < MaxPlantSegments - 1 && probe >= 0 && probe < padded.Length && padded[probe] == block)
        {
            run++;
            probe += step;
        }

        return run;
    }

    /// <summary>
    /// Má se kreslit stěna bloku <paramref name="near"/> směrem k <paramref name="far"/>?
    /// </summary>
    /// <param name="farOccludes">
    /// Smí protější blok stěnu vůbec zakrýt? Blok rozdělený na dílky nesmí — kolem dílků je
    /// vidět skrz, takže by pod stromem chyběl kus terénu.
    /// </param>
    private static bool IsFaceVisible(
        ReadOnlySpan<bool> opacity, ushort near, ushort far, bool farOccludes = true)
    {
        if (near == BlockRegistry.Air)
        {
            return false;
        }

        if (far == BlockRegistry.Air)
        {
            return true;
        }

        if (farOccludes && opacity[far])
        {
            return false;
        }

        // PROTĚJŠEK, KTERÝ SVŮJ OBJEM NEVYPLNÍ CELÝ, STĚNU NIKDY NESCHOVÁ — ani když je to
        // týž blok.
        //
        // Bez téhle výjimky platilo pravidlo o dvou stejných blocích i mezi PLNÝM blokem listí
        // a blokem listí rozděleným na dílky: plný soused stěnu vynechal, protože „vedle je
        // taky listí", a dílkovaný na jejím místě nakreslil jen pár dílků. Ve hře z toho byly
        // průhledné čtverce v koruně, kterými bylo vidět na kmen i na oblohu — a po odstranění
        // toho listu díra zmizela, protože se soused přemeshoval už bez něj.
        if (!farOccludes)
        {
            return true;
        }

        // Dvě sousedící skla stěnu mezi sebou nekreslí — jinak by se uvnitř skleněné stavby
        // objevily vnitřní přepážky.
        return near != far;
    }

    /// <summary>
    /// Doplní buňce výšky rohů hladiny.
    /// </summary>
    /// <remarks>
    /// <para><b>Roh se počítá z bloků, které se ho dotýkají.</b> Sousední bloky tak ve
    /// sdíleném rohu vyjdou na tutéž výšku a hladina na sebe navazuje — bez toho by z ní
    /// byly schody, protože každý blok by měl svou vlastní rovinu.</para>
    ///
    /// <para>Do průměru jdou jen bloky, které vodu mají. Vzduch ani břeh se nezapočítávají:
    /// kdyby se braly jako nula, hladina by se u kraje propadla pod úroveň, kterou tam voda
    /// doopravdy má.</para>
    ///
    /// <para><b>Platí i pro svislé stěny.</b> Horní hrana boku leží na téže hladině jako
    /// plocha nad ním, takže se musí počítat ze stejných rohů. Kdyby bok dostal jen úroveň
    /// svého bloku, rozešel by se s hladinou o zlomek bloku a na okraji doběhu by zůstal
    /// svislý zub — naměřeno přesně osminu bloku, než se to spravilo.</para>
    /// </remarks>
    private static MaskCell WithSurface(
        MaskCell cell,
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<bool> containsWater,
        ReadOnlySpan<byte> fluid,
        int index,
        int outward,
        int axis,
        int strideU,
        int strideV)
    {
        if (fluid.IsEmpty)
        {
            return cell;
        }

        // Vodorovná stěna: čtyři rohy hladiny, každý z bloků, které se ho dotýkají.
        if (axis == 1)
        {
            return cell with
            {
                H00 = CornerLevel(padded, containsWater, fluid, index, -strideU, -strideV),
                H10 = CornerLevel(padded, containsWater, fluid, index, +strideU, -strideV),
                H11 = CornerLevel(padded, containsWater, fluid, index, +strideU, +strideV),
                H01 = CornerLevel(padded, containsWater, fluid, index, -strideU, +strideV),
            };
        }

        // Svislá stěna. Rohy horní hrany leží na hranici mezi tímhle blokem a tím za stěnou,
        // proto jeden z posunů míří ven ze stěny (outward) a druhý podél ní.
        //
        // Která dvojice rohů je nahoře, se liší podle osy: u stěny kolmé na X je svisle
        // osa u, tedy H10 a H11; u stěny kolmé na Z je svisle osa v, tedy H01 a H11.
        // Zbylé dva rohy leží na dně a výška se na ně neaplikuje.
        if (axis == 0)
        {
            return cell with
            {
                H10 = CornerLevel(padded, containsWater, fluid, index, outward, -strideV),
                H11 = CornerLevel(padded, containsWater, fluid, index, outward, +strideV),
            };
        }

        return cell with
        {
            H01 = CornerLevel(padded, containsWater, fluid, index, outward, -strideU),
            H11 = CornerLevel(padded, containsWater, fluid, index, outward, +strideU),
        };
    }

    /// <summary>Průměr úrovní čtyř bloků, které se stýkají v jednom rohu.</summary>
    private static byte CornerLevel(
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<bool> containsWater,
        ReadOnlySpan<byte> fluid,
        int index,
        int offsetU,
        int offsetV)
    {
        int sum = 0;
        int count = 0;
        byte highest = FluidCell.Empty;

        foreach (int probe in (ReadOnlySpan<int>)[index, index + offsetU, index + offsetV, index + offsetU + offsetV])
        {
            if (probe < 0 || probe >= padded.Length || !containsWater[padded[probe]])
            {
                continue;
            }

            byte level = LevelOf(padded, containsWater, fluid, probe);

            // NEJVYŠŠÍ ROZHODUJE, ALE AŽ PO PROJITÍ VŠECH ČTYŘ.
            //
            // Dřív se tady vracelo hned, jakmile se narazilo na zdroj — jenže první
            // zkoumaný blok je ten vlastní, takže u hladiny se vrátila výška zdroje dřív,
            // než se kód vůbec podíval, jestli soused nesahá ke stropu. Na hranici pak
            // zůstala díra vysoká přesně to snížení hladiny a voda se rozpadala na desky.
            if (level > highest)
            {
                highest = level;
            }

            sum += level;
            count++;
        }

        if (count == 0)
        {
            return FluidCell.Source;
        }

        // SLOUPEC PŘEBIJE PRŮMĚR. Rohu, kterého se dotýká voda s další vodou nad sebou,
        // musí sahat ke stropu - jinak se od patra nad ním odtrhne. Průměrovat by tady
        // znamenalo udělat mezeru menší, ne ji odstranit.
        if (highest >= FluidCell.Continuous)
        {
            return FluidCell.Continuous;
        }

        // A ZDROJ PŘEBIJE DOBĚH. Plný blok vody nemá klesnout jen proto, že vedle něj něco
        // odteklo - hráč vylije kbelík a propadne se mu i ta voda, kterou právě položil.
        // Hladina tak klesá OD zdroje, ne spolu s ním.
        if (highest >= FluidCell.Source)
        {
            return FluidCell.Source;
        }

        return (byte)(sum / count);
    }

    /// <summary>Úroveň hladiny v bloku. Voda bez zapsané úrovně je zdroj.</summary>
    /// <remarks>
    /// <b>Voda pod vodou je vždycky plná.</b> Doběh sahá jen do části bloku, takže by mezi
    /// ním a vodou nad ním zůstal pruh vzduchu — padající proud by byl na každém patře
    /// přerušený. Sloupec se proto slije: hladina má smysl jen tam, kde voda opravdu končí
    /// a začíná vzduch. Stejně to dělá Minecraft a je to i důvod, proč se z toho nestane
    /// žebřík průhledných desek.
    /// </remarks>
    private static byte LevelOf(
        ReadOnlySpan<ushort> padded, ReadOnlySpan<bool> containsWater,
        ReadOnlySpan<byte> fluid, int index)
    {
        if (index < 0 || index >= padded.Length || !containsWater[padded[index]])
        {
            return 0;
        }

        int above = index + LayerStride;

        if (above >= 0 && above < padded.Length && containsWater[padded[above]])
        {
            return FluidCell.Continuous;
        }

        byte level = fluid.IsEmpty ? FluidCell.Source : fluid[index];

        return level == 0 ? FluidCell.Source : Math.Min(level, FluidCell.Source);
    }

    /// <param name="outsideIndex">Index voxelu těsně před stěnou, tedy na její vnější straně.</param>
    private static MaskCell MakeCell(
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<byte> pieces,
        ReadOnlySpan<bool> opacity,
        ReadOnlySpan<byte> blockLight,
        ReadOnlySpan<byte> skyLight,
        ushort block,
        int outsideIndex,
        int strideU,
        int strideV)
    {
        byte a00 = CornerAo(padded, pieces, opacity, outsideIndex, -strideU, -strideV);
        byte a10 = CornerAo(padded, pieces, opacity, outsideIndex, +strideU, -strideV);
        byte a11 = CornerAo(padded, pieces, opacity, outsideIndex, +strideU, +strideV);
        byte a01 = CornerAo(padded, pieces, opacity, outsideIndex, -strideU, +strideV);

        // Vrstva textury se doplní až při vysílání obdélníku — tam se ví, o kterou stěnu jde.
        byte l00 = CornerLight(padded, opacity, blockLight, outsideIndex, -strideU, -strideV);
        byte l10 = CornerLight(padded, opacity, blockLight, outsideIndex, +strideU, -strideV);
        byte l11 = CornerLight(padded, opacity, blockLight, outsideIndex, +strideU, +strideV);
        byte l01 = CornerLight(padded, opacity, blockLight, outsideIndex, -strideU, +strideV);

        byte s00 = CornerLight(padded, opacity, skyLight, outsideIndex, -strideU, -strideV);
        byte s10 = CornerLight(padded, opacity, skyLight, outsideIndex, +strideU, -strideV);
        byte s11 = CornerLight(padded, opacity, skyLight, outsideIndex, +strideU, +strideV);
        byte s01 = CornerLight(padded, opacity, skyLight, outsideIndex, -strideU, +strideV);

        return new MaskCell(
            block, a00, a10, a11, a01,
            S00: s00, S10: s10, S11: s11, S01: s01,
            L00: l00, L10: l10, L11: l11, L01: l01);
    }

    /// <summary>
    /// Světlo ve vrcholu je průměr čtyř vzduchových buněk, které se v daném rohu
    /// dotýkají. Sousední plochy tak dostanou shodnou hodnotu na společné hraně a GPU
    /// mezi čtyřmi rohy plynule interpoluje místo viditelného jasu po celých blocích.
    /// </summary>
    private static byte CornerLight(
        ReadOnlySpan<ushort> padded, ReadOnlySpan<bool> opacity,
        ReadOnlySpan<byte> light, int outsideIndex, int offsetU, int offsetV)
    {
        int sideU = outsideIndex + offsetU;
        int sideV = outsideIndex + offsetV;
        bool openU = !opacity[padded[sideU]];
        bool openV = !opacity[padded[sideV]];
        int sum = light[outsideIndex];
        int count = 1;

        if (openU) { sum += light[sideU]; count++; }
        if (openV) { sum += light[sideV]; count++; }

        int diagonal = outsideIndex + offsetU + offsetV;
        if (openU && openV && !opacity[padded[diagonal]])
        {
            sum += light[diagonal];
            count++;
        }

        return (byte)((sum + (count / 2)) / count);
    }

    /// <summary>
    /// Stínění jednoho rohu stěny.
    ///
    /// Bere se čtveřice bloků, která se rohu dotýká zvenčí stěny: blok přímo před stěnou
    /// a tři jeho sousedé po úhlopříčce. První z nich stínit nemůže — kdyby byl plný,
    /// stěna by se vůbec nekreslila — takže o výsledku rozhodují zbylé tři.
    /// Dva plné boční bloky roh uzavřou úplně a rohový vzorek už se nezkoumá.
    ///
    /// Meze se nekontrolují schválně: všechny tři vzorky leží v rozsahu -1..32, který lem
    /// odsazeného objemu pokrývá.
    /// </summary>
    private static byte CornerAo(
        ReadOnlySpan<ushort> padded,
        ReadOnlySpan<byte> pieces,
        ReadOnlySpan<bool> opacity,
        int outsideIndex,
        int offsetU,
        int offsetV)
    {
        bool side1 = Shades(padded, pieces, opacity, outsideIndex + offsetU);
        bool side2 = Shades(padded, pieces, opacity, outsideIndex + offsetV);

        if (side1 && side2)
        {
            return 0;
        }

        bool corner = Shades(padded, pieces, opacity, outsideIndex + offsetU + offsetV);

        return (byte)(3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (corner ? 1 : 0));
    }

    /// <summary>
    /// Vrha blok na tehle pozici stin do rohu sousedni steny?
    ///
    /// <para><b>Blok rozdeleny na dilky nevrha.</b> Je z nej jen kousek, takze plny stin
    /// po nem nezustane. Bez teto vyjimky bylo pod kazdym kmenem na zemi videt ctvercovou
    /// skvrnu velkou pres cely blok — presne takovy stin, jaky by vrhala plna kostka,
    /// zatimco na tom miste stal sloupek o polovicni sirce.</para>
    /// </summary>
    private static bool Shades(
        ReadOnlySpan<ushort> padded, ReadOnlySpan<byte> pieces, ReadOnlySpan<bool> opacity, int index) =>
        opacity[padded[index]] && !HasPieces(pieces, index);

    /// <summary>Slučuje sousední shodné buňky masky do co největších obdélníků.</summary>
    private static void EmitMask(
        Span<MaskCell> mask,
        BlockRegistry registry,
        int axis,
        int planeCoord,
        bool positiveFacing,
        MeshBuffer opaque,
        MeshBuffer transparent,
        MeshBuffer? water,
        MeshBuffer? cutout)
    {
        var face = (BlockFace)((axis * 2) + (positiveFacing ? 1 : 0));
        ReadOnlySpan<bool> liquid = registry.LiquidTable;

        for (int v = 0; v < Chunk.Size; v++)
        {
            int u = 0;
            while (u < Chunk.Size)
            {
                MaskCell cell = mask[u + (v * Chunk.Size)];
                if (cell.Block == BlockRegistry.Air)
                {
                    u++;
                    continue;
                }

                // Šířka: kolik buněk vpravo je úplně stejných. Shodovat se musí i stínění,
                // jinak by se sloučením ztratil přechod světla.
                int width = 1;
                while (u + width < Chunk.Size && mask[u + width + (v * Chunk.Size)] == cell)
                {
                    width++;
                }

                // Výška: kolik celých řad nad tím je stejných.
                int height = 1;
                bool rowMatches = true;
                while (v + height < Chunk.Size && rowMatches)
                {
                    for (int k = 0; k < width; k++)
                    {
                        if (mask[u + k + ((v + height) * Chunk.Size)] != cell)
                        {
                            rowMatches = false;
                            break;
                        }
                    }

                    if (rowMatches)
                    {
                        height++;
                    }
                }

                // KAM STĚNA PATŘÍ.
                //
                // Výřezový buffer je tu nový pro listí. Do téhle chvíle končilo v míchaném
                // spolu se sklem, protože obojí má `opaque: false` — jenže míchaný průchod
                // nezapisuje do hloubky (aby se skla navzájem neořezávala), takže se vrstvy
                // koruny nezakrývaly a stromem bylo vidět skrz až na oblohu.
                //
                // Listí přitom poloprůhledné není: jeho textura je buď plná, nebo díra.
                // Patří proto tam, kde je tráva — alfa test se zápisem do hloubky.
                MeshBuffer target = liquid[cell.Block] && water is not null
                    ? water
                    : registry.IsOpaque(cell.Block) ? opaque
                    : registry.IsCutout(cell.Block) && cutout is not null ? cutout
                    : transparent;

                EmitQuad(
                    cell, registry.FaceLayer(cell.Block, face), axis, planeCoord, u, v, width, height,
                    positiveFacing, target, liquid[cell.Block], registry.OverhangOf(cell.Block));

                for (int dv = 0; dv < height; dv++)
                {
                    mask.Slice(u + ((v + dv) * Chunk.Size), width).Clear();
                }

                u += width;
            }
        }
    }

    private static void EmitQuad(
        in MaskCell cell,
        int layer,
        int axis,
        int planeCoord,
        int u,
        int v,
        int width,
        int height,
        bool positiveFacing,
        MeshBuffer target,
        bool isLiquid = false,
        float overhang = 0f)
    {
        // HLADINA LEŽÍ NÍŽ NEŽ STROP BLOKU.
        //
        // Když voda sahá přesně po okraj, splývá s břehem v jednu rovinu a není poznat,
        // kde končí země a začíná voda. Snížení o kousek udělá viditelný schod a zároveň
        // je vidět, že se v tom dá brodit.
        //
        // Posouvají se jen vrcholy s NEJVYŠŠÍM Y. U vodorovné stěny je to celá stěna,
        // u svislých jen její horní hrana - a ta u vody leží vždycky na hladině, protože
        // nad ní je vzduch a stěna tam končí.
        float drop = isLiquid ? WaterSurfaceDrop : 0f;

        // PŘESAH. Stěna se posune ven podél své normály a zároveň roztáhne do stran,
        // takže blok vypadá o kus větší, než ve skutečnosti je.
        //
        // Tohle je celý trik, kterým se z koruny stane jeden kus místo hromady kostek:
        // sousední listí a kmen se do sebe zanoří a hrana mezi nimi zmizí. Vnitřní stěny
        // koruny se stejně nekreslí (mají plné sousedy), takže se roztahuje jen obrys.
        //
        // UV se přesahem NEMĚNÍ - textura se tím po okrajích mírně natáhne, což je přesně
        // ten efekt "textura přesahuje přes blok".
        float outward = overhang > 0f ? (positiveFacing ? overhang : -overhang) : 0f;
        float grow = overhang;

        float slice = planeCoord + outward;
        float u0 = u - grow;
        float u1 = u + width + grow;
        float v0 = v - grow;
        float v1 = v + height + grow;

        Vector3 p00 = MakeVertex(axis, slice, u0, v0);
        Vector3 p10 = MakeVertex(axis, slice, u1, v0);
        Vector3 p11 = MakeVertex(axis, slice, u1, v1);
        Vector3 p01 = MakeVertex(axis, slice, u0, v1);

        if (isLiquid)
        {
            // KAŽDÝ ROH MÁ SVOU VÝŠKU. Doběh vody klesá s úrovní, takže hladina není rovina -
            // rohy se počítají z okolních bloků (viz WithSurface) a sousedi vyjdou ve sdíleném
            // rohu stejně. Bez toho by z hladiny byly schody.
            //
            // Zdroj má úroveň 8, tedy plnou výšku, a odečte se z něj jen obvyklé snížení pod
            // okraj bloku. Doběh klesá po osminách bloku až k nule.
            float d00 = SurfaceDrop(cell.H00, drop);
            float d10 = SurfaceDrop(cell.H10, drop);
            float d11 = SurfaceDrop(cell.H11, drop);
            float d01 = SurfaceDrop(cell.H01, drop);

            switch (axis)
            {
                case 0:
                    // Stěna kolmá na X: svisle je osa u, takže nahoře jsou p10 a p11.
                    p10.Y -= d10;
                    p11.Y -= d11;
                    break;

                case 2:
                    // Stěna kolmá na Z: svisle je osa v, tedy p01 a p11.
                    p01.Y -= d01;
                    p11.Y -= d11;
                    break;

                default:
                    // Vodorovná stěna: celá hladina dolů. Dno se nesnižuje - to je pod vodou
                    // a nikdo ho zespodu nevidí.
                    if (positiveFacing)
                    {
                        p00.Y -= d00;
                        p10.Y -= d10;
                        p11.Y -= d11;
                        p01.Y -= d01;
                    }

                    break;
            }
        }

        // UV jde od nuly po počet sloučených bloků, takže se textura přes obdélník opakuje.
        //
        // ROZLOŽENÍ SE LIŠÍ PODLE OSY a musí se řešit, i když to vypadá jako detail.
        // Osy u a v jsou zvolené tak, aby vyšlo navíjení, ne aby seděla textura: u stěn
        // kolmých na X vychází u jako světové Y a v jako Z. Se stejnými UV jako u ostatních
        // os se textura otočí o devadesát stupňů — pruh trávy pak stojí svisle na boku
        // místo vodorovně nahoře a vlákna kmene běží naležato. Zadavatel to poznal ze hry.
        //
        // Svislá složka je navíc obrácená: řádek 0 textury je vršek obrázku, ale ve světě
        // leží nahoře, tedy na vyšším Y.
        Vector2 uv00, uv10, uv11, uv01;

        switch (axis)
        {
            case 0:
                // Stěna kolmá na X: u je svisle (Y), v vodorovně (Z).
                uv00 = new Vector2(0f, width);
                uv10 = new Vector2(0f, 0f);
                uv11 = new Vector2(height, 0f);
                uv01 = new Vector2(height, width);
                break;

            case 2:
                // Stěna kolmá na Z: u vodorovně (X), v svisle (Y).
                uv00 = new Vector2(0f, height);
                uv10 = new Vector2(width, height);
                uv11 = new Vector2(width, 0f);
                uv01 = new Vector2(0f, 0f);
                break;

            default:
                // Vodorovná stěna. Na natočení textury tu nezáleží, obě osy jsou vodorovné.
                uv00 = new Vector2(0f, 0f);
                uv10 = new Vector2(width, 0f);
                uv11 = new Vector2(width, height);
                uv01 = new Vector2(0f, height);
                break;
        }

        // Do vrcholu jde jediné číslo: stínění rohu už vynásobené jasem stěny. Díky tomu
        // nemusí vrchol nést normálu a shader nemusí nic dopočítávat.
        float faceBrightness = FaceShading.ForAxis(axis, positiveFacing);

        // Tmavnutí hluboké vody se tady KDYSI dělalo a bylo to špatně: hodnota se kvůli
        // greedy meshingu musela kvantovat do pásem a na hladině z toho byly ploché skvrny
        // s ostrými okraji. Teď to řeší fragment shader, který tmaví to, co je skrz vodu
        // vidět, ne samotnou hladinu — viz chunk_opaque.frag.
        _ = cell.Depth;

        // PŘÍZNAK „JE NAD TÍM VODA" SE VEZE VE STÍNĚNÍ, ne ve vlastním atributu.
        //
        // Stínění je vždy v rozsahu 0 až 1, takže dvojka navrch je volné místo, které nic
        // nepřepíše — vertex shader ji odečte zpátky. Kdyby se přidal atribut, narostl by
        // formát vrcholu o čtyři bajty na každý vrchol v celém světě jen kvůli jednomu bitu.
        //
        // Význam je „nech podvodní efekt zapnutý": nula znamená prokazatelně sucho (nad
        // stěnou je strop), dvojka znamená voda nebo nevíme. Viz <see cref="IsSubmerged"/>.
        float wet = cell.Submerged ? 2f : 0f;

        // OBĚ BANKY PROJDOU TOUTÉŽ KŘIVKOU A TÝMŽ STÍNĚNÍM.
        //
        // Luanti nese v barvě vrcholu denní i noční jas zvlášť a obě prošly stejnou
        // převodní tabulkou i stejným stíněním stěny a rohu. Kdyby se lišily, změnil by
        // se jejich poměr — a právě z toho poměru shader počítá, kolik světla je od
        // slunce a kolik od pochodně (viz LuantiLight a chunk_opaque.frag).
        float light00 = isLiquid ? 0f : FaceShading.Combine(cell.Ao00, faceBrightness * LuantiLight.Brightness(cell.L00));
        float light10 = isLiquid ? 0f : FaceShading.Combine(cell.Ao10, faceBrightness * LuantiLight.Brightness(cell.L10));
        float light11 = isLiquid ? 0f : FaceShading.Combine(cell.Ao11, faceBrightness * LuantiLight.Brightness(cell.L11));
        float light01 = isLiquid ? 0f : FaceShading.Combine(cell.Ao01, faceBrightness * LuantiLight.Brightness(cell.L01));

        float ao0 = FaceShading.Combine(cell.Ao00, faceBrightness * SkyBrightness(cell.S00)) + wet;
        float ao1 = FaceShading.Combine(cell.Ao10, faceBrightness * SkyBrightness(cell.S10)) + wet;
        float ao2 = FaceShading.Combine(cell.Ao11, faceBrightness * SkyBrightness(cell.S11)) + wet;
        float ao3 = FaceShading.Combine(cell.Ao01, faceBrightness * SkyBrightness(cell.S01)) + wet;

        // Úhlopříčka musí spojit dva světlejší rohy, jinak přes obdélník vznikne tmavý pruh.
        // Součty se otočením pořadí nezmění, takže se rozhodnutí počítá jednou pro obě orientace.
        bool flip = cell.Ao00 + cell.Ao11 < cell.Ao10 + cell.Ao01;

        if (positiveFacing)
        {
            target.AddQuadLit(
                p00, p10, p11, p01, uv00, uv10, uv11, uv01, layer,
                ao0, ao1, ao2, ao3, light00, light10, light11, light01, flip);
        }
        else
        {
            // Obrácené pořadí otočí navíjení, aby stěna mířila na druhou stranu.
            target.AddQuadLit(
                p00, p01, p11, p10, uv00, uv01, uv11, uv10, layer,
                ao0, ao3, ao2, ao1, light00, light01, light11, light10, flip);
        }
    }

    /// <summary>
    /// Jas denní banky. Křivka je Luanti <c>light_decode_table</c>, ne mocnina.
    /// </summary>
    /// <remarks>
    /// <b>Nula je opravdu nula.</b> Dřív se sem přičítal podzemní ambient 0,035, aby
    /// jeskyně nebyla úplně černá. Luanti to řeší jinde a lépe: noc i jeskyně dostanou
    /// spodní hranu přes barvu slunce, která v noci neklesne pod 0,135 (viz
    /// <see cref="LuantiLight.SunlightColor"/>). Ambient přičtený tady by se navíc
    /// násobil i tam, kde má být tma úplná.
    /// </remarks>
    private static float SkyBrightness(byte level) => LuantiLight.Brightness(level);

    /// <summary>
    /// Kroky indexu pro danou osu: podél řezu, podél u a podél v.
    /// Volba u = osa+1, v = osa+2 zajistí, že vektorový součin hran obdélníku míří na kladnou
    /// stranu dané osy pro všechny tři osy stejně — díky tomu se navíjení nemusí řešit zvlášť.
    /// </summary>
    private static (int D, int U, int V) Strides(int axis) => axis switch
    {
        0 => (1, LayerStride, PaddedSize),
        1 => (LayerStride, PaddedSize, 1),
        _ => (PaddedSize, 1, LayerStride),
    };

    /// <summary>
    /// O kolik klesne roh hladiny proti stropu bloku.
    /// </summary>
    /// <remarks>
    /// <para>Zdroj drží plnou výšku bloku; <see cref="WaterSurfaceDrop"/> je dnes nula,
    /// protože jakékoli snížení dělalo díru proti vodě pod vodou, která ke stropu sahat
    /// musí.</para>
    ///
    /// <para><b>Doběh nezačíná těsně pod zdrojem.</b> Dokud se výška počítala prostě jako
    /// podíl úrovně, vyšel zdroj i první doběh oba na 0,875 — mezi plnou vodou a prvním
    /// přelitím tedy nebyl vidět žádný rozdíl a hladina vypadala, že neklesá. Doběh se
    /// proto rozprostírá do vlastního pásma pod zdrojem: nejvyšší stupeň leží znatelně
    /// níž a nejnižší se drží nad dnem, aby konec proudu nezmizel úplně.</para>
    /// </remarks>
    private static float SurfaceDrop(byte level, float baseDrop)
    {
        // Uvnitř sloupce se nesnižuje nic - blok musí sahat až ke stropu, aby na proudu
        // nebyly mezery mezi patry.
        if (level >= FluidCell.Continuous)
        {
            return 0f;
        }

        if (level >= FluidCell.Source)
        {
            return baseDrop;
        }

        // Pásmo, ve kterém se pohybuje doběh. Horní hranice je pod zdrojem tak, aby byl
        // první stupeň poznat; dolní nechává tenkou vrstvu i pro poslední kapku.
        const float HighestFlowing = 0.72f;
        const float LowestFlowing = 0.10f;

        float span = (level - 1f) / (FluidCell.MaxFlowing - 1f);
        float height = LowestFlowing + (span * (HighestFlowing - LowestFlowing));

        return 1f - Math.Clamp(height, LowestFlowing, HighestFlowing);
    }

    private static Vector3 MakeVertex(int axis, float slice, float u, float v) => axis switch
    {
        0 => new Vector3(slice, u, v),
        1 => new Vector3(v, slice, u),
        _ => new Vector3(u, v, slice),
    };

    /// <summary>
    /// Jedna buňka masky. Sloučit se smí jen naprosto shodné buňky, proto je to
    /// <c>record struct</c> — porovnání po hodnotě generuje překladač.
    /// </summary>
    /// <param name="Depth">
    /// Pásmo hloubky vody pod hladinou, 0 pro všechno ostatní.
    ///
    /// <para>Je součástí buňky schválně: sloučit se smí jen buňky se shodným pásmem,
    /// takže hladina zůstane sloučená v rámci pásma a rozdělí se jen tam, kde se hloubka
    /// opravdu mění. Kdyby se hloubka nekvantovala, měla by každá buňka svou hodnotu
    /// a greedy meshing by u vody přestal fungovat úplně.</para>
    /// </param>
    /// <param name="Submerged">
    /// Je nad stěnou opravdu voda? Viz <see cref="IsSubmerged"/>.
    ///
    /// <para>Je součástí buňky ze stejného důvodu jako <paramref name="Depth"/>: sloučit se
    /// smí jen stěny se shodným příznakem, jinak by jeden obdélník sahal z jeskyně ven pod
    /// vodu a nemohl by nést obojí. Stojí to o něco víc obdélníků kolem břehů a vchodů do
    /// jeskyní, jinde nic — uvnitř souvislého kamene i pod souvislou hladinou vychází
    /// příznak všude stejně a slučování běží dál.</para>
    /// </param>
    /// <summary>
    /// Jedna buňka masky pro greedy meshing.
    /// </summary>
    /// <remarks>
    /// <para>Je to <c>record struct</c>, takže porovnání na rovnost je strukturální — a právě
    /// tím se rozhoduje, jestli se dvě sousední buňky sloučí do jednoho obdélníku.</para>
    ///
    /// <para><b>Výšky rohů to řeší samy.</b> Hladina vody se sklání podle úrovně, takže dva
    /// bloky se sloučit smí jen tehdy, když mají všechny čtyři rohy shodné — jinak by
    /// se z nakloněné plochy stala rovina a hladina by se zlomila. Nemusí se tedy nic
    /// vyřazovat z greedy: moře je samý zdroj se shodnými rohy a slévá se dál jako dřív,
    /// zatímco doběh po svahu má každý blok jiný a zůstane samostatný.</para>
    ///
    /// <para>Výšky jsou v osminách bloku, tedy 0 až 8 jako úroveň vody.</para>
    /// </remarks>
    private readonly record struct MaskCell(
        ushort Block, byte Ao00, byte Ao10, byte Ao11, byte Ao01, byte Depth = 0, bool Submerged = true,
        byte H00 = FluidCell.Source, byte H10 = FluidCell.Source,
        byte H11 = FluidCell.Source, byte H01 = FluidCell.Source,
        byte S00 = 15, byte S10 = 15, byte S11 = 15, byte S01 = 15,
        byte L00 = 0, byte L10 = 0, byte L11 = 0, byte L01 = 0);
}
