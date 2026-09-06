using OpenTK.Mathematics;
using Tesseris.Game.World;
using Xunit;

namespace Tesseris.Tests;

/// <summary>Kompatibilita s kvádrovými modely a animacemi z Hytale editorů.</summary>
public sealed class HytaleBlockModelTests
{
    [Fact]
    public void Zrcadlene_uv_se_od_offsetu_rozklada_zpet()
    {
        HytaleBlockModel model = HytaleBlockModel.Parse("""
        { "nodes": [{ "name": "panel", "position": { "x": 0, "y": 0, "z": 0 },
          "shape": { "type": "box", "offset": { "x": 0, "y": 6, "z": 0 },
            "settings": { "size": { "x": 12, "y": 12, "z": 2 } },
            "textureLayout": { "front": {
              "offset": { "x": 20, "y": 4 }, "mirror": { "x": true, "y": false }, "angle": 0
            }} }
        }] }
        """);

        ItemShape.Face front = model.BuildFaces(32, 32)[0];
        float low = (8f + 0.125f) / 32f;
        float high = (20f - 0.125f) / 32f;

        Assert.InRange(front.U0.X, low - 0.001f, high + 0.001f);
        Assert.InRange(front.U1.X, low - 0.001f, high + 0.001f);
        Assert.True(front.U0.X > front.U1.X);
    }

    [Fact]
    public void Model_zachova_presah_a_transformaci_potomka()
    {
        HytaleBlockModel model = HytaleBlockModel.Parse("""
        { "nodes": [{ "name": "base", "position": { "x": 0, "y": 0, "z": 0 },
          "orientation": { "x": 0, "y": 0, "z": 0, "w": 1 },
          "shape": { "type": "box", "offset": { "x": 0, "y": 16, "z": 0 }, "settings": { "size": { "x": 32, "y": 32, "z": 32 } } },
          "children": [{ "name": "chimney", "position": { "x": 0, "y": 32, "z": 0 },
            "orientation": { "x": 0, "y": 0, "z": 0, "w": 1 },
            "shape": { "type": "box", "offset": { "x": 0, "y": 16, "z": 0 }, "settings": { "size": { "x": 16, "y": 32, "z": 16 } } }
          }]
        }] }
        """);

        HytaleBlockModel.Node baseNode = Assert.Single(model.Nodes, node => node.Name == "base");
        HytaleBlockModel.Node chimney = Assert.Single(model.Nodes, node => node.Name == "chimney");

        Assert.Equal(new Vector3(0f, 0.5f, 0f), baseNode.Box!.Value.Centre);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.25f), chimney.Box!.Value.HalfSize);
        Assert.Equal(2f, chimney.Box!.Value.Centre.Y, 3);
    }

    [Fact]
    public void Animace_dveri_interpoluje_quaternion_a_drzí_konec()
    {
        HytaleBlockAnimation animation = HytaleBlockAnimation.Parse("""
        { "formatVersion": 1, "duration": 30, "holdLastKeyframe": true,
          "nodeAnimations": { "door": { "orientation": [
            { "time": 0, "delta": { "x": 0, "y": 0, "z": 0, "w": 1 }, "interpolationType": "smooth" },
            { "time": 30, "delta": { "x": 0, "y": 1, "z": 0, "w": 0 }, "interpolationType": "smooth" }
          ]}}
        }
        """);

        Quaternion halfway = animation.OrientationAt("door", 15f);
        Quaternion closed = animation.OrientationAt("door", 0f);
        Quaternion open = animation.OrientationAt("door", 90f);

        Assert.InRange(MathF.Abs(halfway.Y), 0.70f, 0.72f);
        Assert.Equal(1f, closed.W, 3);
        Assert.InRange(MathF.Abs(open.Y), 0.999f, 1f);
    }
}
