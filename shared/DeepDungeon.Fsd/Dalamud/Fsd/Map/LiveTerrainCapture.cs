using System.IO.Compression;
using System.Numerics;
using DeepDungeon.Fsd.Terrain;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DeepDungeon.Fsd.Dalamud.Map;

/// <summary>Capture expanded client scenes on the framework thread; bake on a worker.</summary>
internal static class LiveTerrainCapture
{
    private static string sqpack = "", output = "";
    private static readonly Dictionary<uint, Task> jobs = [];
    private static readonly HashSet<FloorMapKey> readyMaps = [];
    private static CancellationTokenSource lifetime = new();
    private static DateTime retryAt, nextObserve;
    private static Task<string>? export;
    internal static bool Enabled { get; private set; } = true;
    internal static string Status { get; private set; } = "";
    internal static string ExportStatus { get; private set; } = "";
    internal static bool Exporting => export is { IsCompleted: false };

    internal static void Initialize(string configDirectory)
    {
        if (lifetime.IsCancellationRequested) lifetime = new();
        sqpack = Service.DataManager.GameData.DataPath.FullName;
        output = Path.Combine(configDirectory, "FloorMaps", "client-v1");
        Directory.CreateDirectory(output);
        Enabled = true;
    }

    internal static void Dispose() => lifetime.Cancel();

