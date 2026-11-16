using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using ColliderType = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType;

namespace vnavmesh.Build.Scene
{
    // The upstream extractor's data contract, shared with the standalone baker.
    public sealed class SceneDefinition
    {
        public List<string> Terrains = [];
        public Dictionary<uint, (Transform transform, Vector3 bbMin, Vector3 bbMax)> AnalyticShapes = [];
        public Dictionary<uint, string> MeshPaths = [];
        public List<(ulong key, Transform transform, uint crc, ulong matId, ulong matMask, bool analytic)> BgParts = [];
        public List<(ulong key, Transform transform, uint crc, ulong matId, ulong matMask, ColliderType type)> Colliders = [];
        public List<(ulong key, Transform transform)> ExitRanges = [];
    }
}

namespace DeepDungeon.Fsd.Dalamud.Map
{
    public sealed class CapturedTerrainScene
    {
        public uint Dungeon, Territory;
        public int Set, ObservedFloor;
        public vnavmesh.Build.Scene.SceneDefinition Scene = new();
        public List<CapturedTerrainLayout> Layouts = [];
    }

    public sealed class CapturedTerrainLayout
    {
        public int Index;
        public Dictionary<int, Vector3> Centers = [];
        public List<Vector3> Seeds = [];
        public List<CapturedTerrainDoor> Doors = [];
    }

    public sealed record CapturedTerrainDoor(int Cell, int Connection, Matrix4x4 Box);
}
