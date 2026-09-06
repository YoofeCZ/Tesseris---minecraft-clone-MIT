using OpenTK.Mathematics;

namespace Tesseris.Game.Items;

/// <summary>
/// Obsah jedné pece.
/// </summary>
/// <remarks>
/// <para><b>Rozvržení je jako v Rustu:</b> nahoře jeden slot na palivo, uprostřed dva na
/// suroviny, dole tři na výstup. A hlavně — <b>pec se musí zapnout</b>. Do té doby stojí,
/// i když je v ní dřevo i ruda.</para>
///
/// <para>Zapínač není ozdoba: bez něj by pec spálila všechno palivo hned, jak by do ní
/// hráč něco dal, a nešlo by ji použít jako sklad ani nechat naloženou na později.</para>
/// </remarks>
public sealed class Furnace
{
    /// <summary>Kolik slotů má pec na suroviny.</summary>
    public const int InputSlots = 2;

    /// <summary>Kolik slotů má pec na výstup.</summary>
    public const int OutputSlots = 3;

    /// <summary>Čím se topí. Jeden slot nahoře.</summary>
    public ItemStack Fuel { get; set; } = ItemStack.Empty;

    /// <summary>Co se taví. Každý slot si taví po svém.</summary>
    public ItemStack[] Input { get; } = new ItemStack[InputSlots];

    /// <summary>Co už je hotové. Sem padá i uhlí ze spáleného dřeva.</summary>
    public ItemStack[] Output { get; } = new ItemStack[OutputSlots];

    /// <summary>Kolik vteřin hoření ještě zbývá z rozdělaného kusu paliva.</summary>
    public float BurnLeft { get; set; }

    /// <summary>Jak dlouho hořel celý kus paliva. Pro ukazatel plamene.</summary>
    public float BurnTotal { get; set; } = 1f;

    /// <summary>Jak daleko je tavení v jednotlivých slotech, 0 až 1.</summary>
    public float[] Progress { get; } = new float[InputSlots];

    /// <summary>
    /// Je pec zapnutá?
    /// </summary>
    /// <remarks>
    /// Vypnutá pec nehoří ani netaví, ale <b>rozdělané tavení si pamatuje</b>. Kdyby se
    /// postup nuloval, znamenalo by vypnutí ztrátu práce a hráč by pec radši nechával
    /// běžet naprázdno — přesně naopak, než k čemu ten vypínač je.
    /// </remarks>
    public bool Running { get; set; }

    public bool Burning => BurnLeft > 0f;

    /// <summary>Nejdál rozdělané tavení. Pro jednoduchý ukazatel.</summary>
    public float TopProgress
    {
        get
        {
            float best = 0f;

            foreach (float value in Progress)
            {
                best = MathF.Max(best, value);
            }

            return best;
        }
    }

    /// <summary>Je pec úplně prázdná?</summary>
    public bool IsEmpty =>
        Fuel.IsEmpty
        && Array.TrueForAll(Input, stack => stack.IsEmpty)
        && Array.TrueForAll(Output, stack => stack.IsEmpty);

    /// <summary>Všechno, co v peci leží. Pro vysypání při rozbití.</summary>
    public IEnumerable<ItemStack> Contents
    {
        get
        {
            yield return Fuel;

            foreach (ItemStack stack in Input)
            {
                yield return stack;
            }

            foreach (ItemStack stack in Output)
            {
                yield return stack;
            }
        }
    }
}

/// <summary>
/// Pece ve světě a jejich tavení.
/// </summary>
/// <remarks>
/// <para><b>Stav pece leží mimo blok.</b> Blok ve světě je jedno číslo — nemá kam uložit
/// tři hromádky a dva časovače. Pece se proto drží ve slovníku podle souřadnice; blok
/// v terénu je jen značka, že tam pec je.</para>
///
/// <para><b>Obsah se ukládá vedle světa, ne do chunku.</b> Formát regionu umí bloky
/// a mikrovoxely; pec je hromádka předmětů s časovači, tedy něco úplně jiného. Rozšířit
/// kvůli tomu formát chunku by znamenalo, že o předmětech musí vědět serializace terénu.
/// Pecí je ve světě řádově málo, takže se vejdou do jednoho souboru — viz
/// <see cref="Save"/> a <see cref="Load"/>.</para>
///
/// <para><b>Předměty se ukládají jménem, ne indexem.</b> Registr předmětů vzniká z bloků
/// seřazených abecedně, takže přidání jediného bloku posune indexy všech za ním a uložená
/// pec by se tiše proměnila v jinou. Je to týž důvod, proč má paleta chunku jména.</para>
/// </remarks>
public sealed class Furnaces
{
    /// <summary>Kolik vteřin trvá jedno tavení.</summary>
    /// <remarks>
    /// Deset vteřin je dost na to, aby se čekání počítalo, a málo na to, aby to byla
    /// otrava. Uhlí hoří osmdesát vteřin, takže z jednoho kusu je osm tavení.
    /// </remarks>
    public const float SmeltSeconds = 10f;

