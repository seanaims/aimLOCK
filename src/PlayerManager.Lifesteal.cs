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
    // Server lifesteal: 100% of bullet damage dealt everywhere, except duels and bot
    // matches, which use DuelLifestealPercent (85% unless changed with /lifesteal).
    private const float DefaultServerLifestealPercent = 100f;
    private const float DefaultDuelLifestealPercent = 85f;
    private const float LegacyHandicapLifestealReduction = 10f;
    private const float HardBotLifestealPercent = 40f;
    public float DuelLifestealPercent { get; private set; } = DefaultDuelLifestealPercent;

    public void SetDuelLifestealPercent(float percent) =>
        DuelLifestealPercent = Math.Clamp(percent, 0f, 100f);

    /// <summary>
    /// Duel bullets deal DUEL_BULLET_DAMAGE / BASE_DUEL_BULLET_DAMAGE times their normal
    /// damage (25 -> 40) while a duel's combat is live. Runs for every damage instance
    /// on the server, so it bails out as early and cheaply as possible.
    /// </summary>
    public void ApplyDuelBulletDamage(TakeDamageEvent args)
    {
        if (DuelBulletDamageMultiplier == 1f || server.ActiveMatches.Count == 0)
            return;

        var info = args.Info;
        if (info.CitadelDamageType != ECitadelDamageType.Bullet)
            return;

        // As<> instead of an `is` cast: event entities come in as the base wrapper type.
        var victimPawn = args.Entity.As<CCitadelPlayerPawn>();
        if (
            victimPawn?.Controller is not { } victim ||
            !TryGetActiveMatch(victim, out var match) ||
            match.Finished ||
            !match.CombatActive
        )
            return;

        info.Damage *= DuelBulletDamageMultiplier;
    }

    private const float DuelBulletDamageMultiplier =
        aimLOCK.DUEL_BULLET_DAMAGE / aimLOCK.BASE_DUEL_BULLET_DAMAGE;

    public void ApplyDuelBulletLifesteal(
        CCitadelPlayerPawn pawn
    )
    {
        // Duel lifesteal is applied server-side by ApplyServerLifesteal. Strip the item's
        // bullet lifesteal for the duel so the two don't stack; RestoreDuelBulletLifesteal
        // gives it back after the match if the player owned the item.
        pawn.RemoveModifier("upgrade_vampire/modifier_intrinsic_base");
    }

    /// <summary>
    /// Server-side lifesteal: when a player loses health to another player's bullets, the
    /// attacker is healed for a share of the health actually removed (after resists,
    /// capped at the victim's remaining health): 100% by default, DuelLifestealPercent
    /// (or the attacker's /handicap value) in duels and bot matches. Bullets only unless
    /// DUEL_LIFESTEAL_BULLETS_ONLY is false; Fixation's damage never heals. Doesn't depend
    /// on any item being owned.
    /// </summary>
    public void ApplyServerLifesteal(ModifierEvent args)
    {
        if (args.Damage is not { } damage)
            return;

        int healthLost = damage.HealthLost;
        if (healthLost <= 0)
            return;

        LedgerAdd(damage.Victim, -healthLost);

        var info = damage.Info;
        if (info == null)
            return;

        var attackerEntity = damage.Attacker;
        var victimEntity = damage.Victim;
        bool debugging = lifestealDebugSlots.Count != 0;

        // Fixation's stack damage arrives as its own bullet-type hit in the same server
        // tick as the bullet itself, not tagged with the ability (Haze fires one bullet
        // per shot, and shots are several ticks apart). It heals like the bullet when
        // FixationLifesteal is on; either way it isn't counted as an accuracy hit.
        bool fixationHit =
            server.IsFixationAbility(DamageAbilityHandle.Get(info.Handle)) ||
            (
                info.CitadelDamageType == ECitadelDamageType.Bullet &&
                IsSameTickFollowUpHit(attackerEntity, victimEntity)
            );
        if (fixationHit && !FixationLifesteal)
        {
            if (debugging)
                LifestealDebugSkip(attackerEntity, victimEntity, healthLost, "Fixation damage");
            return;
        }

        if (info.CitadelDamageType != ECitadelDamageType.Bullet && aimLOCK.DUEL_LIFESTEAL_BULLETS_ONLY)
        {
            if (debugging)
                LifestealDebugSkip(attackerEntity, victimEntity, healthLost,
                    $"not a bullet ({info.CitadelDamageType}, {GetDamageAbilityName(info)})");
            return;
        }

        if (
            attackerEntity == null ||
            victimEntity == null ||
            attackerEntity.EntityHandle == victimEntity.EntityHandle
        )
            return;

        var attacker = attackerEntity.As<CCitadelPlayerPawn>();
        var victim = victimEntity.As<CCitadelPlayerPawn>();
        if (
            attacker is not { IsValid: true } ||
            victim is not { IsValid: true } ||
            attacker.Controller is not { } attackerController ||
            victim.Controller is not { } victimController
        )
        {
            if (debugging)
                LifestealDebugSkip(attackerEntity, victimEntity, healthLost,
                    "attacker is not a player's hero");
            return;
        }

        if (info.CitadelDamageType == ECitadelDamageType.Bullet && !fixationHit)
            CountAccuracyHit(attacker, victim, info);

        if (!attacker.IsAlive)
        {
            if (debugging)
                LifestealDebugSkip(attackerEntity, victimEntity, healthLost, "attacker is dead");
            return;
        }

        float percent;
        if (server.ActiveMatches.Count != 0 && TryGetActiveMatch(attackerController, out var match))
        {
            string? reason =
                match.Finished ? "match finished" :
                !match.CombatActive ? "round not live (countdown or between rounds)" :
                !TryGetActiveMatch(victimController, out var victimMatch) ||
                    !ReferenceEquals(match, victimMatch) ? "target is not your opponent" :
                null;
            if (reason != null)
            {
                if (debugging)
                    LifestealDebugSkip(attackerEntity, victimEntity, healthLost, reason);
                return;
            }

            percent = GetDuelLifestealPercent(attackerController, match);
        }
        else
        {
            // Outside a duel: never heal off someone who is in one.
            if (server.ActiveMatches.Count != 0 && TryGetActiveMatch(victimController, out _))
            {
                if (debugging)
                    LifestealDebugSkip(attackerEntity, victimEntity, healthLost,
                        "target is in a duel and you are not");
                return;
            }

            percent = DefaultServerLifestealPercent;
        }

        // Fractions of a point carry over to the next hit instead of being dropped
        // (the game's heal only takes whole points).
        uint attackerHandle = attacker.EntityHandle;
        lifestealRemainders.TryGetValue(attackerHandle, out float carried);
        float heal = healthLost * percent / 100f + carried;
        int wholeHeal = (int)heal;
        if (lifestealRemainders.Count > 256)
            lifestealRemainders.Clear();
        lifestealRemainders[attackerHandle] = heal - wholeHeal;
        if (wholeHeal <= 0)
            return;

        int maxHealth = attacker.GetMaxHealth();
        int before = attacker.Health;
        int expected = Math.Max(0, Math.Min(wholeHeal, maxHealth - before));

        // The game's own heal first: it updates the health bar through the normal heal
        // path. If something in the game cut it short, the missing part is written
        // directly, so the heal is always the full amount.
        applyingLifesteal = true;
        int healed;
        try
        {
            healed = expected > 0 ? attacker.Heal(wholeHeal) : 0;
        }
        finally
        {
            applyingLifesteal = false;
        }
        bool topped = false;
        if (healed < expected)
        {
            int target = Math.Min(maxHealth, before + expected);
            if (attacker.Health < target)
            {
                attacker.Health = target;
                topped = true;
            }
        }
        LedgerAdd(attacker, topped ? expected : healed);

        if (debugging)
            LifestealDebugHit(attacker, victim, healthLost, percent, expected, healed, topped,
                before, maxHealth, GetDamageAbilityName(info));
    }

    // Fixation's stack damage heals at the same lifesteal percent as the bullet.
    private const bool FixationLifesteal = true;

    // (attacker handle, victim handle) -> server tick of the last bullet hit.
    private readonly Dictionary<ulong, int> lastBulletHitTick = new();

    private bool IsSameTickFollowUpHit(CBaseEntity? attacker, CBaseEntity? victim)
    {
        if (attacker == null || victim == null)
            return false;

        ulong key = ((ulong)attacker.EntityHandle << 32) | victim.EntityHandle;
        int tick = GlobalVars.TickCount;
        if (lastBulletHitTick.TryGetValue(key, out int lastTick) && lastTick == tick)
            return true;

        if (lastBulletHitTick.Count > 256)
            lastBulletHitTick.Clear();
        lastBulletHitTick[key] = tick;
        return false;
    }

    private static string GetDamageAbilityName(CTakeDamageInfo info) =>
        CBaseEntity.FromHandle(DamageAbilityHandle.Get(info.Handle))?.As<CCitadelBaseAbility>()?.AbilityName
        ?? "no ability";

    // Set while the plugin's own lifesteal heal runs, so its heal event isn't undone.
    private bool applyingLifesteal;

    /// <summary>
    /// In a live duel the plugin's lifesteal is the only healing: any other heal (health
    /// regen, healing from items a player bought, anything else) is undone as it lands.
    /// A point-blank test showed one player outlasting the other by ~1,950 health that
    /// didn't come from lifesteal.
    /// </summary>
    public void BlockOtherDuelHealing(ModifierEvent args)
    {
        if (applyingLifesteal || args.Damage is not { } heal)
            return;

        int applied = heal.HealthApplied;
        if (applied <= 0)
            return;

        var pawn = heal.Victim?.As<CCitadelPlayerPawn>();
        if (
            pawn is not { IsValid: true, IsAlive: true } ||
            pawn.Controller is not { } controller ||
            !TryGetActiveMatch(controller, out var match) ||
            match.Finished ||
            !match.CombatActive
        )
            return;

        pawn.Health = Math.Max(1, pawn.Health - applied);

        if (
            lifestealDebugSlots.Count != 0 &&
            GetLifestealDebugStats(pawn) is { } stats
        )
        {
            stats.OtherHealingBlocked += applied;
            Console.WriteLine($"[aimLOCK] [lsdebug] blocked {applied} healing that wasn't lifesteal");
        }
    }

    // Attacker pawn -> the fraction of a heal point carried over from the last hit.
    private readonly Dictionary<uint, float> lifestealRemainders = new();

    /* /lsdebug */

    // Slots of players watching lifesteal. Empty = the debug code costs one Count check.
    private readonly HashSet<int> lifestealDebugSlots = new();
    private readonly Dictionary<int, LifestealDebugStats> lifestealDebugStats = new();
    private bool lifestealDebugTimerRunning;

    private sealed class LifestealDebugStats
    {
        public int Hits;
        public int DamageDealt;
        public int Expected;
        public int Healed;
        public int Topped;
        public int AtFullHealth;
        public int Skipped;
        public int DamageTaken;
        public int OpponentHealed;
        public int OtherHealingBlocked;
        public int UnexplainedGain;
        public int UnreportedLoss;
        public string? LastSkipReason;
    }

    public bool ToggleLifestealDebug(CCitadelPlayerController player)
    {
        int slot = player.Slot;
        if (lifestealDebugSlots.Remove(slot))
        {
            lifestealDebugStats.Remove(slot);
            return false;
        }

        lifestealDebugSlots.Add(slot);
        lifestealDebugStats[slot] = new LifestealDebugStats();
        if (!lifestealDebugTimerRunning)
        {
            lifestealDebugTimerRunning = true;
            server.ScheduleOnce(1.Seconds(), LifestealDebugReport);
        }

        return true;
    }

    public void RemoveLifestealDebug(CCitadelPlayerController player)
    {
        lifestealDebugSlots.Remove(player.Slot);
        lifestealDebugStats.Remove(player.Slot);
    }

    private LifestealDebugStats? GetLifestealDebugStats(CBaseEntity? pawnEntity)
    {
        var controller = pawnEntity?.As<CCitadelPlayerPawn>()?.Controller;
        if (controller == null || !lifestealDebugSlots.Contains(controller.Slot))
            return null;

        return lifestealDebugStats.TryGetValue(controller.Slot, out var stats) ? stats : null;
    }

    private void LifestealDebugSkip(
        CBaseEntity? attacker,
        CBaseEntity? victim,
        int healthLost,
        string reason
    )
    {
        if (GetLifestealDebugStats(attacker) is { } attackerStats)
        {
            attackerStats.Skipped++;
            attackerStats.DamageDealt += healthLost;
            attackerStats.LastSkipReason = reason;
            Console.WriteLine($"[aimLOCK] [lsdebug] no heal for {healthLost} damage dealt: {reason}");
        }

        if (GetLifestealDebugStats(victim) is { } victimStats)
            victimStats.DamageTaken += healthLost;
    }

    private void LifestealDebugHit(
        CCitadelPlayerPawn attacker,
        CCitadelPlayerPawn victim,
        int healthLost,
        float percent,
        int expected,
        int healed,
        bool topped,
        int before,
        int maxHealth,
        string source
    )
    {
        if (GetLifestealDebugStats(attacker) is { } stats)
        {
            stats.Hits++;
            stats.DamageDealt += healthLost;
            stats.Expected += expected;
            stats.Healed += topped ? expected : healed;
            if (topped)
                stats.Topped++;
            if (before >= maxHealth)
                stats.AtFullHealth++;
            Console.WriteLine(
                $"[aimLOCK] [lsdebug] hit {healthLost} ({source}) at {percent:0.#}%: health {before}/{maxHealth}, " +
                $"heal {expected}, game healed {healed}{(topped ? ", rest written directly" : "")}"
            );
        }

        if (GetLifestealDebugStats(victim) is { } victimStats)
        {
            victimStats.DamageTaken += healthLost;
            victimStats.OpponentHealed += topped ? expected : healed;
        }
    }

    private void LifestealDebugReport()
    {
        if (lifestealDebugSlots.Count == 0)
        {
            lifestealDebugTimerRunning = false;
            return;
        }

        server.ScheduleOnce(1.Seconds(), LifestealDebugReport);
        foreach (var player in Players.GetAll())
        {
            if (
                player.IsBot ||
                !lifestealDebugSlots.Contains(player.Slot) ||
                !lifestealDebugStats.TryGetValue(player.Slot, out var stats) ||
                stats.Hits + stats.Skipped + stats.DamageTaken + stats.OtherHealingBlocked +
                    stats.UnexplainedGain + stats.UnreportedLoss == 0
            )
                continue;

            int net = stats.Healed - stats.DamageTaken;
            string line =
                $"[lsdebug] 1s: dealt {stats.DamageDealt} in {stats.Hits + stats.Skipped} hits, " +
                $"healed {stats.Healed}/{stats.Expected}" +
                (stats.AtFullHealth > 0 ? $" ({stats.AtFullHealth} hits at full HP)" : "") +
                (stats.Topped > 0 ? $", game heal short {stats.Topped}x" : "") +
                (stats.Skipped > 0 ? $", {stats.Skipped} hits no heal: {stats.LastSkipReason}" : "") +
                $" | took {stats.DamageTaken}, they healed {stats.OpponentHealed} | your net {(net >= 0 ? "+" : "")}{net}" +
                (stats.OtherHealingBlocked > 0 ? $" | blocked {stats.OtherHealingBlocked} other healing" : "") +
                (stats.UnexplainedGain > 0 ? $" | took back {stats.UnexplainedGain} health from nowhere" : "") +
                (stats.UnreportedLoss > 0 ? $" | lost {stats.UnreportedLoss} with no damage event" : "") +
                DescribeDuelHealth(player);
            Chat.PrintToChat(player, line);
            Console.WriteLine($"[aimLOCK] {line} ({GetCachedPlayerName(player)})");
            lifestealDebugStats[player.Slot] = new LifestealDebugStats();
        }
    }

    private string DescribeDuelHealth(CCitadelPlayerController player)
    {
        if (
            server.ActiveMatches.Count == 0 ||
            !TryGetActiveMatch(player, out var match) ||
            match.Finished
        )
            return "";

        var opponent = FindMatchParticipant(
            string.Equals(match.Player1, GetMatchParticipantName(player), StringComparison.OrdinalIgnoreCase)
                ? match.Player2
                : match.Player1
        );
        int mine = player.GetHeroPawn()?.Health ?? 0;
        int theirs = opponent?.GetHeroPawn()?.Health ?? 0;
        return $" | HP now: you {mine}, them {theirs}";
    }

    private static readonly SchemaAccessor<uint> DamageAbilityHandle =
        new("CTakeDamageInfo"u8, "m_hAbility"u8);

    /// <summary>
    /// Duel lifesteal percent for an attacker: the duel's agreed value (or
    /// DuelLifestealPercent), or the player's own /handicap value when they set one
    /// (never above the duel's value). Easy and
    /// medium bots use DuelLifestealPercent; the hard bot uses HardBotLifestealPercent.
    /// </summary>
    private float GetDuelLifestealPercent(CBasePlayerController attacker, DuelMatch match)
    {
        // The hard bot heals for less than everyone else.
        if (
            practiceBots.Count != 0 &&
            practiceBots.TryGetValue(attacker.EntityHandle, out var bot) &&
            bot.IsHard
        )
            return HardBotLifestealPercent;

        // The duel's own lifesteal when both players agreed on one (/duel ... ls N),
        // otherwise the server's duel lifesteal.
        float duelPercent = match.LifestealPercent ?? DuelLifestealPercent;
        if (
            attacker is CCitadelPlayerController player &&
            !player.IsBot &&
            server.PlayerRecords.TryGetValue(player.PlayerSteamId, out var record) &&
            record.HandicapEnabled
        )
        {
            float handicap =
                record.HandicapLifestealPercent
                ?? duelPercent - LegacyHandicapLifestealReduction;
            return Math.Clamp(handicap, 0f, duelPercent);
        }

        return duelPercent;
    }
}
