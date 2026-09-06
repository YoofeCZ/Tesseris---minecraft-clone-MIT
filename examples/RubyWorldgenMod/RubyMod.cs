using Tesseris.ModApi;

namespace RubyWorldgen;

public sealed class RubyMod : IMod
{
    public void Configure(IModContext context)
    {
        context.WorldGeneration.Register(
            WorldGenerationStage.Features,
            new ResourceId("ruby:ore_veins"),
            priority: 0,
            new RubyOreHook());

        context.Events.OnStarted(() =>
            context.Logger.Info("Ruby content and deterministic ore generation are active."));
    }
}

internal sealed class RubyOreHook : IChunkGenerationHook
{
    private const string RubyOre = "ruby:ruby_ore";
    private const string VanillaStone = "tesseris:stone";

    public void Generate(IChunkGenerationContext context)
    {
        for (int vein = 0; vein < 3; vein++)
        {
            int x = context.Random.NextInt(context.SizeX);
            int y = context.Random.NextInt(context.SizeY);
            int z = context.Random.NextInt(context.SizeZ);

            for (int step = 0; step < 7; step++)
            {
                if (context.GetBlockId(x, y, z) == VanillaStone)
                {
                    context.SetBlock(x, y, z, RubyOre);
                }

                x = Math.Clamp(x + context.Random.NextInt(-1, 2), 0, context.SizeX - 1);
                y = Math.Clamp(y + context.Random.NextInt(-1, 2), 0, context.SizeY - 1);
                z = Math.Clamp(z + context.Random.NextInt(-1, 2), 0, context.SizeZ - 1);
            }
        }
    }
}