    internal static unsafe void Observe()
    {
        if (!Enabled || DateTime.UtcNow < nextObserve) return;
        nextObserve = DateTime.UtcNow.AddSeconds(1);
        if (DutyTransitionUtil.IsBetweenAreas() || Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]) return;
        var framework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
        var dungeon = framework == null ? null : framework->GetInstanceContentDeepDungeon();
        if (dungeon != null && dungeon->Floor > 0 && FloorMapKey.For(dungeon) is { Layout: >= 0 } key)
            Ensure(key);
    }

    internal static object Configure(Dictionary<string, string> options)
    {
        if (jobs.Values.Any(j => !j.IsCompleted)) throw new InvalidOperationException("地圖正在生成，請稍後再調整。");
        if (options.TryGetValue("sqpack", out var s)) sqpack = Path.GetFullPath(s);
        if (options.TryGetValue("output", out var o)) output = Path.GetFullPath(o);
        if (options.TryGetValue("enabled", out var enabled)) Enabled = bool.Parse(enabled);
        Directory.CreateDirectory(output);
        jobs.Clear();
        readyMaps.Clear();
        FloorMap.Reset();
        return new { ok = true, enabled = Enabled, output };
    }

    internal static string MapPath(FloorMapKey key) => Path.Combine(output, $"{key}.map.gz");
    private static bool HasMap(FloorMapKey key)
    {
        if (readyMaps.Contains(key)) return true;
        if (jobs.TryGetValue(key.Territory, out var job) && !job.IsCompleted) return false;
        if (!File.Exists(Path.Combine(output, $"{key.Territory}.generated.json")) || !File.Exists(MapPath(key))) return false;
        readyMaps.Add(key);
        return true;
    }

    internal static bool Ensure(FloorMapKey key)
    {
        if (HasMap(key)) { Status = "客戶端底圖已就緒。"; return true; }
        if (!Enabled) { Status = "此版型尚未收集客戶端地圖。"; return false; }
        if (jobs.TryGetValue(key.Territory, out var job))
        {
            Status = !job.IsCompleted ? "正在生成客戶端底圖..." : job.IsFaulted
                ? job.Exception!.GetBaseException().Message : job.IsCanceled
                ? "地圖生成已取消。" : $"客戶端底圖缺少 {key}";
            return false;
        }
        if (DutyTransitionUtil.IsBetweenAreas()) { Status = "等待場景載入..."; return false; }
        if (DateTime.UtcNow < retryAt) return false;
        retryAt = DateTime.UtcNow.AddSeconds(1);
        try
        {
            var captured = Capture(key);
            string destination = output, data = sqpack;
            var token = lifetime.Token;
            jobs[key.Territory] = Task.Run(() => {
                token.ThrowIfCancellationRequested();
                File.WriteAllText(Path.Combine(destination, $"{key.Territory}.capture.json"), JsonConvert.SerializeObject(captured));
                var generated = TerrainBaker.BakeCapture(captured, data, destination, token);
                token.ThrowIfCancellationRequested();
                var manifest = Path.Combine(destination, $"{key.Territory}.generated.json");
                File.WriteAllText(manifest + ".tmp", JsonConvert.SerializeObject(new {
                    captured.Dungeon, captured.Set, captured.Territory, source = "client-layout", generated }));
                File.Move(manifest + ".tmp", manifest, true);
            }, token);
            Service.Log.Information($"[FSD.TerrainCapture] captured territory={key.Territory}, layouts={captured.Layouts.Count}, BG={captured.Scene.BgParts.Count}, colliders={captured.Scene.Colliders.Count}");
            Status = "正在生成客戶端底圖...";
        }
        catch (Exception ex) { Status = ex.Message; }
        return false;
    }

    internal static void Export()
    {
        if (Exporting) return;
        // Export only completed client captures; no character, inventory or run logs.
        string source = output;
        string destination = Path.Combine(Directory.GetParent(output)!.Parent!.FullName,
            $"FSD-Maps-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.zip");
        export = Task.Run(() => {
            var manifests = Directory.GetFiles(source, "*.generated.json");
            if (manifests.Length == 0) throw new IOException("尚未收集到可導出的地圖。");
            try
            {
            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            int count = 0;
            foreach (var manifest in manifests)
            {
                var metadata = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifest));
                if ((string?)metadata["source"] != "client-layout") continue;
                string territory = Path.GetFileName(manifest).Split('.')[0];
                var files = metadata["generated"]!.Values<string>()
                    .Append($"{territory}.capture.json").Append(Path.GetFileName(manifest));
                foreach (var name in files)
                {
                    if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name) throw new InvalidDataException("Invalid map filename");
                    archive.CreateEntryFromFile(Path.Combine(source, name), name, CompressionLevel.Fastest);
                }
                count++;
            }
            if (count == 0) throw new IOException("尚未收集到可導出的地圖。");
            return destination;
            }
            catch { File.Delete(destination); throw; }
        });
        ExportStatus = "正在導出地圖...";
    }

    internal static void UpdateExportStatus()
    {
        if (export is not { IsCompleted: true }) return;
        ExportStatus = export.IsCompletedSuccessfully ? $"地圖已導出：{export.Result}"
            : $"地圖導出失敗：{export.Exception?.GetBaseException().Message}";
        export = null;
    }
    private static unsafe CapturedTerrainScene Capture(FloorMapKey key)
    {
        var world = LayoutWorld.Instance();
        var layout = world == null ? null : world->ActiveLayout;
        if (layout == null || layout->InitState != 7) throw new IOException("等待 Layout 完成載入...");
        var result = new CapturedTerrainScene { Dungeon = key.Dungeon, Set = key.Set, Territory = key.Territory };
        var row = Service.DataManager.GetExcelSheet<DeepDungeonLayer>()!
            .Single(r => r.DeepDungeon.RowId == key.Dungeon && r.FloorSet == key.Set - 1 && r.RoomA.RowId != 0);
        var tables = new[] { row.RoomA, row.RoomB, row.RoomC };
        var doorIds = new HashSet<uint>();
        foreach (var table in tables)
            if (table.RowId != 0)
                foreach (var mapRow in table.Value)
                    for (int x = 0; x < 5; x++)
                        if (mapRow.DeepDungeonRoom[x].RowId != 0)
                            for (int side = 1; side <= 4; side++)
                                if (mapRow.DeepDungeonRoom[x].Value.Level[side].RowId is > 1 and var id) doorIds.Add(id);
        var doorBoxes = new Dictionary<uint, List<Matrix4x4>>();
        ReadLayout(world->GlobalLayout, result, doorIds, doorBoxes);
        ReadLayout(layout, result, doorIds, doorBoxes);

        // Only room-table anchors use Lumina. All physical geometry and world
        // transforms above have already been expanded by the game client.
        var bg = Service.DataManager.GetExcelSheet<TerritoryType>()!.GetRow(key.Territory).Bg.ToString();
        var plan = Service.DataManager.GetFile<LgbFile>("bg/" + bg[..bg.LastIndexOf('/')] + "/planmap.lgb")
            ?? throw new IOException("Missing planmap anchors");
        var marks = plan.Layers.SelectMany(l => l.InstanceObjects).ToDictionary(e => e.InstanceId);
        for (int index = 0; index < tables.Length; index++)
        {
            if (tables[index].RowId == 0) continue;
            var map = new CapturedTerrainLayout { Index = index };
            foreach (var mapRow in tables[index].Value)
                for (int x = 0; x < 5; x++)
                {
                    var room = mapRow.DeepDungeonRoom[x];
                    if (room.RowId == 0) continue;
                    int cell = mapRow.SubrowId * 5 + x;
                    var p = marks[room.Value.Level[0].RowId].Transform.Translation;
                    map.Centers.Add(cell, new(p.X, p.Y, p.Z));
                    for (int side = 0; side < 4; side++)
                    {
                        uint id = room.Value.Level[side + 1].RowId;
                        if (id <= 1) continue;
                        if (!doorBoxes.TryGetValue(id, out var boxes)) throw new IOException($"Client door {id} missing");
                        foreach (var box in boxes) map.Doors.Add(new(cell, 1 << side, box));
                    }
                }
            map.Seeds.AddRange(map.Centers.Values);
            result.Layouts.Add(map);
        }
        int maxSet = key.Dungeon == 1 ? 20 : 10;
        if (key.Set == maxSet)
        {
            var activeLayers = new HashSet<ushort>();
            foreach (var (id, _) in layout->Layers) activeLayers.Add(id);
            var resultLayer = plan.Layers.First(l => activeLayers.Contains((ushort)l.LayerId)
                && l.Name.StartsWith("LVD_Event_", StringComparison.OrdinalIgnoreCase));
            var map = new CapturedTerrainLayout { Index = 4 };
            foreach (var e in resultLayer.InstanceObjects)
                if (e.AssetType is LayerEntryType.EventRange or LayerEntryType.PopRange or LayerEntryType.CollisionBox)
                {
                    var p = e.Transform.Translation;
                    map.Seeds.Add(new(p.X, p.Y, p.Z));
                }
            result.Layouts.Add(map);
        }
        return result;
    }

    // Same native input as vnav SceneDefinition.FillFromLayout, including its
    // DD rule retaining inactive BG. Conditional door boxes are kept separately.
    private static unsafe void ReadLayout(LayoutManager* layout, CapturedTerrainScene captured,
        HashSet<uint> doors, Dictionary<uint, List<Matrix4x4>> doorBoxes)
    {
        if (layout == null || layout->InitState != 7) return;
        var scene = captured.Scene;
        foreach (var (key, value) in layout->CrcToPath) scene.MeshPaths[key] = value.Value->DataString;
        foreach (var (key, value) in layout->CrcToAnalyticShapeData)
            scene.AnalyticShapes[key.Key] = (value.Transform, value.BoundsMin, value.BoundsMax);
        foreach (var (_, value) in layout->Terrains) scene.Terrains.Add($"{value.Value->PathString}/collision");
        foreach (var (type, entries) in layout->InstancesByType)
            foreach (var (key, value) in *entries.Value)
            {
                if (type == InstanceType.BgPart)
                {
                    var bg = (BgPartsLayoutInstance*)value.Value;
                    uint crc = bg->AnalyticShapeDataCrc != 0 ? bg->AnalyticShapeDataCrc : bg->CollisionMeshPathCrc;
                    if (crc == 0) continue;
                    scene.BgParts.Add((key, *value.Value->GetTransformImpl(), crc,
                        bg->CollisionMaterialIdLow | ((ulong)bg->CollisionMaterialIdHigh << 32),
                        bg->CollisionMaterialMaskLow | ((ulong)bg->CollisionMaterialMaskHigh << 32), bg->AnalyticShapeDataCrc != 0));
                }
                else if (type == InstanceType.CollisionBox)
                {
                    var box = (CollisionBoxLayoutInstance*)value.Value;
                    if (doors.Contains(box->Id.InstanceKey))
                    {
                        if (box->TriggerBoxLayoutInstance.Type != ColliderType.Box) throw new IOException("Non-box client door");
                        if (!doorBoxes.TryGetValue(box->Id.InstanceKey, out var list)) doorBoxes[box->Id.InstanceKey] = list = [];
                        list.Add(box->Transform.Compose());
                    }
                    else if ((box->Flags3 & 0x10) != 0)
                        scene.Colliders.Add((key, box->Transform, box->PcbPathCrc,
                            box->MaterialIdLow | ((ulong)box->MaterialIdHigh << 32),
                            box->MaterialMaskLow | ((ulong)box->MaterialMaskHigh << 32), box->TriggerBoxLayoutInstance.Type));
                }
            }
        // CrcToPath also contains models, VFX, sounds etc. The extractor only
        // needs paths referenced by collision bindings.
        var required = scene.BgParts.Where(b => !b.analytic).Select(b => b.crc)
            .Concat(scene.Colliders.Where(c => c.type == ColliderType.Mesh).Select(c => c.crc)).ToHashSet();
        foreach (var crc in scene.MeshPaths.Keys.Where(k => !required.Contains(k)).ToArray()) scene.MeshPaths.Remove(crc);
    }
}
