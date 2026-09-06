using System.Buffers.Binary;
using Tesseris.Engine.Core;
using Tesseris.Game.Blocks;
using Tesseris.Game.Micro;

namespace Tesseris.Game.World;

/// <summary>
/// Převod chunku na bajty a zpátky.
///
/// <para><b>Ukládá se vnitřní podoba, ne rozbalená mřížka.</b> Chunk už paletové kódování
/// má, takže se paleta i zabalené indexy zapíšou tak, jak jsou. Rozbalovat 32 768 voxelů
/// na dva bajty každý by dalo 64 kB místo 4 až 32 kB a při načítání by se musely balit
/// znovu.</para>
///
/// <para><b>Bez komprese.</b> Ta patří o vrstvu výš, do <see cref="RegionFile"/> — tady by
/// se míchaly dvě odpovědnosti a formát by nešlo otestovat bez toho, aby se procházel
/// deflatem.</para>
///
/// <para><b>V paletě jsou JMÉNA bloků, ne jejich indexy.</b> Registry se řadí abecedně,
/// takže přidání jediného typu bloku posune indexy všech, které jsou za ním — a uložený
/// svět by se tím tiše proměnil v nesmysl. Jméno zabere pár desítek bajtů na chunk, což
/// je proti čtyřem až dvaatřiceti kilobajtům indexů šum, a navíc se dobře komprimuje.</para>
///
/// <para>Formát je čistá funkce bez I/O, takže se dá otestovat kompletně: zakódovat,
/// dekódovat a porovnat voxel po voxelu.</para>
/// </summary>
public static class ChunkSerializer
{
    /// <summary>Čtyři bajty na začátku každého chunku. Chrání před čtením cizího souboru.</summary>
    private const uint Magic = 0x324B4843; // "CHK2"

    private const byte FlagHasIndices = 1 << 0;
    private const byte FlagHasMicro = 1 << 1;

    /// <summary>
    /// Chunk nese masky dílků, tedy strom sázený na dvakrát jemnější mřížce.
    ///
    /// <para>Je to nový bit v existujícím poli příznaků, ne nová verze formátu: starší
    /// uložený svět ho prostě nemá nastavený a načte se jako dřív.</para>
    /// </summary>
    private const byte FlagHasPieces = 1 << 2;

    /// <summary>Chunk nese druhý materiál uvnitř bloků, tedy listí kolem kmene.</summary>
    private const byte FlagHasExtra = 1 << 3;

    /// <summary>Zapíše chunk do pole bajtů.</summary>
    public static byte[] Encode(Chunk chunk, BlockRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(registry);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);

        byte flags = 0;
        if (!chunk.IsHomogeneous)
        {
            flags |= FlagHasIndices;
        }

        if (chunk.HasMicro)
        {
            flags |= FlagHasMicro;
        }

        if (chunk.HasPieces)
        {
            flags |= FlagHasPieces;
        }

        if (chunk.HasExtra)
        {
            flags |= FlagHasExtra;
        }

        writer.Write(flags);
        writer.Write((byte)chunk.BitsPerIndex);
        writer.Write((ushort)chunk.RawPaletteCount);

        for (int i = 0; i < chunk.RawPaletteCount; i++)
        {
            writer.Write(registry.Definition(chunk.RawPaletteAt(i)).Id);
        }

