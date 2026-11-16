using System.IO.Compression;
using System.Numerics;
using DotRecast.Detour;
using DeepDungeon.Fsd.Dalamud.Map;
using Lumina;
using vnavmesh.Build.Scene;
using vnavmesh.Build.Ground;
using vnavmesh.Common.Build;
using vnavmesh.Common.Models;

namespace DeepDungeon.Fsd.Terrain;

public static class TerrainBaker
{
    private static readonly object Gate = new();

    public static List<string> BakeCapture(CapturedTerrainScene captured, string sqpack, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        // Upstream's file-source adapter is static; only one scene can own it.
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var game = new GameData(sqpack);
            vnavmesh.Service.DataManager = game;
            try
            {
                var layouts = captured.Layouts.Select(l => {
                    var map = new FloorLayout(l.Index);
                    foreach (var (cell, center) in l.Centers) map.Centers.Add(cell, center);
                    map.Seeds.AddRange(l.Seeds);
                    map.Doors.AddRange(l.Doors.Select(d => new FloorDoor(d.Cell, d.Connection, d.Box)));
                    return map;
                }).ToList();
                return BakeScene(captured.Dungeon, captured.Set - 1, captured.Territory,
                    new SceneExtractor(captured.Scene), layouts, outputDirectory, cancellationToken);
            }
            finally { vnavmesh.Service.DataManager = null!; }
        }
    }
    public static List<string> BakeScene(uint dungeon, int set, uint territory, SceneExtractor scene, List<FloorLayout> layouts, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"SCENE {territory} meshes={scene.Meshes.Count} instances={scene.Meshes.Values.Sum(m=>m.Instances.Count)}");
        var settings = new NavmeshSettings { Flyable = false, GenerateEdgeClimbLinks = false, GenerateEdgeJumpLinks = false,
            AgentRadius = DeepDungeon.Fsd.Core.FloorTerrainProfile.BakedBodyRadius,
            CellSize = .125f, CellHeight = .1f,
            Filtering = vnavmesh.Common.Build.Ground.Enums.NavmeshFilter.LedgeSpans | vnavmesh.Common.Build.Ground.Enums.NavmeshFilter.WalkableLowHeightSpans };
        var builder = new NavmeshBuilder(scene, settings);
        builder.Build(_ => cancellationToken.ThrowIfCancellationRequested());
        var mesh = builder.Navmesh.Mesh;
        var polygons = new Dictionary<long, FloorPolygon>();
        for (int i = 0; i < mesh.GetMaxTiles(); i++)
        {
            var tile = mesh.GetTile(i);
            if (tile?.data?.header == null) continue;
            for (int j = 0; j < tile.data.header.polyCount; j++)
            {
                var poly = tile.data.polys[j];
                if (poly.GetPolyType() != 0 || (poly.flags & 1) == 0) continue;
                polygons.Add(mesh.GetPolyRefBase(tile) | (uint)j, new(tile, poly,
                    poly.verts.Take(poly.vertCount).Select(v => NavmeshBitmap.GetVertex(tile, v)).ToArray()));
            }
        }
        var generated = new List<string>();
        var normalPolygons = new HashSet<long>();
        foreach (var map in layouts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queue = new Queue<long>();
            foreach (var seed in map.Seeds)
            {
                var nearest = polygons.MinBy(p => p.Value.DistanceSquared(seed));
                float distance = nearest.Value.DistanceSquared(seed);
                if (map.Index >= 3 && normalPolygons.Contains(nearest.Key)) continue;
                if (map.Index >= 3 && distance > 25) continue;
                if (map.Index < 3 && distance > 36)
                {
                    var detail = new NavmeshBitmap(seed - new Vector3(35, 5, 35), seed + new Vector3(35, 5, 35), .25f);
                    foreach (var poly in polygons.Values) detail.RasterizePolygon(poly.Tile, poly.Poly);
                    detail.Save(Path.Combine(outputDirectory, $"{territory}-anchor.bmp"));
                    throw new InvalidDataException($"{territory}/{map.Index}: no floor at {seed}; nearest={MathF.Sqrt(distance):F2}m, vertices={string.Join(';', nearest.Value.Vertices)}");
                }
                queue.Enqueue(nearest.Key);
            }
            if (queue.Count == 0) throw new InvalidDataException($"{territory}/{map.Index}: no reachable polygon");
            var reached = new HashSet<long>(); var bounds = AABB.Empty;
            while (queue.TryDequeue(out long id))
            {
                if (!reached.Add(id)) continue;
                var poly = polygons[id];
                foreach (var p in poly.Vertices) { bounds.Min = Vector3.Min(bounds.Min, p); bounds.Max = Vector3.Max(bounds.Max, p); }
                for (var link = poly.Poly.firstLink; link != DtDetour.DT_NULL_LINK; link = poly.Tile.links[link].next)
                    if (polygons.ContainsKey(poly.Tile.links[link].refs)) queue.Enqueue(poly.Tile.links[link].refs);
            }
            if (map.Index < 3) normalPolygons.UnionWith(reached);
            var bitmap = new NavmeshBitmap(bounds.Min - Vector3.One, bounds.Max + Vector3.One, .25f);
            foreach (long id in reached) { var p = polygons[id]; bitmap.RasterizePolygon(p.Tile, p.Poly); }
            string name = $"{dungeon}-{set + 1}-{territory}-{map.Index}.map.gz";
            using (var output = File.Create(Path.Combine(outputDirectory, name + ".tmp")))
            using (var zip = new GZipStream(output, CompressionLevel.SmallestSize))
            using (var w = new BinaryWriter(zip))
            {
                w.Write(bitmap.Width); w.Write(bitmap.Height); w.Write(bitmap.MinBounds.X); w.Write(bitmap.MinBounds.Z); w.Write(bitmap.PixelSize);
                w.Write(map.Centers.Count);
                foreach (var (cell, p) in map.Centers) { w.Write(cell); w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                w.Write(map.Doors.Count);
                foreach (var door in map.Doors)
                {
                    w.Write(door.Cell); w.Write(door.Connection); var m = door.Box;
                    w.Write(m.M11); w.Write(m.M13); w.Write(m.M21); w.Write(m.M23);
                    w.Write(m.M31); w.Write(m.M33); w.Write(m.M41); w.Write(m.M43);
                }
                w.Write(bitmap.Pixels);
            }
            File.Move(Path.Combine(outputDirectory, name + ".tmp"), Path.Combine(outputDirectory, name), true);
            generated.Add(name); Console.WriteLine($"MAP {name} {bitmap.Width}x{bitmap.Height}");
        }
        return generated;
    }
}

public sealed class FloorLayout(int index)
{
    public int Index => index;
    public Dictionary<int, Vector3> Centers { get; } = [];
    public List<Vector3> Seeds { get; } = [];
    public List<FloorDoor> Doors { get; } = [];
}
public readonly record struct FloorDoor(int Cell, int Connection, Matrix4x4 Box);
internal sealed record FloorPolygon(DtMeshTile Tile, DtPoly Poly, Vector3[] Vertices)
{
    internal float DistanceSquared(Vector3 p)
    {
        bool inside = false; float nearest = float.MaxValue;
        for (int i = 0, j = Vertices.Length - 1; i < Vertices.Length; j = i++)
        {
            var a = Vertices[i]; var b = Vertices[j];
            if ((a.Z > p.Z) != (b.Z > p.Z) && p.X < (b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
            var edge = b - a; float t = Math.Clamp(Vector3.Dot(p - a, edge) / MathF.Max(edge.LengthSquared(), 1e-8f), 0, 1);
            nearest = Math.Min(nearest, Vector3.DistanceSquared(p, a + edge * t));
        }
        if (!inside) return nearest;
        float dy = p.Y - Vertices.Average(v => v.Y); return dy * dy;
    }
}
