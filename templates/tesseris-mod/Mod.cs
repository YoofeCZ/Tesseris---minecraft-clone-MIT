using Tesseris.ModApi;

namespace TesserisMod;

public sealed class Mod : IMod
{
    public void Configure(IModContext context)
    {
        context.Logger.Info("Example Mod configured.");
    }
}
