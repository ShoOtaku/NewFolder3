using System;
using System.Collections.Generic;
using System.Numerics;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

/// <summary>Detects repeated movement confined to one small area, across route/goal changes.</summary>
internal sealed class RoomEscapeRecovery(Action<string, object> record)
{
    private const double LocalAttemptSeconds = 5;
    private const int LocalAttemptLimit = 3;
    private const float ProgressDistance = 3;
    private const double RoomAttemptSeconds = 30;
    private const double CooldownSeconds = 30;
    internal const float ArrivalRadius = 2.5f;
    private readonly HashSet<int> attemptedRooms = new();
    private Vector3? anchor;
    private DateTime lastSample;
    private double localEffort;
    private double roomEffort;
    private double cooldown;

    internal (int Room, Vector3 Center)? Target { get; private set; }

    internal void Pause() => lastSample = default;

    internal void Reset()
    {
        Target = null;
        anchor = null;
        lastSample = default;
        localEffort = roomEffort = cooldown = 0;
        attemptedRooms.Clear();
    }

    internal bool Update(DateTime now, Vector3 position, int currentRoom,
        bool movementRequested, bool allowed,
        Func<IReadOnlyList<(int Room, Vector3 Center)>> connectedRooms)
    {
        // Pauses, frame stalls and external movement ownership must not burn effort.
        var dt = lastSample == default ? 0 : Math.Clamp((now - lastSample).TotalSeconds, 0, .5);
        lastSample = now;
        if (!allowed)
        {
            if (Target is { } cancelled)
                record("room-escape-ended", new { room = cancelled.Room, outcome = "interrupted" });
            Reset();
            return false;
        }
        if (Target is { } target)
        {
            // A dispatched move is not success. Read back physical arrival in the other room.
            if (currentRoom == target.Room && DistanceSquared(position, target.Center) <= ArrivalRadius * ArrivalRadius)
            {
                record("room-escape-ended", new { room = target.Room, outcome = "arrived" });
                Reset();
                anchor = position;
                cooldown = CooldownSeconds;
                return false;
            }
            roomEffort += dt;
            if (roomEffort < RoomAttemptSeconds)
                return true;
            record("room-escape-ended", new { room = target.Room, outcome = "timeout" });
            Target = null;
            return SelectRoom(position, currentRoom, connectedRooms());
        }

        if (!movementRequested)
        {
            Pause();
            return false;
        }
        cooldown = Math.Max(0, cooldown - dt);
        if (anchor is not { } origin || DistanceSquared(position, origin) >= ProgressDistance * ProgressDistance)
        {
            anchor = position;
            localEffort = 0;
            return false;
        }
        if (cooldown > 0)
            return false;
        localEffort += dt;
        if (localEffort < LocalAttemptSeconds * LocalAttemptLimit)
            return false;
        attemptedRooms.Clear();
        return SelectRoom(position, currentRoom, connectedRooms());
    }

    private bool SelectRoom(Vector3 position, int currentRoom,
        IReadOnlyList<(int Room, Vector3 Center)> connectedRooms)
    {
        (int Room, Vector3 Center)? best = null;
        var bestDistance = float.PositiveInfinity;
        foreach (var candidate in connectedRooms)
        {
            var distance = DistanceSquared(position, candidate.Center);
            if (candidate.Room == currentRoom || attemptedRooms.Contains(candidate.Room) ||
                !float.IsFinite(distance) || distance <= ArrivalRadius * ArrivalRadius || distance >= bestDistance)
                continue;
            best = candidate;
            bestDistance = distance;
        }
        localEffort = roomEffort = 0;
        Target = best;
        if (best is not { } target)
        {
            cooldown = CooldownSeconds;
            record("room-escape-unavailable", new { currentRoom, attempted = attemptedRooms.Count });
            return false;
        }
        attemptedRooms.Add(target.Room);
        record("room-escape-started", new { fromRoom = currentRoom, room = target.Room,
            destination = new { x = target.Center.X, y = target.Center.Y, z = target.Center.Z },
            localAttempts = LocalAttemptLimit, attempt = attemptedRooms.Count });
        return true;
    }

    private static float DistanceSquared(Vector3 a, Vector3 b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
}
