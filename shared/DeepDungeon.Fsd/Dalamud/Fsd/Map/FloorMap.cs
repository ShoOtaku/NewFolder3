using System.IO.Compression;
using System.Numerics;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Core;
using MasterPlayer.MechanicRuntime;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Map;

internal readonly record struct FloorMapKey(uint Dungeon, int Set, uint Territory, int Layout)
{
    internal static unsafe FloorMapKey? For(InstanceContentDeepDungeon* dd)
    {
        var kind = DeepDungeonFloorClassifier.Classify(dd->DeepDungeonId, dd->Floor);
        if (kind == DeepDungeonFloorKind.Boss) return null;
        int layout = kind switch
        {
            DeepDungeonFloorKind.Result => 4,
            DeepDungeonFloorKind.Mob => FloorTerrainProfile.LayoutIndex(dd->LayoutInitializationType),
            _ => -1
        };
        return new(dd->DeepDungeonId, (dd->Floor - 1) / 10 + 1, Service.ClientState.TerritoryType, layout);
    }

    public override string ToString() => $"{Dungeon}-{Set}-{Territory}-{Layout}";
}

internal sealed class FloorMap
{
    internal static readonly NavigationClearance Clearance = new(FloorTerrainProfile.BakedBodyRadius, NavigationRange.MovementTolerance);
    internal readonly record struct Door(int Cell, int Connection, Matrix4x4 Box);

    private static FloorMapKey? cachedKey;
    private static FloorMap? cached;
    private static bool cachedClientMap;
    internal static string Error { get; private set; } = "";
    internal readonly Vector3?[] Centers = new Vector3?[25];
    internal FloorMapKey Key { get; }
    internal readonly Door[] Doors;
    internal readonly int Width;
    internal readonly int Height;
    internal readonly Vector3 Origin;
    internal readonly float PixelSize;
    internal readonly byte[] Bits;
    internal readonly int RowBytes;
    internal string Source { get; }
    internal float SourcePixelSize { get; }

    internal static FloorMap? Get(FloorMapKey key)
    {
        bool clientMap = LiveTerrainCapture.Ensure(key);
        if (key == cachedKey && clientMap == cachedClientMap) return cached;
        cachedKey = key;
        cachedClientMap = clientMap;
        cached = null;
        Error = "";
        try
        {
            using var source = clientMap ? File.OpenRead(LiveTerrainCapture.MapPath(key)) :
                typeof(FloorMap).Assembly.GetManifestResourceStream($"Fsd.FloorMaps.{key}.map.gz");
            if (source == null)
            {
                // A missing embedded map is expected while its first client bake runs.
                cachedKey = null;
                Error = LiveTerrainCapture.Status;
                return null;
            }
            using var stream = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new BinaryReader(stream);
            cached = new FloorMap(key, reader, clientMap);
        }
        catch (IOException ex)
        {
            ReportError(key, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            ReportError(key, ex.Message);
        }
        return cached;
    }

    internal static void Reset()
    {
        cachedKey = null;
        cached = null;
        Error = "";
    }

    private static void ReportError(FloorMapKey key, string detail)
    {
        Error = $"地宮 {key.Dungeon}、第 {key.Set} 組、區域 {key.Territory}、版型 {key.Layout}：{detail}";
        Service.Log.Error(Error);
    }

    private FloorMap(FloorMapKey key, BinaryReader reader, bool clientMap)
    {
        Key = key;
        Width = reader.ReadInt32();
        Height = reader.ReadInt32();
        Origin = new(reader.ReadSingle(), 0, reader.ReadSingle());
        PixelSize = reader.ReadSingle();
        var count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            Centers[reader.ReadInt32()] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        Doors = new Door[reader.ReadInt32()];
        for (int i = 0; i < Doors.Length; i++)
        {
            int cell = reader.ReadInt32(), connection = reader.ReadInt32();
            var m = Matrix4x4.Identity;
            m.M11 = reader.ReadSingle(); m.M13 = reader.ReadSingle();
            m.M21 = reader.ReadSingle(); m.M23 = reader.ReadSingle();
            m.M31 = reader.ReadSingle(); m.M33 = reader.ReadSingle();
            m.M41 = reader.ReadSingle(); m.M43 = reader.ReadSingle();
            Doors[i] = new(cell, connection, m);
        }
        RowBytes = ((Width + 31) / 32) * 4;
        var centers = new byte[checked(RowBytes * Height)];
        reader.BaseStream.ReadExactly(centers);
        Bits = centers;
        Source = clientMap ? "client-layout:vnav" : "embedded:client-layout:vnav";
        SourcePixelSize = PixelSize;
        float bakedRadius = FloorTerrainProfile.BakedBodyRadius;
        int border = Clearance.WholeCellBorder(PixelSize, bakedRadius);
        for (int i = 0; i < border; ++i) Bits = WholeCellObstacles(Bits, RowBytes, Height);
    }

    private static byte[] WholeCellObstacles(byte[] centers, int stride, int height)
    {
        // FSD admits every point in a free cell. Dilate by the caller's border
        // to cover cell extent, follower tolerance and any unbaked body radius.
        // Do this once per loaded base map, before current-floor door overlays.
        var result = new byte[centers.Length];
        Array.Fill(result, (byte)0xff);
        for (int y = 1; y < height - 1; ++y)
            for (int x = 0; x < stride; ++x)
            {
                int i = y * stride + x;
                int column = centers[i - stride] | centers[i] | centers[i + stride];
                int before = x == 0 ? 0xff :
                    centers[i - stride - 1] | centers[i - 1] | centers[i + stride - 1];
                int after = x + 1 == stride ? 0xff :
                    centers[i - stride + 1] | centers[i + 1] | centers[i + stride + 1];
                result[i] = (byte)(column | (column << 1) | (column >> 1)
                    | ((before & 1) << 7) | ((after & 0x80) >> 7));
            }
        return result;
    }
}
