using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Lumina;
using ColliderType = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType;

namespace vnavmesh
{
    // The upstream extractor only needs a real SqPack file source and logging.
    // This standalone host never loads Dalamud or touches a game process.
    internal static class Service
    {
        internal static GameData DataManager = null!;
        internal static OfflineLog Log { get; } = new();
    }

    internal sealed class OfflineLog
    {
        public void Error(string message) => Console.Error.WriteLine(message);
    }
}

