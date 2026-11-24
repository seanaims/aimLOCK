namespace AimLOCK;

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

public partial class PlayerManager
{
    private const int DuelMaxHealth = 2800;

    // true: duels use the hero's own max health (max boons plus the duel items), with
    // no server-only bonus, so the health bar on players' screens matches the real
    // health. The 2800 bonus was a server-only value the game never sent to players,
    // so their bar stayed full until health dropped below the vanilla max.
    private const bool VanillaDuelMaxHealth = true;
    private const double DuelHealthCapCheckIntervalSeconds = 0.05;

    private static int lastLoggedDuelMaxHealth;

    /* Duel health ledger */

    // In a live round a duelist's health may only change through damage the server
    // reports (DamageTaken) and the plugin's own lifesteal. The ledger is the health
    // those two add up to; any health above it came from somewhere else and is taken
    // back on the next loop tick (20 times a second). Health below it (damage with no
    // event) is accepted and the ledger follows it down. Point-blank tests showed one
    // player losing ~2,000 more health than the other with the same damage and the
    // same lifesteal on both sides.
    private readonly Dictionary<uint, int> duelHealthLedger = new();

    // The health cap is checked by the shared duel loop (DuelLoopTick) for every
    // duelist; no per-match timer.
    public void StartDuelHealthCapMonitor(DuelMatch match) => this.StartDuelLoop();

    public void PrepareDuelMatchHealth(DuelMatch match)
    {
        if (match.Finished)
        {
            return;
        }

        var pawn1 = this.FindMatchParticipant(match.Player1)?.GetHeroPawn();
        var pawn2 = this.FindMatchParticipant(match.Player2)?.GetHeroPawn();
        if (pawn1 is not { IsValid: true } || pawn2 is not { IsValid: true })
        {
            Console.WriteLine(
                "[aimLOCK] Could not set duel max health for " +
                $"{this.GetMatchParticipantDisplayName(match.Player1)} vs " +
                $"{this.GetMatchParticipantDisplayName(match.Player2)}.");
            return;
        }

        ApplyDuelMaxHealth(pawn1);
        ApplyDuelMaxHealth(pawn2);
        this.ResetDuelCurrentHealth(match);
    }

    /// <summary>Sets both duelists to their max health (only writes when not full).</summary>
    public void FillMatchHealth(DuelMatch match)
    {
        FillHealth(this.FindMatchParticipant(match.Player1)?.GetHeroPawn());
        FillHealth(this.FindMatchParticipant(match.Player2)?.GetHeroPawn());
    }

    private static bool HasDuelMaxHealth(CCitadelPlayerPawn pawn) =>
        VanillaDuelMaxHealth || pawn.GetMaxHealth() == DuelMaxHealth;

    private static void ClampHealthToMax(CCitadelPlayerPawn? pawn)
    {
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            return;
        }

        // Only write when over the cap: every Health write is networked, and the duel
        // monitor calls this 20 times a second per duelist.
        int maxHealth = pawn.GetMaxHealth();
        if (pawn.Health > maxHealth)
        {
            pawn.Health = maxHealth;
        }
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
        {
            pawn.AddItem("upgrade_health");
        }

        if (pawn.AbilityComponent?.FindAbilityByName("upgrade_health") == null)
        {
            Console.WriteLine(
                "[aimLOCK] Cannot set duel max health; upgrade_health is missing.");
            return;
        }

