using System.IO.Compression;
using Tesseris.Launcher;
using Xunit;

namespace Tesseris.Tests;

public sealed class RuntimePayloadExtractorTests
{
    [Fact]
    public void Extract_publishes_content_addressed_runtime_and_reuses_it()
    {
        using var temporary = new TemporaryDirectory();
        byte[] payload = CreatePayload(("Tesseris.dll", "game"), ("assets/blocks/stone.json", "{}"));

        ExtractedRuntime first = RuntimePayloadExtractor.Extract(new MemoryStream(payload), temporary.Path);
        DateTime markerTime = File.GetLastWriteTimeUtc(Path.Combine(first.Directory, ".complete"));
        ExtractedRuntime second = RuntimePayloadExtractor.Extract(new MemoryStream(payload), temporary.Path);

        Assert.Equal(first, second);
        Assert.Equal("game", File.ReadAllText(Path.Combine(first.Directory, "Tesseris.dll")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(first.Directory, "assets", "blocks", "stone.json")));
        Assert.Equal(markerTime, File.GetLastWriteTimeUtc(Path.Combine(second.Directory, ".complete")));
        Assert.DoesNotContain(Directory.EnumerateDirectories(temporary.Path), path =>
            Path.GetFileName(path).StartsWith(".staging-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("folder/../../outside.dll")]
    [InlineData("C:/outside.dll")]
    public void Extract_rejects_unsafe_archive_paths(string entry)
    {
        using var temporary = new TemporaryDirectory();
        byte[] payload = CreatePayload(("Tesseris.dll", "game"), (entry, "escape"));

        Assert.Throws<InvalidDataException>(() =>
            RuntimePayloadExtractor.Extract(new MemoryStream(payload), temporary.Path));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "outside.dll")));
    }

    [Fact]
    public void Extract_requires_game_assembly_and_does_not_publish_partial_runtime()
    {
        using var temporary = new TemporaryDirectory();
        byte[] payload = CreatePayload(("assets/readme.txt", "missing game"));

        Assert.Throws<InvalidDataException>(() =>
            RuntimePayloadExtractor.Extract(new MemoryStream(payload), temporary.Path));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Path));
    }

    private static byte[] CreatePayload(params (string Path, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string content) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TesserisRuntimePayloadTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
