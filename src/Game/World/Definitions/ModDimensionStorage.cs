using System.Security.Cryptography;
using System.Text;
using Tesseris.ModApi;

namespace Tesseris.Game.World.Definitions;

public static class ModDimensionStorage
{
    public static string Key(ResourceId dimensionId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(dimensionId.Value));
        return "dim-" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string Directory(string worldDirectory, ResourceId dimensionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldDirectory);
        string root = Path.GetFullPath(worldDirectory);
        string dimensionsRoot = Path.GetFullPath(Path.Combine(root, "dimensions"));
        string candidate = Path.GetFullPath(Path.Combine(dimensionsRoot, Key(dimensionId)));
        string prefix = dimensionsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? dimensionsRoot
            : dimensionsRoot + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Dimension '{dimensionId}' resolved outside the world directory.");
        }

        return candidate;
    }
}