        pawn.RemoveModifier(modifierName);
        int maxHealthWithoutHealthUpgrade = pawn.GetMaxHealth();
        if (maxHealthWithoutHealthUpgrade <= 0)
        {
            Console.WriteLine(
                $"[aimLOCK] Cannot set duel max health; effective max health without upgrade_health is {maxHealthWithoutHealthUpgrade}.");
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
                $"[aimLOCK] Duel max health is {effectiveMaxHealth}, expected {DuelMaxHealth} after the upgrade_health bonus.");
        }

        ClampHealthToMax(pawn);
    }

    private static void SetDuelStartingHealth(CCitadelPlayerPawn pawn)
    {
        pawn.Health = VanillaDuelMaxHealth
            ? pawn.GetMaxHealth()
            : Math.Min(DuelMaxHealth, pawn.GetMaxHealth());
    }

    private static void FillHealth(CCitadelPlayerPawn? pawn)
    {
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            return;
        }

        int maxHealth = pawn.GetMaxHealth();
        if (maxHealth > 0 && pawn.Health != maxHealth)
        {
            pawn.Health = maxHealth;
        }
    }

    private void CheckDuelHealthLedger(CCitadelPlayerPawn pawn, DuelMatch match)
    {
        uint handle = pawn.EntityHandle;
        if (!match.CombatActive)
        {
            this.duelHealthLedger.Remove(handle);
            return;
        }

        int health = pawn.Health;
        if (!this.duelHealthLedger.TryGetValue(handle, out int ledger))
        {
            this.duelHealthLedger[handle] = health;
            return;
        }

        if (health > ledger)
        {
            int target = Math.Max(1, ledger);
            pawn.Health = target;
            if (this.lifestealDebugSlots.Count != 0 && GetLifestealDebugStats(pawn) is { } stats)
            {
                stats.UnexplainedGain += health - target;
            }

            this.duelHealthLedger[handle] = target;
            return;
        }

        if (health < ledger && this.lifestealDebugSlots.Count != 0 && GetLifestealDebugStats(pawn) is { } lossStats)
        {
            lossStats.UnreportedLoss += ledger - health;
        }

        this.duelHealthLedger[handle] = health;
    }

    private void LedgerAdd(CBaseEntity? pawnEntity, int amount)
    {
        if (
            amount != 0 &&
            pawnEntity != null &&
            this.duelHealthLedger.Count != 0 &&
            this.duelHealthLedger.TryGetValue(pawnEntity.EntityHandle, out int ledger))
        {
            this.duelHealthLedger[pawnEntity.EntityHandle] = ledger + amount;
        }
    }

    private void RestoreDuelRespawnHealth(
        DuelMatch match,
        string playerName,
        int attemptsRemaining = 12,
        bool hasStableMaxHealth = false)
    {
        if (match.Finished)
        {
            return;
        }

        var pawn = this.FindMatchParticipant(playerName)?.GetHeroPawn();
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            if (attemptsRemaining <= 1)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not restore duel health for {playerName} after respawn.");
                return;
            }

            this.server.ScheduleOnce(0.25.Seconds(), () =>
                this.RestoreDuelRespawnHealth(
                    match,
                    playerName,
                    attemptsRemaining - 1));
            return;
        }

        if (
            !VanillaDuelMaxHealth &&
            pawn.AbilityComponent?.FindAbilityByName("upgrade_health") == null)
        {
            pawn.AddItem("upgrade_health");
        }

        if (!HasDuelMaxHealth(pawn))
        {
            ApplyDuelMaxHealth(pawn);
        }

        if (HasDuelMaxHealth(pawn))
        {
            if (hasStableMaxHealth)
            {
                SetDuelStartingHealth(pawn);
                this.server.Queue.StartCountdown(match);
                return;
            }

            this.server.ScheduleOnce(0.25.Seconds(), () =>
                this.RestoreDuelRespawnHealth(
                    match,
                    playerName,
                    attemptsRemaining - 1,
                    hasStableMaxHealth: true));
            return;
        }

        if (attemptsRemaining <= 1)
        {
            Console.WriteLine(
                $"[aimLOCK] Duel max health remained {pawn.GetMaxHealth()} instead of {DuelMaxHealth} after respawning {playerName}.");
            this.server.Queue.StartCountdown(match);
            return;
        }

        this.server.ScheduleOnce(0.25.Seconds(), () =>
            this.RestoreDuelRespawnHealth(
                match,
                playerName,
                attemptsRemaining - 1));
    }

    private void ResetDuelCurrentHealth(DuelMatch match)
    {
        foreach (var player in this.GetMatchParticipants(match))
        {
            var pawn = player.GetHeroPawn();
            if (pawn is { IsValid: true, IsAlive: true })
            {
                SetDuelStartingHealth(pawn);
            }
        }
    }
}
