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
    private const int FullyUpgradedAbilityBits = 0xF;

    // Max boons. The boon cap is 35; the level that grants it is 36 (the value the
    // Deadworks deathmatch example writes for a fully levelled hero).
    private const int MaxBoonLevel = 36;
    private const string BurstFireItemName = "upgrade_burst_fire";
    private const string BurstFireBuffName = "upgrade_burst_fire/modifier_burst_fire_buff";

    // Long enough to outlast any match; it is removed when the match ends.
    private const float PermanentModifierDuration = 36000f;

    private const long LoadoutReapplyWindowMs = 1000;
    private const int InitialAbilityCurrencyGrant = 30;

    // Haze abilities that are disabled: Sleep Dagger, Smoke Bomb (invisibility) and
    // Bullet Dance (ultimate). Fixation is the only ability left.
    private static readonly string[] DisabledHazeAbilities =
    {
        "ability_sleep_dagger",
        "ability_smoke_bomb",
        "ability_bullet_flurry",
    };
    private readonly Dictionary<uint, long> loadoutAppliedAt = new();

    private const float DefaultBulletLifestealPercent = 28f;
    private const float DefaultBlitzBulletsFireRatePercent = 22f;

    private const float DuelBlitzBulletsFireRatePercent =
        DefaultBlitzBulletsFireRatePercent / 2f;

    private const float DuelHighVelocityRoundsBulletSpeedPercent = 100f;

    private static readonly string[] DuelItemStack =
    {
        "upgrade_blitz_bullets",
        "upgrade_high_velocity_mag",
        "upgrade_pristine_emblem",
        "upgrade_fleetfoot_boots",
        "upgrade_titan_round",
        "upgrade_cardio_calibrator",
        "upgrade_burst_fire",
    };
    private static readonly string[] DuelHealthRegenerationProperties =
    {
        "BonusHealthRegen",
        "OutOfCombatHealthRegen",
        "HealthRegen",
        "MaxHealthRegen",
        "MaxHealthRegenPct",
        "TotalHealthRegen",
        "HealLifePercentOutOfCombat",
    };

    // Built once instead of with LINQ for every item on every respawn. AddModifier only
    // reads it.
    private static readonly Dictionary<string, float> ZeroHealthRegenValues =
        DuelHealthRegenerationProperties.ToDictionary(name => name, _ => 0f);

    /* Duel items: what a player owned before the match is restored after it */
    private static readonly string[] TrackedDuelItems =
    {
        "upgrade_vampire",
        "upgrade_blitz_bullets",
        "upgrade_high_velocity_mag",
        "upgrade_pristine_emblem",
        "upgrade_fleetfoot_boots",
        "upgrade_titan_round",
        "upgrade_cardio_calibrator",
        "upgrade_burst_fire",
        "upgrade_health",
    };

    // Removed after the match unless the player owned them before it.
    private static readonly string[] GrantedDuelItems =
    {
        "upgrade_pristine_emblem",
        "upgrade_fleetfoot_boots",
        "upgrade_titan_round",
        "upgrade_cardio_calibrator",
        "upgrade_burst_fire",
        "upgrade_health",
    };

    public static HashSet<string> GetOwnedDuelItems(CCitadelPlayerPawn? pawn)
    {
        var ownedItems = new HashSet<string>();
        foreach (string itemName in TrackedDuelItems)
        {
            if (pawn?.AbilityComponent?.FindAbilityByName(itemName) != null)
            {
                ownedItems.Add(itemName);
            }
        }

        return ownedItems;
    }

    public void RestoreDuelBulletLifesteal(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch)
    {
        pawn.RemoveModifier("upgrade_vampire/modifier_intrinsic_base");
        if (!hadItemBeforeMatch)
        {
            return;
        }

        pawn.AddModifier(
            "upgrade_vampire/modifier_intrinsic_base",
            abilityValues: new()
            {
                ["BulletLifestealPercent"] = DefaultBulletLifestealPercent,
            });
    }

    private void RestorePawnAfterDuel(
        CCitadelPlayerPawn? pawn,
        HashSet<string> itemsBeforeMatch)
    {
        if (pawn == null)
        {
            return;
        }

        RestoreDuelBulletLifesteal(
            pawn,
            itemsBeforeMatch.Contains("upgrade_vampire"));
        RestoreDuelBlitzBullets(
            pawn,
            itemsBeforeMatch.Contains("upgrade_blitz_bullets"));
        RestoreDuelHighVelocityRounds(
            pawn,
            itemsBeforeMatch.Contains("upgrade_high_velocity_mag"));
        foreach (string itemName in GrantedDuelItems)
        {
            RemoveDuelItemIfGranted(
                pawn,
                itemName,
                itemsBeforeMatch.Contains(itemName));
        }

        RestoreDuelItemHealthRegen(
            pawn,
            "upgrade_health",
            itemsBeforeMatch.Contains("upgrade_health"));

        ClampHealthToMax(pawn);
        pawn.RemoveModifier("modifier_speed_boost");
        RemovePermanentBurstFire(pawn);
    }

    // Swift Striker at its normal value (20% fire rate) in duels: a changed value only
    // exists on the server, so players' games predicted shots at a different fire
    // rate than the server fired them.
    public void ApplyDuelBlitzBullets(CCitadelPlayerPawn pawn)
    {
        if (
            pawn.AbilityComponent?.FindAbilityByName("upgrade_blitz_bullets")
            == null)
        {
            pawn.AddItem("upgrade_blitz_bullets");
        }
    }

    public void RestoreDuelBlitzBullets(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch)
    {
        if (!hadItemBeforeMatch)
        {
            pawn.RemoveItem("upgrade_blitz_bullets");
        }
    }

    public void RemoveDuelItemIfGranted(
        CCitadelPlayerPawn pawn,
        string itemName,
        bool hadItemBeforeMatch)
    {
        if (!hadItemBeforeMatch)
        {
            pawn.RemoveItem(itemName);
        }
    }

    public void ApplyDuelItemStack(CCitadelPlayerPawn pawn)
    {
        foreach (string itemName in DuelItemStack)
        {
            if (pawn.AbilityComponent?.FindAbilityByName(itemName) == null)
            {
                pawn.AddItem(itemName);
            }
        }

        ApplyDuelHighVelocityRounds(pawn);
        this.ApplyDuelBlitzBullets(pawn);
        this.ApplyPermanentBurstFire(pawn);
    }

    public void RestoreDuelHighVelocityRounds(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch)
    {
        const string itemName = "upgrade_high_velocity_mag";
        pawn.RemoveModifier($"{itemName}/modifier_intrinsic_base");
        if (hadItemBeforeMatch)
        {
            pawn.AddModifier($"{itemName}/modifier_intrinsic_base");
        }
        else
        {
            pawn.RemoveItem(itemName);
        }
    }

    private static void ApplyDuelHighVelocityRounds(CCitadelPlayerPawn pawn)
    {
        const string itemName = "upgrade_high_velocity_mag";
        const string modifierName = $"{itemName}/modifier_intrinsic_base";
        if (pawn.AbilityComponent?.FindAbilityByName(itemName) == null)
        {
            Console.WriteLine(
                "[aimLOCK] Cannot set duel bullet speed; High-Velocity Mag is missing.");
            return;
        }

        pawn.RemoveModifier(modifierName);
        pawn.AddModifier(
            modifierName,
            abilityValues: new()
            {
                ["BonusBulletSpeedPercent"] = DuelHighVelocityRoundsBulletSpeedPercent,
            });
    }

    /// <summary>
    /// Burst Fire's buff permanently on, instead of only after a hit with a cooldown
    /// in between. Applied with the item as its source so the item's own values are
    /// used, and re-applied on every respawn (ApplyDuelItemStack runs then).
    /// </summary>
    public void ApplyPermanentBurstFire(CCitadelPlayerPawn pawn)
    {
        var burstFire = pawn.AbilityComponent?.FindAbilityByName(BurstFireItemName);
        if (burstFire == null)
        {
            return;
        }

        for (int i = 0; i < 4 && pawn.RemoveModifier(BurstFireBuffName); i++)
        {
        }

        using var kv = new KeyValues3();
        kv.SetFloat("duration", PermanentModifierDuration);
        pawn.AddModifier(BurstFireBuffName, kv, pawn, burstFire);
    }

    public void RemovePermanentBurstFire(CCitadelPlayerPawn pawn)
    {
        for (int i = 0; i < 4 && pawn.RemoveModifier(BurstFireBuffName); i++)
        {
        }
    }

    public void ApplyDuelMatchModifiers(CCitadelPlayerPawn pawn)
    {
        this.ApplyHazeLoadout(pawn);

        // Not skipped by the loadout's once-per-burst check; does nothing at max level.
        ApplyMaxBoons(pawn);
    }

    private static void RestoreDuelItemHealthRegen(
        CCitadelPlayerPawn pawn,
        string itemName,
        bool hadItemBeforeMatch)
    {
        string modifierName = $"{itemName}/modifier_intrinsic_base";
        pawn.RemoveModifier(modifierName);
        if (hadItemBeforeMatch)
        {
            pawn.AddModifier(modifierName);
        }
    }

    /// <summary>
    /// The spawn loadout: max boons, Sleep Dagger / Smoke Bomb / Bullet Dance removed,
    /// Fixation unlocked with every upgrade and a 999 stack cap. Safe to call repeatedly.
    /// </summary>
    public void ApplyHazeLoadout(CCitadelPlayerPawn pawn, bool force = false)
    {
        if (!pawn.IsValid)
        {
            return;
        }

        // Several events ask for the loadout around the same moment (hero initialized,
        // respawn, spawn completed, match start). Apply it once per pawn per burst; the
        // hero-initialized hook passes force because abilities were just rebuilt.
        uint pawnHandle = pawn.EntityHandle;
        long now = Environment.TickCount64;
        if (this.loadoutAppliedAt.Count > 256)
        {
            this.loadoutAppliedAt.Clear();
        }

        if (
            !force &&
            this.loadoutAppliedAt.TryGetValue(pawnHandle, out long appliedAt) &&
            now - appliedAt < LoadoutReapplyWindowMs)
        {
            return;
        }

        this.loadoutAppliedAt[pawnHandle] = now;

        // The practice bot never casts abilities (its inputs are only movement and
        // shooting), but it gets the same max boons as players so its health matches.
        if (pawn.Controller is { IsBot: true })
        {
            ApplyMaxBoons(pawn);
            this.ApplyFixation(pawn);
            return;
        }

        ApplyMaxBoons(pawn);
        RemoveDisabledAbilities(pawn);
        this.ApplyFixation(pawn);
    }

    /// <summary>
    /// Max boons: the level is written directly and a zero-amount currency change makes
    /// the game recalculate the boon stats. This is how Deadworks' own Deathmatch example
    /// does it. (Feeding souls instead only reached level 3.)
    /// </summary>
    private static void ApplyMaxBoons(CCitadelPlayerPawn pawn)
    {
        if (pawn.Level >= MaxBoonLevel)
        {
            return;
        }

        pawn.Level = MaxBoonLevel;
        pawn.ModifyCurrency(
            ECurrencyType.EGold,
            0,
            ECurrencySource.ECheats,
            silent: true);
    }

    private static void RemoveDisabledAbilities(CCitadelPlayerPawn pawn)
    {
        foreach (string abilityName in DisabledHazeAbilities)
        {
            if (pawn.AbilityComponent?.FindAbilityByName(abilityName) != null)
            {
                pawn.RemoveAbility(abilityName);
            }
        }
    }

    private void GrantInitialAbilityCurrencies(
        CCitadelPlayerController controller,
        CCitadelPlayerPawn pawn)
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !pawn.IsValid ||
            this.initialAbilityCurrenciesGranted.Contains(controllerHandle))
        {
            return;
        }

        int abilityPoints = pawn.GetCurrency(ECurrencyType.EAbilityPoints);
        int abilityUnlocks = pawn.GetCurrency(ECurrencyType.EAbilityUnlocks);
        pawn.SetCurrency(
            ECurrencyType.EAbilityPoints,
            abilityPoints > int.MaxValue - InitialAbilityCurrencyGrant
                ? int.MaxValue
                : abilityPoints + InitialAbilityCurrencyGrant);
        pawn.SetCurrency(
            ECurrencyType.EAbilityUnlocks,
            abilityUnlocks > int.MaxValue - InitialAbilityCurrencyGrant
                ? int.MaxValue
                : abilityUnlocks + InitialAbilityCurrencyGrant);
        this.initialAbilityCurrenciesGranted.Add(controllerHandle);
    }
}