        if ((flags & FlagHasIndices) != 0)
        {
            ReadOnlySpan<ulong> indices = chunk.RawIndices;
            writer.Write(indices.Length);

            Span<byte> word = stackalloc byte[sizeof(ulong)];
            foreach (ulong value in indices)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(word, value);
                writer.Write(word);
            }
        }

        if ((flags & FlagHasMicro) != 0)
        {
            writer.Write((ushort)chunk.MicroCount);

            foreach ((int index, MicroBlock micro) in chunk.MicroBlocks)
            {
                writer.Write((ushort)index);
                writer.Write((byte)micro.MaterialCount);

                // Položka 0 je vždycky prázdno a neukládá se.
                for (int i = 1; i <= micro.MaterialCount; i++)
                {
                    writer.Write(registry.Definition(micro.RawPaletteAt(i)).Id);
                }

                writer.Write(micro.RawVoxels);
            }
        }

        if ((flags & FlagHasPieces) != 0)
        {
            writer.Write((ushort)chunk.PieceCount);

            foreach ((int index, byte mask) in chunk.Pieces)
            {
                writer.Write((ushort)index);
                writer.Write(mask);
            }
        }

        if ((flags & FlagHasExtra) != 0)
        {
            writer.Write((ushort)chunk.ExtraCount);

            foreach ((int index, ExtraPieces extra) in chunk.Extras)
            {
                writer.Write((ushort)index);
                writer.Write(registry.Definition(extra.Block).Id);
                writer.Write(extra.Mask);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Poskládá chunk z bajtů zapsaných <see cref="Encode"/>.</summary>
    public static Chunk Decode(ReadOnlySpan<byte> data, BlockRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var stream = new MemoryStream(data.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);

        uint magic = reader.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException(
                $"Chunk nezačíná očekávanou značkou (čekalo se {Magic:X8}, přišlo {magic:X8}).");
        }

        byte flags = reader.ReadByte();
        int bitsPerIndex = reader.ReadByte();
        int paletteCount = reader.ReadUInt16();

        if (paletteCount is < 1 or > Chunk.MaxPaletteEntries)
        {
            throw new InvalidDataException($"Paleta má {paletteCount} položek, což není platné.");
        }

        // Paleta musí mít kapacitu podle šířky indexu, ne podle počtu položek — jinak by
        // se do ní po načtení nedal přidat blok, aniž by se přerostla kapacita indexu.
        int capacity = bitsPerIndex == 0 ? 1 : 1 << bitsPerIndex;
        var palette = new ushort[Math.Max(capacity, paletteCount)];

        for (int i = 0; i < paletteCount; i++)
        {
            palette[i] = Resolve(registry, reader.ReadString());
        }

        ulong[]? indices = null;

        if ((flags & FlagHasIndices) != 0)
        {
            int words = reader.ReadInt32();
            int expected = Chunk.Volume * bitsPerIndex / 64;

            if (words != expected)
            {
                throw new InvalidDataException(
                    $"Pole indexů má {words} slov, ale při {bitsPerIndex} bitech jich má být {expected}.");
            }

            indices = new ulong[words];
            for (int i = 0; i < words; i++)
            {
                indices[i] = reader.ReadUInt64();
            }
        }

        Chunk chunk = Chunk.FromRaw(palette, paletteCount, indices, bitsPerIndex);

        if ((flags & FlagHasMicro) != 0)
        {
            int microCount = reader.ReadUInt16();

            for (int i = 0; i < microCount; i++)
            {
                int index = reader.ReadUInt16();
                int materials = reader.ReadByte();

                var microPalette = new ushort[materials + 1];
                for (int m = 1; m <= materials; m++)
                {
                    microPalette[m] = Resolve(registry, reader.ReadString());
                }

                byte[] voxels = reader.ReadBytes(MicroBlock.Volume);
                if (voxels.Length != MicroBlock.Volume)
                {
                    throw new InvalidDataException("Mikro mřížka je useknutá.");
                }

                chunk.SetMicroRaw(index, MicroBlock.FromRaw(voxels, microPalette, materials));
            }
        }

        if ((flags & FlagHasPieces) != 0)
        {
            int pieceCount = reader.ReadUInt16();

            for (int i = 0; i < pieceCount; i++)
            {
                int index = reader.ReadUInt16();
                chunk.SetPiecesRaw(index, reader.ReadByte());
            }
        }

        if ((flags & FlagHasExtra) != 0)
        {
            int extraCount = reader.ReadUInt16();

            for (int i = 0; i < extraCount; i++)
            {
                int index = reader.ReadUInt16();
                ushort block = Resolve(registry, reader.ReadString());
                chunk.SetExtraRaw(index, block, reader.ReadByte());
            }
        }

        // Načtený chunk je z definice upravený — kdyby nebyl, na disku by vůbec nebyl.
        chunk.MarkModified();
        return chunk;
    }

    /// <summary>
    /// Převede uložené jméno bloku na index v registry.
    ///
    /// <para>Blok, který v registry už není (odebrala ho novější verze hry, nebo mod),
    /// se změní na vzduch. Padat kvůli tomu by znamenalo, že jediný odebraný typ bloku
    /// znepřístupní celý svět.</para>
    /// </summary>
    private static ushort Resolve(BlockRegistry registry, string id)
    {
        if (registry.TryIndexOf(id, out ushort index))
        {
            return index;
        }

        Log.Warn($"Uložený blok '{id}' registry nezná, nahrazuje se vzduchem.");
        return BlockRegistry.Air;
    }
}
