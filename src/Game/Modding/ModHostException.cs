namespace Tesseris.Game.Modding;

public sealed class ModHostException : Exception
{
    public ModHostException(string message)
        : base(message)
    {
    }

    public ModHostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
