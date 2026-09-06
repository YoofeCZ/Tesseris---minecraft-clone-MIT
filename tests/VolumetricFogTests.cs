using Xunit;

namespace Tesseris.Tests;

/// <summary>
/// CPU model of the projection and layer clipping used by volumetric_fog.frag. A mistake
/// here is visual rather than an exception: fog would leak over nearby blocks or disappear
/// when viewed from above, so the two fragile transformations are pinned explicitly.
/// </summary>
public sealed class VolumetricFogTests
{
    private const float Near = 0.12f;
    private const float Far = 728f;

    private static float ProjectDepth(float viewDistance) =>
        (Far / (Far - Near)) - ((Far * Near) / ((Far - Near) * viewDistance));

    private static float ReconstructRayDistance(float depth, float rayLength)
    {
        float viewDistance = (Near * Far) / (Far - (depth * (Far - Near)));
        return viewDistance * rayLength;
    }

    [Theory]
    [InlineData(0.12f)]
    [InlineData(10f)]
    [InlineData(320f)]
    [InlineData(700f)]
    public void Vulkan_depth_round_trips_to_forward_distance(float distance)
    {
        float restored = ReconstructRayDistance(ProjectDepth(distance), 1f);

        Assert.InRange(MathF.Abs(restored - distance), 0f, distance * 0.001f + 0.0001f);
    }

    [Fact]
    public void Off_axis_depth_becomes_true_ray_distance()
    {
        const float forwardDistance = 240f;
        float rayLength = MathF.Sqrt(1f + (0.8f * 0.8f) + (0.45f * 0.45f));

        float restored = ReconstructRayDistance(ProjectDepth(forwardDistance), rayLength);

        float expected = forwardDistance * rayLength;
        Assert.InRange(MathF.Abs(restored - expected), 0f, expected * 0.001f);
    }

    [Fact]
    public void Downward_view_from_above_is_clipped_to_fog_layer()
    {
        const float sea = 320f;
        const float cameraY = 700f;
        const float directionY = -1f;
        float first = ((sea - 28f) - cameraY) / directionY;
        float second = ((sea + 230f) - cameraY) / directionY;
        float begin = MathF.Max(32f, MathF.Min(first, second));
        float end = MathF.Min(1400f, MathF.Max(first, second));

        Assert.Equal(150f, begin);
        Assert.Equal(408f, end);
        Assert.True(end > begin);
    }
}
