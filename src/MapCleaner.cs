/*
Keeps the map empty: no troopers, guardians, walkers, patrons, neutrals, mid boss,
breakable boxes/statues, urn, powerups or healing pickups.

This replaces the repeating sweep (every 1 s, Entities.ByClass<CAI_BaseNPC>()), which
walked every entity slot on the map and marshalled every class name each second. That
sweep is a steady CPU cost plus a spike every second.

How it works now:
1. Spawn convars are turned off so the game stops creating most of these at all.
2. OnEntitySpawned catches anything that still spawns. Candidates are checked on the next
   tick (so an ability-summoned NPC already has its summoner set) and removed.
3. One staggered sweep runs after map load / plugin reload to catch whatever already
   exists, removing a small batch per tick so it never spikes a frame.

Minions and summoned NPCs (anything with a summoner) are kept, same as before.
Use /dumpents (aimlock.debug) to write every designer name on the map to
aimlock_entities.txt if something still shows up and needs adding to the rules below.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using DeadworksManaged.Api;

namespace aimLOCK;

public sealed class MapCleaner
{
    private const int RemovalsPerTick = 48;

    // Spawn systems to switch off. Missing convars are skipped with one log line.
    private static readonly string[] SpawnConVarsToDisable =
    {
        "citadel_trooper_spawn_enabled",      // lane troopers
        "citadel_npc_spawn_enabled",          // all NPCs
        "citadel_neutral_spawn_enabled",      // jungle camps
        "citadel_crate_spawn_enabled",        // crates
        "citadel_powerup_spawn_enabled",      // powerup camps
        "citadel_pickup_spawner_enabled",     // healing snacks
        "citadel_koth_enabled",               // urn / king-of-the-hill objective
        "citadel_breakable_prop_breakable_enabled" // breakable boxes / statues
    };

    // Exact designer names that don't fit the prefix rules.
    private static readonly HashSet<string> ExactNamesToRemove = new(StringComparer.Ordinal)
    {
        "citadel_trigger_idol_return",
        "citadel_point_talker_idol"
    };

    // Non-NPC prefixes: removed outright.
    private static readonly string[] PropPrefixesToRemove =
    {
        "citadel_breakable_",       // boxes, crates, jars, golden statues
        "citadel_item_pickup",      // urn, rejuv, dropped pickups
        "citadel_pickup",           // healing snacks, gold/ability pickups, spawners
        "citadel_item_powerup_spawner",
        "citadel_item_koth_spawner"
    };

    // "npc_" covers troopers, guardians (tier1), walkers (tier2), patrons/shrines
    // (tier3, barrack boss), sentries, neutrals, mid boss and NPC makers.
    private const string NpcPrefix = "npc_";

    // Never remove these even if they match a rule.
    private static readonly HashSet<string> NamesToKeep = new(StringComparer.Ordinal)
    {
        "npc_player_bot_brain"
    };

    private readonly HashSet<string> missingConVarsLogged = new(StringComparer.Ordinal);
    private readonly Queue<uint> pendingRemovals = new();
    private readonly List<uint> spawnedCandidates = new();
    private readonly aimLOCK server;
    private bool sweepRunning;
    private bool spawnCheckScheduled;

    public MapCleaner(aimLOCK server)
    {
        this.server = server;
    }

    /// <summary>Cheap name-only test, safe to call for every spawned entity.</summary>
    private static bool IsCandidate(string designerName)
    {
        if (designerName.Length == 0 || NamesToKeep.Contains(designerName))
            return false;

        // Almost every entity spawned during play (particles, projectiles, world text,
        // abilities) is rejected by this first-character check.
        char first = designerName[0];
        if (first != 'n' && first != 'c')
            return false;

        if (designerName.StartsWith(NpcPrefix, StringComparison.Ordinal))
            return true;

        if (ExactNamesToRemove.Contains(designerName))
            return true;

        foreach (string prefix in PropPrefixesToRemove)
        {
            if (designerName.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Full test: candidate by name, and not a minion or summoned NPC.</summary>
    public static bool ShouldRemove(CBaseEntity entity)
    {
        if (!entity.IsValid)
            return false;

        string designerName = entity.DesignerName;
        if (!IsCandidate(designerName))
            return false;

        if (!designerName.StartsWith(NpcPrefix, StringComparison.Ordinal))
            return true;

        var citadelNpc = entity.As<CAI_CitadelNPC>();
        return citadelNpc == null || (!citadelNpc.IsMinion && citadelNpc.Summoner == null);
    }

    public void DisableSpawnConVars()
    {
        foreach (string name in SpawnConVarsToDisable)
        {
            var conVar = ConVar.Find(name);
            if (conVar == null)
            {
                if (missingConVarsLogged.Add(name))
                    Console.WriteLine($"[aimLOCK] Could not find {name}; skipped.");
                continue;
            }

            if (conVar.GetInt() != 0)
            {
                conVar.SetInt(0);
                Console.WriteLine($"[aimLOCK] Set {name} to 0.");
            }
        }
    }

    public void OnEntitySpawned(CBaseEntity entity)
    {
        if (!entity.IsValid || !IsCandidate(entity.DesignerName))
            return;

        spawnedCandidates.Add(entity.EntityHandle);
        if (spawnCheckScheduled)
            return;

        spawnCheckScheduled = true;
        server.ScheduleNextTick(CheckSpawnedCandidates);
    }

    private void CheckSpawnedCandidates()
    {
        spawnCheckScheduled = false;
        foreach (uint handle in spawnedCandidates)
        {
            var entity = CBaseEntity.FromHandle(handle);
            if (entity != null && ShouldRemove(entity))
                entity.Remove();
        }

        spawnedCandidates.Clear();
    }

    /// <summary>One-time sweep for entities that existed before the plugin was listening.</summary>
    public void StartSweep()
    {
        if (sweepRunning)
            return;

        int queued = 0;
        foreach (var entity in Entities.All)
        {
            if (entity.IsValid && IsCandidate(entity.DesignerName))
            {
                pendingRemovals.Enqueue(entity.EntityHandle);
                queued++;
            }
        }

        Console.WriteLine($"[aimLOCK] Map cleanup: {queued} NPCs/objectives/breakables queued for removal.");
        if (pendingRemovals.Count == 0)
            return;

        sweepRunning = true;
        server.ScheduleNextTick(RemoveBatch);
    }

    private void RemoveBatch()
    {
        for (int i = 0; i < RemovalsPerTick && pendingRemovals.TryDequeue(out uint handle); i++)
        {
            var entity = CBaseEntity.FromHandle(handle);
            if (entity != null && ShouldRemove(entity))
                entity.Remove();
        }

        if (pendingRemovals.Count > 0)
            server.ScheduleNextTick(RemoveBatch);
        else
            sweepRunning = false;
    }

    /// <summary>Writes every designer name on the map with its count, most common first.</summary>
    public string DumpEntities(string path = "aimlock_entities.txt")
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in Entities.All)
        {
            if (!entity.IsValid)
                continue;

            string name = entity.DesignerName;
            counts[name] = counts.TryGetValue(name, out int count) ? count + 1 : 1;
        }

        var builder = new StringBuilder();
        foreach (var pair in counts.OrderByDescending(pair => pair.Value))
        {
            builder.Append(pair.Value.ToString().PadLeft(6))
                .Append("  ")
                .Append(pair.Key)
                .Append(IsCandidate(pair.Key) ? "   [removed by aimLOCK]" : "")
                .AppendLine();
        }

        File.WriteAllText(path, builder.ToString());
        return Path.GetFullPath(path);
    }
}
