namespace Tesseris.Engine.Rendering;

/// <summary>
/// Uložení snímku do souboru BMP.
///
/// <para>
/// Formát je schválně ten nejhloupější možný: hlavička a syrové pixely, žádná komprese
/// ani knihovna. Smysl není v efektivitě, ale v tom, aby šlo <b>opravdu se podívat</b>,
/// co se vykreslilo. Chyba v orientaci stěn nebo souřadnic se z čísel nepozná — selftest
/// může hlásit samá OK a obraz přitom může být naruby.
/// </para>
/// </summary>
public static class Screenshot
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;

    /// <summary>
    /// Zapíše obraz RGBA se řádky shora dolů, tedy přesně v tom tvaru, jak ho vrací
    /// <c>GlRenderer.TakeCapture</c>.
    /// </summary>
    public static void WriteBmp(string path, ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(path);

        int expected = width * height * 4;
        if (rgba.Length < expected)
        {
            throw new ArgumentException(
                $"Obraz {width}x{height} potřebuje {expected} B, dostal jsem {rgba.Length} B.", nameof(rgba));
        }

        int pixelBytes = width * height * 4;
        byte[] file = new byte[FileHeaderSize + InfoHeaderSize + pixelBytes];

        Span<byte> output = file;

        // BITMAPFILEHEADER
        output[0] = (byte)'B';
        output[1] = (byte)'M';
        WriteInt32(output[2..], file.Length);
        WriteInt32(output[10..], FileHeaderSize + InfoHeaderSize);

        // BITMAPINFOHEADER
        WriteInt32(output[14..], InfoHeaderSize);
        WriteInt32(output[18..], width);

        // Záporná výška znamená řádky shora dolů. BMP je jinak zdola nahoru a bez tohohle
        // by byl uložený snímek vzhůru nohama — a přesně obrácený obraz se tady hledá,
        // takže by to bylo obzvlášť matoucí.
        WriteInt32(output[22..], -height);

        WriteInt16(output[26..], 1);   // roviny
        WriteInt16(output[28..], 32);  // bitů na pixel
        WriteInt32(output[34..], pixelBytes);

        Span<byte> pixels = output[(FileHeaderSize + InfoHeaderSize)..];
        for (int i = 0; i < width * height; i++)
        {
            int source = i * 4;
            int destination = i * 4;

            // BMP ukládá BGRA, vstup je RGBA.
            pixels[destination + 0] = rgba[source + 2];
            pixels[destination + 1] = rgba[source + 1];
            pixels[destination + 2] = rgba[source + 0];
            pixels[destination + 3] = 255;
        }

        File.WriteAllBytes(path, file);
    }

    private static void WriteInt32(Span<byte> destination, int value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination, value);

    private static void WriteInt16(Span<byte> destination, short value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(destination, value);
}
