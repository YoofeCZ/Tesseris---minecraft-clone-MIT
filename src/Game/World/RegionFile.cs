using System.Buffers.Binary;
using System.IO.Compression;
using OpenTK.Mathematics;

namespace Tesseris.Game.World;

/// <summary>
/// Jeden soubor se skupinou chunků.
///
/// <para><b>Proč ne soubor na chunk.</b> Při dohledu 12 a světě vysokém 1024 bloků se
/// běžně dotýká víc než deset tisíc chunků. Soubor na každý znamená deset tisíc položek
/// v adresáři, deset tisíc otevření a u většiny z nich pár set bajtů dat — režie
/// souborového systému by převážila vlastní data. Region drží
/// <c>16 × 16</c> sloupců přes celou výšku světa, tedy <c>16 × 16 × 32 = 8192</c> slotů
/// v jednom souboru.</para>
///
/// <para><b>Rozvržení.</b> Na začátku značka a verze, pak tabulka slotů (u každého pozice
/// a délka v bajtech), za ní data. Nulová délka znamená „tenhle chunk uložený není".</para>
///
/// <para><b>Přepis rostoucího chunku se připíše na konec</b> a staré místo se nechá ležet.
/// Je to o řád jednodušší než správa volného místa a hra chunk přepisuje řádově jednotky
/// krát za sezení. Kdyby soubory narostly, patří sem setřásání — zatím by to byla
/// optimalizace bez naměřeného důvodu.</para>
///
/// <para><b>Souběh.</b> Třída si drží otevřený <see cref="FileStream"/> a všechna volání
/// jdou pod zámkem. Ukládání běží na worker vláknech, takže se sem sahá z několika najednou.</para>
/// </summary>
public sealed class RegionFile : IDisposable
{
    /// <summary>Kolik sloupců chunků má region na stranu.</summary>
    public const int ColumnsPerRegion = 16;

    /// <summary>Kolik slotů má region celkem. Výška světa se do regionu vejde celá.</summary>
    public static int SlotCount => ColumnsPerRegion * ColumnsPerRegion * TerrainGenerator.WorldHeightChunks;

    private const uint Magic = 0x31524B56; // "VKR1"
    private const int Version = 1;
    private const int HeaderBytes = 8;

    private static int TableBytes => SlotCount * 8;

    private readonly FileStream _file;
    private readonly int[] _offsets;
    private readonly int[] _lengths;
    private readonly object _lock = new();

    private bool _tableDirty;

    private RegionFile(FileStream file, int[] offsets, int[] lengths)
    {
        _file = file;
        _offsets = offsets;
        _lengths = lengths;
    }

    /// <summary>Otevře region, nebo založí prázdný, když soubor ještě neexistuje.</summary>
    public static RegionFile Open(string path)
    {
        var offsets = new int[SlotCount];
        var lengths = new int[SlotCount];

        FileStream file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        if (file.Length >= HeaderBytes + TableBytes)
        {
            file.Position = 0;

            Span<byte> header = stackalloc byte[HeaderBytes];
            file.ReadExactly(header);

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            int version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);

            if (magic != Magic || version != Version)
            {
                file.Dispose();
                throw new InvalidDataException(
                    $"Soubor {path} není region Tesseris verze {Version} (značka {magic:X8}, verze {version}).");
            }

            var table = new byte[TableBytes];
            file.ReadExactly(table);

            for (int i = 0; i < SlotCount; i++)
            {
                offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 8));
                lengths[i] = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan((i * 8) + 4));
            }
        }
        else
        {
            // Nový nebo useknutý soubor: založí se hlavička a prázdná tabulka.
            file.SetLength(0);
            file.Position = 0;

            Span<byte> header = stackalloc byte[HeaderBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
            file.Write(header);

            file.Write(new byte[TableBytes]);
            file.Flush();
        }

        return new RegionFile(file, offsets, lengths);
    }

    /// <summary>Slot pro chunk podle jeho pozice ve světě.</summary>
    public static int SlotOf(Vector3i chunkPosition)
    {
        int x = Mod(chunkPosition.X, ColumnsPerRegion);
        int z = Mod(chunkPosition.Z, ColumnsPerRegion);

        return chunkPosition.Y + (TerrainGenerator.WorldHeightChunks * (x + (z * ColumnsPerRegion)));
    }

    /// <summary>Souřadnice regionu, do kterého chunk patří.</summary>
    public static Vector2i RegionOf(Vector3i chunkPosition) => new(
        FloorDiv(chunkPosition.X, ColumnsPerRegion),
        FloorDiv(chunkPosition.Z, ColumnsPerRegion));

    /// <summary>Zapíše chunk do slotu. Data se komprimují deflatem.</summary>
    public void Write(int slot, ReadOnlySpan<byte> payload)
    {
        byte[] compressed = Compress(payload);

        lock (_lock)
        {
            // Vejde se to na původní místo? Pak se přepíše tam, jinak se připíše na konec.
            bool reuse = _lengths[slot] >= compressed.Length && _offsets[slot] > 0;
            int offset = reuse ? _offsets[slot] : (int)Math.Max(_file.Length, HeaderBytes + TableBytes);

            _file.Position = offset;
            _file.Write(compressed);

            _offsets[slot] = offset;
            _lengths[slot] = compressed.Length;
            _tableDirty = true;
        }
    }

    /// <summary>Přečte chunk ze slotu, nebo vrátí null, když v něm nic není.</summary>
    public byte[]? Read(int slot)
    {
        lock (_lock)
        {
            if (_lengths[slot] == 0)
            {
                return null;
            }

            var compressed = new byte[_lengths[slot]];
            _file.Position = _offsets[slot];
            _file.ReadExactly(compressed);

            return Decompress(compressed);
        }
    }

    /// <summary>Uloží tabulku slotů a vyprázdní zápisovou cache operačního systému.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_tableDirty)
            {
                return;
            }

            var table = new byte[TableBytes];

            for (int i = 0; i < SlotCount; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(i * 8), _offsets[i]);
                BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan((i * 8) + 4), _lengths[i]);
            }

            _file.Position = HeaderBytes;
            _file.Write(table);
            _file.Flush(flushToDisk: true);

            _tableDirty = false;
        }
    }

    public void Dispose()
    {
        Flush();
        _file.Dispose();
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();

        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        deflate.CopyTo(output);
        return output.ToArray();
    }

    // Zbytek i dělení musí fungovat i pro záporné souřadnice. Operátor % v C# vrací
    // u záporných čísel záporný zbytek, takže by chunk na x = -1 spadl do slotu -1.
    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : ((value + 1) / divisor) - 1;
}