    private readonly Dictionary<Vector3i, Furnace> _furnaces = [];
    private readonly ItemRegistry _items;
    private readonly RecipeBook _recipes;

    public Furnaces(ItemRegistry items, RecipeBook recipes)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
    }

    public int Count => _furnaces.Count;

    /// <summary>Pec na téhle souřadnici. Když tam žádná není, založí se prázdná.</summary>
    public Furnace At(Vector3i block)
    {
        if (!_furnaces.TryGetValue(block, out Furnace? furnace))
        {
            furnace = new Furnace();
            _furnaces[block] = furnace;
        }

        return furnace;
    }

    /// <summary>Vysype obsah pece. Volá se, když ji hráč rozbije.</summary>
    public IEnumerable<ItemStack> Remove(Vector3i block)
    {
        if (!_furnaces.Remove(block, out Furnace? furnace))
        {
            return [];
        }

        return furnace.Contents;
    }

    /// <summary>Posune tavení ve všech pecích.</summary>
    public void Update(float deltaSeconds)
    {
        foreach (Furnace furnace in _furnaces.Values)
        {
            Step(furnace, deltaSeconds);
        }
    }

    /// <summary>
    /// Jeden krok jedné pece.
    /// </summary>
    /// <remarks>
    /// <para><b>Topení a tavení jsou oddělené.</b> Zapnutá pec hoří, i když v ní není co
    /// tavit — tak se z dřeva dělá uhlí. Dřív se palivo zapalovalo jen tehdy, když byla
    /// surovina, takže pec nešlo použít jako milíř.</para>
    ///
    /// <para><b>Když dojde palivo, pec se vypne sama.</b> Kdyby zůstala zapnutá, spálila by
    /// první kus dřeva, který do ní hráč později dá, aniž by o to stál.</para>
    /// </remarks>
    private void Step(Furnace furnace, float deltaSeconds)
    {
        if (!furnace.Running)
        {
            return;
        }

        if (!furnace.Burning && !TryLightFuel(furnace))
        {
            // Došlo palivo. Pec zhasne a čeká, až ji hráč znovu naloží a zapne.
            furnace.Running = false;
            return;
        }

        Burn(furnace, deltaSeconds);

        for (int slot = 0; slot < Furnace.InputSlots; slot++)
        {
            Smelt(furnace, slot, deltaSeconds);
        }
    }

    /// <summary>Posune tavení v jednom vstupním slotu.</summary>
    private void Smelt(Furnace furnace, int slot, float deltaSeconds)
    {
        ItemStack input = furnace.Input[slot];

        if (FindRecipe(input) is not { } entry || !HasRoom(furnace, entry.Output, entry.Count))
        {
            furnace.Progress[slot] = 0f;
            return;
        }

        furnace.Progress[slot] += deltaSeconds / SmeltSeconds;

        if (furnace.Progress[slot] < 1f)
        {
            return;
        }

        furnace.Progress[slot] = 0f;
        furnace.Input[slot] = input.WithCount(input.Count - 1);

        Deposit(furnace, entry.Output, entry.Count);
    }

    private static void Burn(Furnace furnace, float deltaSeconds)
    {
        if (furnace.BurnLeft > 0f)
        {
            furnace.BurnLeft = MathF.Max(0f, furnace.BurnLeft - deltaSeconds);
        }
    }

    /// <summary>
    /// Zapálí kus paliva, když je čím, a uloží, co po něm zbylo.
    /// </summary>
    /// <remarks>
    /// <para><b>Uhlí zbývá po dřevě.</b> Není to tavení a nemá to recept: hráč přiloží
    /// dřevo a uhel je vedlejší produkt topení. Proto se to řeší tady a ne v receptech.</para>
    ///
    /// <para><b>Bez místa na uhel se dřevo nezapálí.</b> Kdyby shořelo a uhel se zahodil,
    /// přišel by hráč o dřevo i o uhlí, aniž by se cokoli stalo — a vypadalo by to, jako
    /// že se palivo ztrácí samo.</para>
    /// </remarks>
    private bool TryLightFuel(Furnace furnace)
    {
        if (furnace.Fuel.IsEmpty)
        {
            return false;
        }

        ItemDefinition definition = _items.Definition(furnace.Fuel.Item);

        if (definition.BurnSeconds <= 0f)
        {
            return false;
        }

        int residue = string.IsNullOrEmpty(definition.BurnResidue)
            ? ItemRegistry.Nothing
            : _items.IndexOf(definition.BurnResidue);

        if (residue != ItemRegistry.Nothing && !HasRoom(furnace, residue, 1))
        {
            return false;
        }

        furnace.Fuel = furnace.Fuel.WithCount(furnace.Fuel.Count - 1);
        furnace.BurnLeft = definition.BurnSeconds;
        furnace.BurnTotal = definition.BurnSeconds;

        if (residue != ItemRegistry.Nothing)
        {
            Deposit(furnace, residue, 1);
        }

        return true;
    }

    /// <summary>
    /// Uloží výsledek do výstupu: nejdřív dorovná rozdělanou hromádku, pak zabere prázdný slot.
    /// </summary>
    /// <remarks>
    /// Pořadí je schválně tohle. Kdyby se bral první volný slot, rozpadlo by se dvacet
    /// ingotů na tři hromádky po několika kusech a výstup by byl plný, i když se do něj
    /// vejde ještě spousta.
    /// </remarks>
    private void Deposit(Furnace furnace, int item, int count)
    {
        int max = _items.Definition(item).MaxStack;

        for (int slot = 0; slot < Furnace.OutputSlots; slot++)
        {
            ItemStack stack = furnace.Output[slot];

            if (!stack.IsEmpty && stack.Item == item && stack.Count + count <= max)
            {
                furnace.Output[slot] = stack.WithCount(stack.Count + count);
                return;
            }
        }

        for (int slot = 0; slot < Furnace.OutputSlots; slot++)
        {
            if (furnace.Output[slot].IsEmpty)
            {
                furnace.Output[slot] = new ItemStack(item, count, 0);
                return;
            }
        }
    }

    /// <summary>Recept tavení pro danou surovinu, nebo nic.</summary>
    public RecipeBook.Entry? FindRecipe(ItemStack input)
    {
        if (input.IsEmpty)
        {
            return null;
        }

        foreach (RecipeBook.Entry entry in _recipes.Smelting)
        {
            if (entry.Inputs.Length == 1 && entry.Inputs[0].Item == input.Item
                && input.Count >= entry.Inputs[0].Count)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Vejde se tolik kusů daného předmětu do výstupu?</summary>
    private bool HasRoom(Furnace furnace, int item, int count)
    {
        int max = _items.Definition(item).MaxStack;

        foreach (ItemStack stack in furnace.Output)
        {
            if (stack.IsEmpty || (stack.Item == item && stack.Count + count <= max))
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>Je ten předmět palivo?</summary>
    public bool IsFuel(ItemStack stack) =>
        !stack.IsEmpty && _items.Definition(stack.Item).BurnSeconds > 0f;

    /// <summary>
    /// Čtyři bajty na začátku souboru. Chrání před čtením něčeho cizího.
    /// </summary>
    /// <remarks>
    /// <b>Číslo se zvedlo z FRN1 na FRN2</b>, protože pec má nově šest slotů místo tří
    /// a vypínač. Starý soubor se tím zahodí místo aby se přečetl jako nesmysl — pec byla
    /// dosud jen chvíli, takže je to levnější než převodní cesta, kterou by nikdo nepoužil.
    /// </remarks>
    private const uint LegacyMagic = 0x324E5246; // "FRN2"
    private const uint Magic = 0x334E5246; // "FRN3"

    /// <summary>
    /// Zapíše obsah všech pecí do souboru.
    /// </summary>
    /// <remarks>
    /// <para><b>Prázdné pece se nezapisují.</b> Záznam vzniká už tím, že hráč pec otevře
    /// (<see cref="At"/> ji založí), takže by se jinak ukládala každá pec, na kterou kdy
    /// někdo klikl — včetně těch, do kterých nic nedal.</para>
    ///
    /// <para>Selhání zápisu shodit hru nesmí. Ztráta obsahu pece je nepříjemná, pád při
    /// ukončování je horší — a hráč by přišel i o svět.</para>
    /// </remarks>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            List<KeyValuePair<Vector3i, Furnace>> full =
                [.. _furnaces.Where(pair => !pair.Value.IsEmpty)];

            if (full.Count == 0)
            {
                // Prázdný seznam znamená smazat soubor. Kdyby zůstal ležet, obnovila by se
                // z něj při dalším spuštění pec, kterou hráč mezitím vybral.
                File.Delete(path);
                return;
            }

            string? directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);

            writer.Write(Magic);
            writer.Write(full.Count);

            foreach ((Vector3i at, Furnace furnace) in full)
            {
                writer.Write(at.X);
                writer.Write(at.Y);
                writer.Write(at.Z);

                WriteStack(writer, furnace.Fuel);

                for (int slot = 0; slot < Furnace.InputSlots; slot++)
                {
                    WriteStack(writer, furnace.Input[slot]);
                    writer.Write(furnace.Progress[slot]);
                }

                for (int slot = 0; slot < Furnace.OutputSlots; slot++)
                {
                    WriteStack(writer, furnace.Output[slot]);
                }

                writer.Write(furnace.BurnLeft);
                writer.Write(furnace.BurnTotal);
                writer.Write(furnace.Running);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Engine.Core.Log.Warn($"Obsah pecí se nepodařilo uložit: {error.Message}");
        }
    }

    /// <summary>
    /// Načte obsah pecí ze souboru. Chybějící ani poškozený soubor není chyba.
    /// </summary>
    /// <returns>Kolik pecí se obnovilo.</returns>
    /// <remarks>
    /// <para><b>Předmět, který v registru chybí, se zahodí i s hromádkou.</b> Stane se to,
    /// když se ze hry odstraní blok nebo předmět; alternativou by bylo shodit načítání
    /// celého světa kvůli jedné peci.</para>
    /// </remarks>
    public int Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            uint magic = reader.ReadUInt32();
            if (magic is not (Magic or LegacyMagic))
            {
                Engine.Core.Log.Warn($"Soubor {path} není seznam pecí, přeskakuje se.");
                return 0;
            }

            bool legacyStacks = magic == LegacyMagic;
            int count = reader.ReadInt32();
            int restored = 0;

            for (int i = 0; i < count; i++)
            {
                var at = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

                var furnace = new Furnace { Fuel = ReadStack(reader, legacyStacks) };

                for (int slot = 0; slot < Furnace.InputSlots; slot++)
                {
                    furnace.Input[slot] = ReadStack(reader, legacyStacks);
                    furnace.Progress[slot] = reader.ReadSingle();
                }

                for (int slot = 0; slot < Furnace.OutputSlots; slot++)
                {
                    furnace.Output[slot] = ReadStack(reader, legacyStacks);
                }

                furnace.BurnLeft = reader.ReadSingle();
                furnace.BurnTotal = reader.ReadSingle();
                furnace.Running = reader.ReadBoolean();

                if (furnace.IsEmpty)
                {
                    continue;
                }

                _furnaces[at] = furnace;
                restored++;
            }

            return restored;
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or InvalidDataException)
        {
            Engine.Core.Log.Warn($"Obsah pecí se nepodařilo načíst: {error.Message}");
            return 0;
        }
    }

    private void WriteStack(BinaryWriter writer, ItemStack stack)
    {
        ItemStackCodec.Write(writer, stack, _items);
    }

    private ItemStack ReadStack(BinaryReader reader, bool legacy)
    {
        return legacy
            ? ItemStackCodec.ReadLegacy(reader, _items)
            : ItemStackCodec.Read(reader, _items);
    }
}
