using OpenTK.Mathematics;
using Tesseris.Engine.Core;

namespace Tesseris.Game.Colony;

/// <summary>
/// Uložení a načtení kolonie: pásy, stroje, vkládače, lidé a označená práce.
/// </summary>
/// <remarks>
/// <para><b>Bez tohohle nemá stavění smysl.</b> Postavená linka je jediná věc, do které hráč
/// investoval čas — když ji restart smaže, je celá automatizace jen ukázka.</para>
///
/// <para><b>Odvozený stav se neukládá.</b> Cesty kolonistů, navigační mřížky ani režim stroje
/// se nezapisují: cesta se doplánuje (0,858 ms), mřížka postaví a režim se odvodí z pásů
/// a proudu. Uložený odvozený stav se dřív nebo později rozejde se skutečností — a hůř, po
/// načtení by vypadal správně.</para>
///
/// <para><b>Indexy se při ukládání zhušťují.</b> Banky si po zbourání drží místo v poli (viz
/// <see cref="MachineBank.Remove"/>), takže do souboru jdou jen živé prvky a odkazy na ně se
/// přečíslují. Jinak by sav rostl o každý zbouraný pás.</para>
///
/// <para><b>Formát je stejný jako u truhel a pecí:</b> magické číslo, verze, počty, atomický
/// přepis přes <c>.tmp</c>. Načtení poškozeného souboru kolonii vyprázdní a jen varuje —
/// spadnout kvůli savu je horší než přijít o linku.</para>
/// </remarks>
public static class ColonySave
{
    /// <summary>Jméno souboru ve složce světa.</summary>
    public const string FileName = "kolonie.dat";

    private const uint Magic = 0x4C4F434B;

    /// <summary>
    /// Verze 4 nese polohu kolonisty spojitě, ne jako buňku.
    /// </summary>
    /// <remarks>
    /// <para><b>Starší sav se nenačte, jen se zahodí s varováním.</b> Verze 3 nesla polohu
    /// jako <c>Vector3i</c>. Dosadit za ni střed buňky by šlo, ale kolonista uložený v půlce
    /// kroku by se po načtení posunul — a hlavně by se tichý dopočet nedal odlišit od
    /// skutečně uložené hodnoty. Kolonie je zatím rozehraná věc a přijít o ni je lepší než ji
    /// mít vymyšlenou.</para>
    ///
    /// <para><b>Svislá rychlost se neukládá.</b> Je to okamžik pádu, ne stav světa; po načtení
    /// se spočítá z toho, jestli je pod nohama zem. Uložit ji by znamenalo obnovit kolonistu
    /// uprostřed letu, což vypadá jako chyba načtení.</para>
    /// </remarks>
    private const int Version = 4;

    // Stropy proti poškozenému souboru: přečtený počet se použije na alokaci.
    private const int MaximumBelts = 100_000;
    private const int MaximumMachines = 100_000;
    private const int MaximumInserters = 100_000;
    private const int MaximumColonists = 4_096;
    private const int MaximumStoreKinds = 65_536;
    private const int MaximumJobs = 4_000_000;

    /// <summary>Uloží kolonii. Prázdnou kolonii uloží jako smazaný soubor.</summary>
    public static void Save(ColonyRuntime colony, string path)
    {
        ArgumentNullException.ThrowIfNull(colony);
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            if (IsEmpty(colony))
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";

            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);

                // RADNICE PRVNÍ. Nese polohu, ze které se odvozuje i sklad — bez ní se dá
                // po načtení do skladu odevzdávat, ale nedá se k němu dojít, takže se v něm
                // nikdo nenají. Uložený hlad by pak nebyl tlak, ale past: kolonie by stála
                // vedle plného skladu jídla a trvale jela na poloviční výkon.
                writer.Write(colony.TownHall.IsFounded);
                if (colony.TownHall.IsFounded)
                {
                    WriteCell(writer, colony.TownHall.Cell);
                    writer.Write(colony.TownHall.Arrived);
                    writer.Write(colony.TownHall.TicksSinceArrival);
                }

