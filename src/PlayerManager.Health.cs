using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using DeadworksManaged.Api;
using DeadworksManaged.Game;

namespace aimLOCK;

public partial class PlayerManager
{
    private const int DuelMaxHealth = 2800;
    // true: duels use the hero's own max health (max boons plus the duel items), with
    // no server-only bonus, so the health bar on players' screens matches the real
    // health. The 2800 bonus was a server-only value the game never sent to players,
    // so their bar stayed full until health dropped below the vanilla max.
    private const bool VanillaDuelMaxHealth = true;

    private static bool HasDuelMaxHealth(CCitadelPlayerPawn pawn) =>
        VanillaDuelMaxHealth || pawn.GetMaxHealth() == DuelMaxHealth;

    private static int lastLoggedDuelMaxHealth;
    private const double DuelHealthCapCheckIntervalSeconds = 0.05;

    /* Duel health ledger */

    // In a live round a duelist's health may only change through damage the server
    // reports (DamageTaken) and the plugin's own lifesteal. The ledger is the health
    // those two add up to; any health above it came from somewhere else and is taken
    // back on the next loop tick (20 times a second). Health below it (damage with no
    // event) is accepted and the ledger follows it down. Point-blank tests showed one
    // player losing ~2,000 more health than the other with the same damage and the
    // same lifesteal on both sides.
    private readonly Dictionary<uint, int> duelHealthLedger = new();

    private void CheckDuelHealthLedger(CCitadelPlayerPawn pawn, DuelMatch match)
    {
        uint handle = pawn.EntityHandle;
        if (!match.CombatActive)
        {
            duelHealthLedger.Remove(handle);
            return;
        }

        int health = pawn.Health;
        if (!duelHealthLedger.TryGetValue(handle, out int ledger))
        {
            duelHealthLedger[handle] = health;
            return;
        }

        if (health > ledger)
        {
            int target = Math.Max(1, ledger);
            pawn.Health = target;
            if (lifestealDebugSlots.Count != 0 && GetLifestealDebugStats(pawn) is { } stats)
                stats.UnexplainedGain += health - target;
            duelHealthLedger[handle] = target;
            return;
        }

        if (health < ledger && lifestealDebugSlots.Count != 0 && GetLifestealDebugStats(pawn) is { } lossStats)
            lossStats.UnreportedLoss += ledger - health;
        duelHealthLedger[handle] = health;
    }

    private void LedgerAdd(CBaseEntity? pawnEntity, int amount)
    {
        if (
            amount != 0 &&
            pawnEntity != null &&
            duelHealthLedger.Count != 0 &&
            duelHealthLedger.TryGetValue(pawnEntity.EntityHandle, out int ledger)
        )
            duelHealthLedger[pawnEntity.EntityHandle] = ledger + amount;
    }

    private static void ClampHealthToMax(CCitadelPlayerPawn? pawn)
    {
        if (pawn is not { IsValid: true, IsAlive: true })
            return;

        // Only write when over the cap: every Health write is networked, and the duel
        // monitor calls this 20 times a second per duelist.
        int maxHealth = pawn.GetMaxHealth();
        if (pawn.Health > maxHealth)
            pawn.Health = maxHealth;
    }

    private static void ApplyDuelMaxHealth(CCitadelPlayerPawn pawn)
    {
        if (VanillaDuelMaxHealth)
        {
            int vanillaMax = pawn.GetMaxHealth();
            if (vanillaMax != lastLoggedDuelMaxHealth)
            {
                lastLoggedDuelMaxHealth = vanillaMax;
                Console.WriteLine($"[aimLOCK] Duel max health: {vanillaMax} (the hero's own value).");
            }
            return;
        }

        const string modifierName =
            "upgrade_health/modifier_intrinsic_base";

        // The duel max health is carried by upgrade_health's modifier (it is no longer
        // in the item stack), so add the item here when the player doesn't own it.
        if (pawn.AbilityComponent?.FindAbilityByName("upgrade_health") == null)
            pawn.AddItem("upgrade_health");
        if (pawn.AbilityComponent?.FindAbilityByName("upgrade_health") == null)
        {
            Console.WriteLine(
                "[aimLOCK] Cannot set duel max health; upgrade_health is missing."
            );
            return;
        }

        pawn.RemoveModifier(modifierName);
        int maxHealthWithoutHealthUpgrade = pawn.GetMaxHealth();
        if (maxHealthWithoutHealthUpgrade <= 0)
        {
            Console.WriteLine(
                $"[aimLOCK] Cannot set duel max health; effective max health without upgrade_health is {maxHealthWithoutHealthUpgrade}."
            );
            return;
        }

        var abilityValues = new Dictionary<string, float>(ZeroHealthRegenValues);
        abilityValues["BonusHealth"] =
            DuelMaxHealth - maxHealthWithoutHealthUpgrade;
        pawn.AddModifier(modifierName, abilityValues: abilityValues);

        int effectiveMaxHealth = pawn.GetMaxHealth();
        if (effectiveMaxHealth != DuelMaxHealth)
        {
            Console.WriteLine(
                $"[aimLOCK] Duel max health is {effectiveMaxHealth}, expected {DuelMaxHealth} after the upgrade_health bonus."
            );
        }
        ClampHealthToMax(pawn);
    }

