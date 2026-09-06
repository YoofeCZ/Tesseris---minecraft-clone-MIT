using OpenTK.Mathematics;
using Tesseris.Game.Blocks;

namespace Tesseris.Game.World;

/// <summary>Jeden dveřní nebo trapdoor panel právě přecházející mezi dvěma stavy.</summary>
public readonly record struct BuildingPartAnimation(
    Vector3i Block,
    ushort LowerBlock,
    ushort UpperBlock,
    BlockShape Shape,
    byte FromState,
    byte ToState,
    float Progress);

/// <summary>Staví malou dynamickou mesh plynule otočených stavebních panelů.</summary>
public static class BuildingPartAnimationMesher
{
    public static void Build(
        MeshBuffer mesh,
        BlockRegistry registry,
        IEnumerable<BuildingPartAnimation> animations)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(animations);

        mesh.Clear();
        foreach (BuildingPartAnimation animation in animations)
        {
            float progress = Smooth(Math.Clamp(animation.Progress, 0f, 1f));
            if (animation.Shape == BlockShape.Door)
            {
                AddDoor(mesh, registry, animation, progress);
            }
            else if (animation.Shape == BlockShape.Trapdoor)
            {
                AddTrapdoor(mesh, registry, animation, progress);
            }
            else if (animation.Shape == BlockShape.Chest)
            {
                AddChest(mesh, registry, animation, progress);
            }
        }
    }

    private static void AddDoor(
        MeshBuffer mesh,
        BlockRegistry registry,
        in BuildingPartAnimation animation,
        float progress)
    {
        byte state = PieceMask.DoorFinalState(animation.ToState);
        int facing = PieceMask.DoorFacing(state);
        bool hingeRight = PieceMask.DoorHingeRight(state);
        Vector3 forward = DoorForward(facing);
        Vector3 right = new(PieceMask.DoorRightStep(facing).X, 0f, PieceMask.DoorRightStep(facing).Z);
        Vector3 closed = hingeRight ? -right : right;
        Vector3 opened = forward;

        bool fromOpen = PieceMask.DoorIsOpen(animation.FromState);
        bool toOpen = PieceMask.DoorIsOpen(animation.ToState);
        Vector3 from = fromOpen ? opened : closed;
        Vector3 to = toOpen ? opened : closed;
        Vector3 free = RotateBetween(from, to, progress);

        Vector3 localHinge = facing switch
        {
            PieceMask.DoorEast => new Vector3(0f, 0f, hingeRight ? 1f : 0f),
            PieceMask.DoorWest => new Vector3(1f, 0f, hingeRight ? 0f : 1f),
            PieceMask.DoorNorth => new Vector3(hingeRight ? 1f : 0f, 0f, 1f),
            _ => new Vector3(hingeRight ? 0f : 1f, 0f, 0f),
        };
        Vector3 origin = new(animation.Block.X, animation.Block.Y, animation.Block.Z);
        Vector3 hinge = origin + localHinge;

        AddPrism(
            mesh, hinge, free, Vector3.UnitY,
            registry.FaceLayer(animation.LowerBlock, BlockFace.PosZ));
        AddPrism(
            mesh, hinge + Vector3.UnitY, free, Vector3.UnitY,
            registry.FaceLayer(animation.UpperBlock, BlockFace.PosZ));
    }

    private static void AddTrapdoor(
        MeshBuffer mesh,
        BlockRegistry registry,
        in BuildingPartAnimation animation,
        float progress)
    {
        byte fromState = PieceMask.TrapdoorFinalState(animation.FromState);
        byte toState = PieceMask.TrapdoorFinalState(animation.ToState);
        bool top = PieceMask.TrapdoorIsTop(toState);
        bool alongZ = PieceMask.TrapdoorIsAlongZ(toState);
        float fromOpen = PieceMask.TrapdoorIsOpen(fromState) ? 1f : 0f;
        float toOpen = PieceMask.TrapdoorIsOpen(toState) ? 1f : 0f;
        float angle = MathHelper.PiOver2 * MathHelper.Lerp(fromOpen, toOpen, progress);

        Vector3 horizontal = alongZ ? Vector3.UnitX : Vector3.UnitZ;
        Vector3 vertical = top ? -Vector3.UnitY : Vector3.UnitY;
        Vector3 swing = (horizontal * MathF.Cos(angle)) + (vertical * MathF.Sin(angle));
        Vector3 hingeAxis = alongZ ? Vector3.UnitZ : Vector3.UnitX;
        Vector3 localHinge = top ? Vector3.UnitY : Vector3.Zero;
        Vector3 origin = new(animation.Block.X, animation.Block.Y, animation.Block.Z);

        AddPrism(
            mesh, origin + localHinge, hingeAxis, swing,
            registry.FaceLayer(animation.LowerBlock, BlockFace.PosY));
    }

    private static void AddChest(
        MeshBuffer mesh,
        BlockRegistry registry,
        in BuildingPartAnimation animation,
        float progress)
    {
        float fromOpen = PieceMask.ChestIsOpen(animation.FromState) ? 1f : 0f;
        float toOpen = PieceMask.ChestIsOpen(animation.ToState) ? 1f : 0f;
        AddChest(mesh, registry, animation.LowerBlock,
            new Vector3(animation.Block.X, animation.Block.Y, animation.Block.Z),
            MathHelper.Lerp(fromOpen, toOpen, progress), animation.ToState);
    }

    internal static void AddChest(
        MeshBuffer mesh, BlockRegistry registry, ushort blockId,
        Vector3 block, float openAmount, byte state)
    {
        const float TextureSeam = 20f / 64f;
        int facing = PieceMask.ChestFacing(state);
        ChestPairSide pair = PieceMask.ChestPair(state);
        float x0 = pair == ChestPairSide.Left ? 0f : 1f / 16f;
        float x1 = pair == ChestPairSide.Right ? 1f : 15f / 16f;
        float u0 = pair == ChestPairSide.Left ? 0.5f : 0f;
        float u1 = pair == ChestPairSide.Right ? 0.5f : 1f;
        bool capNegX = pair != ChestPairSide.Left;
        bool capPosX = pair != ChestPairSide.Right;

        Vector3 bodyOrigin = RotateChestPoint(block, new Vector3(x0, 0f, 1f / 16f), facing);
        Vector3 bodyX = RotateChestVector(new Vector3(x1 - x0, 0f, 0f), facing);
        Vector3 bodyY = Vector3.UnitY * (10f / 16f);
        Vector3 bodyZ = RotateChestVector(new Vector3(0f, 0f, 14f / 16f), facing);
        AddParallelepiped(
            mesh, registry, blockId,
            bodyOrigin, bodyX, bodyY, bodyZ,
            sideVMin: TextureSeam, sideVMax: 1f,
            interiorTop: true, u0, u1, capNegX, capPosX);

        float amount = Math.Clamp(openAmount, 0f, 1f);
        float angle = MathHelper.DegreesToRadians(105f) * amount;
        Vector3 towardFront = RotateChestVector(new Vector3(
            0f, MathF.Sin(angle) * 14f / 16f, MathF.Cos(angle) * 14f / 16f), facing);
        Vector3 thickness = RotateChestVector(new Vector3(
            0f, MathF.Cos(angle) * 4f / 16f, -MathF.Sin(angle) * 4f / 16f), facing);
        Vector3 lidOrigin = RotateChestPoint(
            block, new Vector3(x0, 10f / 16f, 1f / 16f), facing);

        AddParallelepiped(
            mesh, registry, blockId,
            lidOrigin,
            RotateChestVector(new Vector3(x1 - x0, 0f, 0f), facing),
            thickness,
            towardFront,
            sideVMin: 0f, sideVMax: TextureSeam,
            interiorTop: false, u0, u1, capNegX, capPosX);
    }

    private static void AddParallelepiped(
        MeshBuffer mesh,
        BlockRegistry registry,
        ushort blockId,
        Vector3 origin,
        Vector3 axisX,
        Vector3 axisY,
        Vector3 axisZ,
        float sideVMin,
        float sideVMax,
        bool interiorTop,
        float uMin,
        float uMax,
        bool capNegX,
        bool capPosX)
    {
        Vector3 p000 = origin;
        Vector3 p100 = origin + axisX;
        Vector3 p010 = origin + axisY;
        Vector3 p110 = origin + axisX + axisY;
        Vector3 p001 = origin + axisZ;
        Vector3 p101 = origin + axisX + axisZ;
        Vector3 p011 = origin + axisY + axisZ;
        Vector3 p111 = origin + axisX + axisY + axisZ;
        var side00 = new Vector2(uMin, sideVMax);
        var side10 = new Vector2(uMax, sideVMax);
        var side11 = new Vector2(uMax, sideVMin);
        var side01 = new Vector2(uMin, sideVMin);
        var full00 = new Vector2(uMin, 1f);
        var full10 = new Vector2(uMax, 1f);
        var full11 = new Vector2(uMax, 0f);
        var full01 = new Vector2(uMin, 0f);
        var top00 = new Vector2(uMin, 0f);
        var top01 = new Vector2(uMin, 1f);
        var top11 = new Vector2(uMax, 1f);
        var top10 = new Vector2(uMax, 0f);
        bool reverseWinding = Vector3.Dot(Vector3.Cross(axisX, axisY), axisZ) < 0f;

        void SideQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float layer, float shade)
        {
            float rigidShade = shade + ChunkMesher.StaticCutoutFlag;
            if (reverseWinding)
            {
                mesh.AddQuad(d, c, b, a, side01, side11, side10, side00,
                    layer, rigidShade, rigidShade, rigidShade, rigidShade, false);
            }
            else
            {
                mesh.AddQuad(a, b, c, d, side00, side10, side11, side01,
                    layer, rigidShade, rigidShade, rigidShade, rigidShade, false);
            }
        }

        void CapQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float layer, float shade)
        {
            float rigidShade = shade + ChunkMesher.StaticCutoutFlag;
            var cap00 = new Vector2(0f, sideVMax);
            var cap10 = new Vector2(1f, sideVMax);
            var cap11 = new Vector2(1f, sideVMin);
            var cap01 = new Vector2(0f, sideVMin);
            if (reverseWinding)
            {
                mesh.AddQuad(d, c, b, a, cap01, cap11, cap10, cap00,
                    layer, rigidShade, rigidShade, rigidShade, rigidShade, false);
            }
            else
            {
                mesh.AddQuad(a, b, c, d, cap00, cap10, cap11, cap01,
                    layer, rigidShade, rigidShade, rigidShade, rigidShade, false);
            }
        }

        void FullQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float layer, float shade)
        {
            float rigidShade = shade + ChunkMesher.StaticCutoutFlag;
            if (reverseWinding)
            {
                mesh.AddQuad(d, c, b, a, full01, full11, full10, full00,
                    layer, rigidShade, rigidShade, rigidShade, rigidShade, false);
            }
            else
            {
                mesh.AddQuad(a, b, c, d, full00, full10, full11, full01,
                    layer, rigidShade, rigidShade, rigidShade, rigidShade, false);
            }
        }

        void TopQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float layer, float shade)
        {
            float rigidShade = shade + ChunkMesher.StaticCutoutFlag;
            if (reverseWinding)
            {
                mesh.AddQuad(d, c, b, a, top10, top11, top01, top00,
                    layer, rigidShade, rigidShade, rigidShade, rigidShade, false);
            }
            else
            {
                mesh.AddQuad(a, b, c, d, top00, top01, top11, top10,
                    layer, rigidShade, rigidShade, rigidShade, rigidShade, false);
            }
        }

        SideQuad(p001, p101, p111, p011, registry.FaceLayer(blockId, BlockFace.PosZ), 1f);
        SideQuad(p100, p000, p010, p110, registry.FaceLayer(blockId, BlockFace.NegZ), 0.82f);
        if (capNegX)
        {
            CapQuad(p000, p001, p011, p010, registry.FaceLayer(blockId, BlockFace.NegX), 0.72f);
        }
        if (capPosX)
        {
            CapQuad(p101, p100, p110, p111, registry.FaceLayer(blockId, BlockFace.PosX), 0.88f);
        }
        TopQuad(p010, p011, p111, p110,
            registry.FaceLayer(blockId, interiorTop ? BlockFace.NegY : BlockFace.PosY), 1f);
        FullQuad(p000, p100, p101, p001, registry.FaceLayer(blockId, BlockFace.NegY), 0.68f);
    }

    private static Vector3 RotateChestPoint(Vector3 block, Vector3 local, int facing) => facing switch
    {
        PieceMask.DoorEast => block + new Vector3(local.Z, local.Y, 1f - local.X),
        PieceMask.DoorNorth => block + new Vector3(1f - local.X, local.Y, 1f - local.Z),
        PieceMask.DoorWest => block + new Vector3(1f - local.Z, local.Y, local.X),
        _ => block + local,
    };

    private static Vector3 RotateChestVector(Vector3 vector, int facing) => facing switch
    {
        PieceMask.DoorEast => new Vector3(vector.Z, vector.Y, -vector.X),
        PieceMask.DoorNorth => new Vector3(-vector.X, vector.Y, -vector.Z),
        PieceMask.DoorWest => new Vector3(-vector.Z, vector.Y, vector.X),
        _ => vector,
    };

    private static void AddPrism(
        MeshBuffer mesh,
        Vector3 origin,
        Vector3 axisU,
        Vector3 axisV,
        float layer)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(axisU, axisV));
        Vector3 halfDepth = normal * (PieceMask.PanelThickness * 0.5f);
        Vector3 p00 = origin - halfDepth;
        Vector3 p10 = origin + axisU - halfDepth;
        Vector3 p11 = origin + axisU + axisV - halfDepth;
        Vector3 p01 = origin + axisV - halfDepth;
        Vector3 q00 = p00 + (halfDepth * 2f);
        Vector3 q10 = p10 + (halfDepth * 2f);
        Vector3 q11 = p11 + (halfDepth * 2f);
        Vector3 q01 = p01 + (halfDepth * 2f);

        var uv00 = new Vector2(0f, 1f);
        var uv10 = new Vector2(1f, 1f);
        var uv11 = new Vector2(1f, 0f);
        var uv01 = new Vector2(0f, 0f);

        void Quad(
            Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud,
            float shade) => mesh.AddQuad(
                a, b, c, d, ua, ub, uc, ud,
                layer, shade, shade, shade, shade, false);

        // Obě široké strany používají stejné fyzické UV: zezadu se kresba správně zrcadlí.
        Quad(q00, q10, q11, q01, uv00, uv10, uv11, uv01, 1f);
        Quad(p10, p00, p01, p11, uv10, uv00, uv01, uv11, 0.82f);
        Quad(p00, q00, q01, p01, uv00, uv10, uv11, uv01, 0.72f);
        Quad(q10, p10, p11, q11, uv00, uv10, uv11, uv01, 0.88f);
        Quad(q01, q11, p11, p01, uv00, uv10, uv11, uv01, 1f);
        Quad(p00, p10, q10, q00, uv00, uv10, uv11, uv01, 0.68f);
    }

    private static Vector3 DoorForward(int facing) => facing switch
    {
        PieceMask.DoorEast => Vector3.UnitX,
        PieceMask.DoorWest => -Vector3.UnitX,
        PieceMask.DoorNorth => -Vector3.UnitZ,
        _ => Vector3.UnitZ,
    };

    private static Vector3 RotateBetween(Vector3 from, Vector3 to, float progress)
    {
        float fromAngle = MathF.Atan2(from.X, from.Z);
        float toAngle = MathF.Atan2(to.X, to.Z);
        float delta = toAngle - fromAngle;
        if (delta > MathF.PI) delta -= MathF.Tau;
        if (delta < -MathF.PI) delta += MathF.Tau;
        float angle = fromAngle + (delta * progress);
        return new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
    }

    private static float Smooth(float value) => value * value * (3f - (2f * value));
}