                Dictionary<BeltSegment, int> beltIndex = WriteBelts(writer, colony);
                int[] machineIndex = WriteMachines(writer, colony, beltIndex);
                WriteInserters(writer, colony, beltIndex, machineIndex);
                WriteColonists(writer, colony, machineIndex);
                WriteJobs(writer, colony);
            }

            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Kolonii se nepodarilo ulozit: {error.Message}");
        }
    }

    /// <summary>
    /// Načte kolonii. Vrací, kolik prvků se obnovilo; nula znamená, že sav nebyl nebo byl vadný.
    /// </summary>
    public static int Load(ColonyRuntime colony, string path)
    {
        ArgumentNullException.ThrowIfNull(colony);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != Magic)
            {
                throw new InvalidDataException("Neznamy format kolonie.");
            }

            int version = reader.ReadInt32();
            if (version != Version)
            {
                throw new InvalidDataException($"Kolonie je z verze {version}, cekala se {Version}.");
            }

            colony.Clear();

            if (reader.ReadBoolean())
            {
                Vector3i hall = ReadCell(reader);
                colony.RestoreTownHall(hall, reader.ReadInt32(), reader.ReadInt32());
            }

            BeltSegment[] belts = ReadBelts(reader, colony);
            int[] machines = ReadMachines(reader, colony, belts);
            ReadInserters(reader, colony, belts, machines);
            int restoredColonists = ReadColonists(reader, colony, machines);
            int restoredJobs = ReadJobs(reader, colony);

            return belts.Length + machines.Length + restoredColonists + restoredJobs;
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or InvalidDataException)
        {
            // Rozdělaně načtená kolonie je horší než žádná: půlka pásů bez strojů by se
            // tvářila jako platný stav.
            colony.Clear();
            Log.Warn($"Kolonii se nepodarilo nacist: {error.Message}");
            return 0;
        }
    }

    private static bool IsEmpty(ColonyRuntime colony) =>
        colony.Placements.Count == 0
        && colony.MachineCount == 0
        && colony.InserterCount == 0
        && colony.Colonists.Count == 0

        // SKLAD S OBSAHEM UŽ PRÁZDNÁ KOLONIE NENÍ. Bez tohohle by se jídlo naskladněné do
        // prázdné kolonie při uložení tiše smazalo.
        && colony.Store.Total == 0
        && colony.Jobs.OpenCount == 0
        && colony.Jobs.ClaimedCount == 0
        && colony.Jobs.DeferredCount == 0;

    private static Dictionary<BeltSegment, int> WriteBelts(BinaryWriter writer, ColonyRuntime colony)
    {
        IReadOnlyList<ColonyRuntime.BeltPlacement> placements = colony.Placements;
        var index = new Dictionary<BeltSegment, int>(placements.Count);

        writer.Write(placements.Count);
        for (int i = 0; i < placements.Count; i++)
        {
            ColonyRuntime.BeltPlacement placement = placements[i];
            index[placement.Belt] = i;

            WriteCell(writer, placement.Start);
            WriteCell(writer, placement.Direction);
            writer.Write(placement.Belt.Cells);

            // Sloty doslova i s mezerami: na pásu JE poloha itemu celý stav.
            writer.Write(placement.Belt.Count);
            for (int slot = 0; slot < placement.Belt.Count; slot++)
            {
                BeltSlot value = placement.Belt.SlotAt(slot);
                writer.Write(value.Gap);
                writer.Write(value.ItemId);
            }
        }

        return index;
    }

    private static BeltSegment[] ReadBelts(BinaryReader reader, ColonyRuntime colony)
    {
        int count = ReadCount(reader, MaximumBelts, "pasu");
        var belts = new BeltSegment[count];
        var slots = new List<BeltSlot>();

        for (int i = 0; i < count; i++)
        {
            Vector3i start = ReadCell(reader);
            Vector3i direction = ReadCell(reader);
            int cells = reader.ReadInt32();

            if (cells is < 1 or > 4096)
            {
                throw new InvalidDataException($"Neplatna delka pasu: {cells}.");
            }

            BeltSegment belt = colony.PlaceBelt(start, cells, direction);

            int items = ReadCount(reader, MaximumBelts, "itemu na pasu");
            slots.Clear();
            for (int slot = 0; slot < items; slot++)
            {
                slots.Add(new BeltSlot { Gap = reader.ReadUInt16(), ItemId = reader.ReadUInt16() });
            }

            belt.Restore(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(slots));
            belts[i] = belt;
        }

        return belts;
    }

    /// <returns>Převod ze starého indexu stroje na nový, nebo −1 pro zbourané.</returns>
    private static int[] WriteMachines(
        BinaryWriter writer,
        ColonyRuntime colony,
        Dictionary<BeltSegment, int> belts)
    {
        MachineBank machines = colony.Machines;
        var mapping = new int[machines.Count];
        int live = 0;

        for (int machine = 0; machine < machines.Count; machine++)
        {
            mapping[machine] = machines.IsRemoved(machine) ? -1 : live++;
        }

        writer.Write(live);
        for (int machine = 0; machine < machines.Count; machine++)
        {
            if (mapping[machine] < 0)
            {
                continue;
            }

            WriteCell(writer, machines.CellOf(machine));
            writer.Write(machines.InputItemOf(machine));
            writer.Write(machines.OutputItemOf(machine));
            writer.Write(machines.TicksPerCraftOf(machine));
            writer.Write(machines.InputOf(machine));
            writer.Write(machines.OutputOf(machine));
            writer.Write(machines.ProgressOf(machine));
            writer.Write(machines.IsPowered(machine));
            writer.Write(BeltIndex(belts, machines.InputBeltOf(machine)));
            writer.Write(BeltIndex(belts, machines.OutputBeltOf(machine)));
        }

        return mapping;
    }

    private static int[] ReadMachines(BinaryReader reader, ColonyRuntime colony, BeltSegment[] belts)
    {
        int count = ReadCount(reader, MaximumMachines, "stroju");
        var machines = new int[count];

        for (int i = 0; i < count; i++)
        {
            Vector3i cell = ReadCell(reader);
            ushort inputItem = reader.ReadUInt16();
            ushort outputItem = reader.ReadUInt16();
            int ticksPerCraft = Math.Max(1, reader.ReadInt32());
            int input = reader.ReadInt32();
            int output = reader.ReadInt32();
            int progress = reader.ReadInt32();
            bool powered = reader.ReadBoolean();
            int inputBelt = reader.ReadInt32();
            int outputBelt = reader.ReadInt32();

            int machine = colony.PlaceCrusher(cell, inputItem, outputItem, ticksPerCraft);
            machines[i] = machine;

            if (BeltAt(belts, inputBelt) is { } feed)
            {
                colony.Machines.SetInputBelt(machine, feed);
            }

            if (BeltAt(belts, outputBelt) is { } drain)
            {
                colony.Machines.SetOutputBelt(machine, drain);
            }

            colony.Machines.SetPowered(machine, powered);
            colony.Machines.Restore(machine, input, output, progress);
        }

        return machines;
    }

    private static void WriteInserters(
        BinaryWriter writer,
        ColonyRuntime colony,
        Dictionary<BeltSegment, int> belts,
        int[] machineMapping)
    {
        InserterBank inserters = colony.Inserters;

        int live = 0;
        for (int inserter = 0; inserter < inserters.Count; inserter++)
        {
            if (!inserters.IsRemoved(inserter))
            {
                live++;
            }
        }

        writer.Write(live);
        for (int inserter = 0; inserter < inserters.Count; inserter++)
        {
            if (inserters.IsRemoved(inserter))
            {
                continue;
            }

            WriteCell(writer, inserters.CellOf(inserter));
            writer.Write(inserters.TicksPerMoveOf(inserter));
            writer.Write((byte)inserters.SourceKindOf(inserter));
            writer.Write((byte)inserters.TargetKindOf(inserter));
            writer.Write(BeltIndex(belts, inserters.SourceBeltOf(inserter)));
            writer.Write(BeltIndex(belts, inserters.TargetBeltOf(inserter)));
            writer.Write(MachineIndex(machineMapping, inserters.SourceIndexOf(inserter)));
            writer.Write(MachineIndex(machineMapping, inserters.TargetIndexOf(inserter)));
        }
    }

    private static void ReadInserters(
        BinaryReader reader,
        ColonyRuntime colony,
        BeltSegment[] belts,
        int[] machines)
    {
        int count = ReadCount(reader, MaximumInserters, "vkladacu");

        for (int i = 0; i < count; i++)
        {
            Vector3i cell = ReadCell(reader);
            int ticksPerMove = Math.Max(1, reader.ReadInt32());
            var sourceKind = (InserterEnd)reader.ReadByte();
            var targetKind = (InserterEnd)reader.ReadByte();
            BeltSegment? sourceBelt = BeltAt(belts, reader.ReadInt32());
            BeltSegment? targetBelt = BeltAt(belts, reader.ReadInt32());
            int sourceMachine = MachineAt(machines, reader.ReadInt32());
            int targetMachine = MachineAt(machines, reader.ReadInt32());

            // Vkládač, kterému chybí konec, se nestaví. Bez jednoho konce by jen budil sám
            // sebe a v UI by vypadal jako funkční kus linky — stejný důvod jako u bourání.
            bool built = (sourceKind, targetKind) switch
            {
                (InserterEnd.Belt, InserterEnd.Machine) when sourceBelt is not null && targetMachine >= 0 =>
                    Built(colony.Inserters.AddBeltToMachine(cell, sourceBelt, targetMachine, ticksPerMove)),

                (InserterEnd.Machine, InserterEnd.Belt) when sourceMachine >= 0 && targetBelt is not null =>
                    Built(colony.Inserters.AddMachineToBelt(
                        colony.Machines, cell, sourceMachine, targetBelt, ticksPerMove)),

                (InserterEnd.Belt, InserterEnd.Belt) when sourceBelt is not null && targetBelt is not null =>
                    Built(colony.Inserters.AddBeltToBelt(cell, sourceBelt, targetBelt, ticksPerMove)),

                _ => false,
            };

            if (built)
            {
                colony.CountInserter();
            }
            else
            {
                Log.Warn($"Vkladac na {cell} nemel po nacteni oba konce, vynechan.");
            }
        }

        static bool Built(int id) => id >= 0;
    }

    private static void WriteColonists(BinaryWriter writer, ColonyRuntime colony, int[] machineMapping)
    {
        ColonySimulation colonists = colony.Colonists;

        // U koho stojí operátor. Ukládá se ze strany stroje, protože tam ta vazba bydlí.
        var operatorOf = new int[colonists.Count];
        Array.Fill(operatorOf, -1);

        for (int machine = 0; machine < colony.Machines.Count; machine++)
        {
            if (colony.Machines.IsRemoved(machine))
            {
                continue;
            }

            int colonist = colony.Machines.OperatorOf(machine);
            if (colonist >= 0 && colonist < operatorOf.Length)
            {
                operatorOf[colonist] = machineMapping[machine];
            }
        }

        writer.Write(colonists.Count);
        for (int colonist = 0; colonist < colonists.Count; colonist++)
        {
            WritePosition(writer, colonists.PositionOf(colonist));
            writer.Write(colonists.CarryingOf(colonist));
            writer.Write(operatorOf[colonist]);

            // HLAD SE UKLÁDÁ. Bez toho by restart nakrmil celou kolonii zadarmo, což je
            // přesně ten druh tichého úniku tlaku, kvůli kterému hlad vzniká.
            writer.Write(colonists.HungerOf(colonist));
        }

        WriteStore(writer, colonists.Store);
        writer.Write(colonists.DeliveredToFactory);
    }

    /// <summary>
    /// Zapíše obsah skladu po druzích.
    /// </summary>
    /// <remarks>
    /// <b>Pořadí je dané tříděním ve skladu</b>, ne pořadím příchodu, takže kolečko
    /// uložit → načíst → uložit vyjde bajt po bajtu stejně.
    /// </remarks>
    private static void WriteStore(BinaryWriter writer, ColonyStore store)
    {
        writer.Write(store.KindCount);
        for (int kind = 0; kind < store.KindCount; kind++)
        {
            writer.Write(store.ItemAt(kind));
            writer.Write(store.CountAt(kind));
        }
    }

    private static void ReadStore(BinaryReader reader, ColonyStore store)
    {
        int kinds = ReadCount(reader, MaximumStoreKinds, "druhu ve skladu");
        for (int kind = 0; kind < kinds; kind++)
        {
            ushort item = reader.ReadUInt16();
            int count = reader.ReadInt32();

            if (count < 0)
            {
                throw new InvalidDataException($"Neplatny pocet ve skladu: {count}.");
            }

            store.Add(item, count);
        }
    }

    private static int ReadColonists(BinaryReader reader, ColonyRuntime colony, int[] machines)
    {
        int count = ReadCount(reader, MaximumColonists, "kolonistu");

        for (int i = 0; i < count; i++)
        {
            Vector3 position = ReadPosition(reader);
            ushort carrying = reader.ReadUInt16();
            int operatorOf = reader.ReadInt32();
            int hunger = reader.ReadInt32();

            int colonist = colony.Colonists.Restore(position, carrying, hunger);

            int machine = MachineAt(machines, operatorOf);
            if (machine >= 0)
            {
                colony.TryAssignOperator(machine, colonist);
            }
        }

        ReadStore(reader, colony.Colonists.Store);
        colony.Colonists.RestoreCounters(reader.ReadInt32());
        return count;
    }

    private static void WriteJobs(BinaryWriter writer, ColonyRuntime colony)
    {
        DigJobQueue jobs = colony.Jobs;

        int pending = 0;
        for (int job = 0; job < jobs.Count; job++)
        {
            if (IsPending(jobs.StateOf(job)))
            {
                pending++;
            }
        }

        // HOTOVÉ ÚKOLY SE NEUKLÁDAJÍ. Vykopaný blok je ve světě a označení už nic neznamená;
        // zapisovat by se rovnalo tomu, nechat sav růst o každý kopnutý voxel.
        writer.Write(pending);
        for (int job = 0; job < jobs.Count; job++)
        {
            if (IsPending(jobs.StateOf(job)))
            {
                WriteCell(writer, jobs.TargetOf(job));
            }
        }
    }

    private static int ReadJobs(BinaryReader reader, ColonyRuntime colony)
    {
        int count = ReadCount(reader, MaximumJobs, "ukolu");

        // Zabraný úkol se vrací jako volný: cesty se stejně neobnovují, takže by ho nikdo
        // nedokončil a zůstal by zabraný napořád.
        for (int i = 0; i < count; i++)
        {
            colony.Jobs.Add(ReadCell(reader));
        }

        if (count > 0)
        {
            colony.Colonists.WakeIdle();
        }

        return count;
    }

    private static bool IsPending(JobState state) =>
        state is JobState.Open or JobState.Claimed or JobState.Deferred;

    private static int BeltIndex(Dictionary<BeltSegment, int> belts, BeltSegment? belt) =>
        belt is not null && belts.TryGetValue(belt, out int index) ? index : -1;

    private static BeltSegment? BeltAt(BeltSegment[] belts, int index) =>
        index >= 0 && index < belts.Length ? belts[index] : null;

    private static int MachineIndex(int[] mapping, int machine) =>
        machine >= 0 && machine < mapping.Length ? mapping[machine] : -1;

    private static int MachineAt(int[] machines, int index) =>
        index >= 0 && index < machines.Length ? machines[index] : -1;

    private static void WriteCell(BinaryWriter writer, Vector3i cell)
    {
        writer.Write(cell.X);
        writer.Write(cell.Y);
        writer.Write(cell.Z);
    }

    private static Vector3i ReadCell(BinaryReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

    /// <summary>
    /// Zapíše spojitou polohu.
    /// </summary>
    /// <remarks>
    /// <b>Tři <c>float</c>y doslova, žádné zaokrouhlení.</b> Kolečko uložit → načíst → uložit
    /// musí vyjít bajt po bajtu stejně, a <c>BinaryWriter</c> zapisuje IEEE 754 bez ztráty.
    /// Kdyby se poloha ukládala zaokrouhleně, druhý zápis by se od prvního lišil.
    /// </remarks>
    private static void WritePosition(BinaryWriter writer, Vector3 position)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
        writer.Write(position.Z);
    }

    private static Vector3 ReadPosition(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static int ReadCount(BinaryReader reader, int maximum, string what)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException($"Neplatny pocet {what}: {count}.");
        }

        return count;
    }
}