    private static void SetDuelStartingHealth(CCitadelPlayerPawn pawn)
    {
        pawn.Health = VanillaDuelMaxHealth
            ? pawn.GetMaxHealth()
            : Math.Min(DuelMaxHealth, pawn.GetMaxHealth());
    }

    // The health cap is checked by the shared duel loop (DuelLoopTick) for every
    // duelist; no per-match timer.
    public void StartDuelHealthCapMonitor(DuelMatch match) => StartDuelLoop();

    public void PrepareDuelMatchHealth(DuelMatch match)
    {
        if (match.Finished)
            return;

        var pawn1 = FindMatchParticipant(match.Player1)?.GetHeroPawn();
        var pawn2 = FindMatchParticipant(match.Player2)?.GetHeroPawn();
        if (pawn1 is not { IsValid: true } || pawn2 is not { IsValid: true })
        {
            Console.WriteLine(
                "[aimLOCK] Could not set duel max health for " +
                $"{GetMatchParticipantDisplayName(match.Player1)} vs " +
                $"{GetMatchParticipantDisplayName(match.Player2)}."
            );
            return;
        }

        ApplyDuelMaxHealth(pawn1);
        ApplyDuelMaxHealth(pawn2);
        ResetDuelCurrentHealth(match);
    }

    private void RestoreDuelRespawnHealth(
        DuelMatch match,
        string playerName,
        int attemptsRemaining = 12,
        bool hasStableMaxHealth = false
    )
    {
        if (match.Finished)
            return;

        var pawn = FindMatchParticipant(playerName)?.GetHeroPawn();
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            if (attemptsRemaining <= 1)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not restore duel health for {playerName} after respawn."
                );
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                RestoreDuelRespawnHealth(
                    match,
                    playerName,
                    attemptsRemaining - 1
                ));
            return;
        }

        if (
            !VanillaDuelMaxHealth &&
            pawn.AbilityComponent?.FindAbilityByName("upgrade_health") == null
        )
            pawn.AddItem("upgrade_health");
        if (!HasDuelMaxHealth(pawn))
            ApplyDuelMaxHealth(pawn);
        if (HasDuelMaxHealth(pawn))
        {
            if (hasStableMaxHealth)
            {
                SetDuelStartingHealth(pawn);
                server.Queue.StartCountdown(match);
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                RestoreDuelRespawnHealth(
                    match,
                    playerName,
                    attemptsRemaining - 1,
                    hasStableMaxHealth: true
                ));
            return;
        }

        if (attemptsRemaining <= 1)
        {
            Console.WriteLine(
                $"[aimLOCK] Duel max health remained {pawn.GetMaxHealth()} instead of {DuelMaxHealth} after respawning {playerName}."
            );
            server.Queue.StartCountdown(match);
            return;
        }

        server.ScheduleOnce(0.25.Seconds(), () =>
            RestoreDuelRespawnHealth(
                match,
                playerName,
                attemptsRemaining - 1
            ));
    }

    /// <summary>Sets both duelists to their max health (only writes when not full).</summary>
    public void FillMatchHealth(DuelMatch match)
    {
        FillHealth(FindMatchParticipant(match.Player1)?.GetHeroPawn());
        FillHealth(FindMatchParticipant(match.Player2)?.GetHeroPawn());
    }

    private static void FillHealth(CCitadelPlayerPawn? pawn)
    {
        if (pawn is not { IsValid: true, IsAlive: true })
            return;

        int maxHealth = pawn.GetMaxHealth();
        if (maxHealth > 0 && pawn.Health != maxHealth)
            pawn.Health = maxHealth;
    }

    private void ResetDuelCurrentHealth(DuelMatch match)
    {
        foreach (var player in GetMatchParticipants(match))
        {
            var pawn = player.GetHeroPawn();
            if (pawn is { IsValid: true, IsAlive: true })
            {
                SetDuelStartingHealth(pawn);
            }
        }
    }
}
