namespace Tesseris.Loader;

/// <summary>A fail-closed loader error containing package, owner or path information where available.</summary>
public sealed class LoaderException : Exception
{
    public LoaderException(string message) : base(message) { }

    public LoaderException(string message, Exception innerException) : base(message, innerException) { }
}
