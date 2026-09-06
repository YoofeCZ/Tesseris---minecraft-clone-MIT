using System.IO.Compression;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Tesseris.ModSdk.Tasks;

public sealed class CreateDeterministicArchive : Microsoft.Build.Utilities.Task
{
    private static readonly DateTimeOffset StableTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Required] public string SourceDirectory { get; set; } = string.Empty;
    [Required] public string DestinationFile { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            string source = Path.GetFullPath(SourceDirectory);
            string destination = Path.GetFullPath(DestinationFile);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);

            try
            {
                {
                    using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: false);
                    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                                 .OrderBy(path => Path.GetRelativePath(source, path).Replace('\\', '/'), StringComparer.Ordinal))
                    {
                        string entryName = Path.GetRelativePath(source, file).Replace('\\', '/');
                        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        entry.LastWriteTime = StableTimestamp;
                        using Stream input = File.OpenRead(file);
                        using Stream entryStream = entry.Open();
                        input.CopyTo(entryStream);
                    }
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.LogError($"Could not create Tesseris mod archive '{DestinationFile}': {exception.Message}");
            return false;
        }
    }
}
