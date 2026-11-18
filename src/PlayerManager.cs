/*
This file handels player activity and matches
forces haze, bot spawning, player connections, score tracking, match completion, respawning
*/

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

public class PlayerManager
{
    private static readonly JsonSerializerOptions PlayerRecordJsonOptions = new()
    {
        WriteIndented = true
    };
    public const string RecordsFileName = "records.json";
    private static readonly string RecordsFilePath = JsonStore.PathFor(RecordsFileName);
    private static readonly Vector3 ArenaScoreboardAngles =
        new(180f, 90f, 270f);

    private const string FixationAbilityName = "ability_stacking_damage";
    private const string FixationModifierName =
        "ability_stacking_damage/modifier_passive_haze_stacking_damage";
    private const short FixationMaxStacks = 999;
    private const int FullyUpgradedAbilityBits = 0xF;
    // Max boons. The boon cap is 35; the level that grants it is 36 (the value the
    // Deadworks deathmatch example writes for a fully levelled hero).
    private const int MaxBoonLevel = 36;
    private const string BurstFireItemName = "upgrade_burst_fire";
    private const string BurstFireBuffName = "upgrade_burst_fire/modifier_burst_fire_buff";
    // Long enough to outlast any match; it is removed when the match ends.
    private const float PermanentModifierDuration = 36000f;
    // Haze abilities that are disabled: Sleep Dagger, Smoke Bomb (invisibility) and
    // Bullet Dance (ultimate). Fixation is the only ability left.
    private static readonly string[] DisabledHazeAbilities =
    {
        "ability_sleep_dagger",
        "ability_smoke_bomb",
        "ability_bullet_flurry",
    };
    private const long LoadoutReapplyWindowMs = 1000;
    private readonly Dictionary<uint, long> loadoutAppliedAt = new();
    private const int InitialAbilityCurrencyGrant = 30;

    // Keep the test bot's Elo entry separate from real Steam accounts.
    private const ulong BotRankedEloId = ulong.MaxValue;
    private const int InjectedSoulsBalance = 1_000_000_000;
    private const int SoulsCleanupRetries = 10;
    private const float MinPracticeBotStrafeSpeed = 100f;
    private const float MaxPracticeBotStrafeSpeed = 2500f;
    private const float PracticeBotDuelMinRange = 160f;
    private const float PracticeBotDuelMaxRange = 200f;
    private const float PracticeBotArenaBoundaryMargin = 64f;
    private const float PracticeBotDoorwayLabelFontSize = 68f;
    private const float PracticeBotDoorwayLabelWorldUnitsPerPx = 0.3f;
    private const float PracticeBotDoorwayLabelHorizontalPadding = 80f;
    private const float QueueEntryLabelHorizontalPadding = 180f;
    private static readonly Vector3[] RemovedBotsLabelPositions =
    {
        new(-745f, -9525f, 1265f),
        new(-821f, -9628f, 1244f),
        new(-788f, -9568f, 1248f)
    };
    private const byte PracticeBotDoorwayLabelBackgroundAlpha = 160;
    private const char PracticeBotDoorwayLabelBackgroundGlyph = '\u2588';
    private const float PracticeBotDoorwayLabelBackgroundDepthOffset = -1f;
    private const float PracticeBotDoorwayLabelNearbyDistanceSquared = 25f;
    private static readonly Vector3 PracticeBotDoorwayLabelAngles =
        new(180f, 90f, 270f);
    private static readonly PracticeBotZone[] PracticeBotZones =
    {
        new(
            new Vector3(-555f, -9208f, 1198f),
            140f,
            "easy",
            "Easy Bot",
            0xFF00FF00,
            new Vector3(-545f, -9244f, 1473f)
        ),
        new(
            new Vector3(-205f, -9100f, 1217f),
            120f,
            "medium",
            "Medium Bot",
            0xFF00A5FF,
            new Vector3(-175f, -9191f, 1392f)
        ),
        new(
            new Vector3(-65f, -8945f, 1258f),
            120f,
            "hard",
            "Hard Bot",
            0xFF0000FF,
            new Vector3(45f, -8931f, 1433f)
        )
    };
    private static readonly QueueEntryZone[] QueueEntryZones =
    {
        new(
            new Vector3(-2079f, -9270f, 1211f),
            100f,
            MatchQueueType.Unranked,
            "UNRANKED QUEUE",
            0xFF00FF00,
            new Vector3(-2079f, -9270f, 1411f)
        ),
        new(
            new Vector3(-2298f, -9224f, 1211f),
            100f,
            MatchQueueType.Ranked,
            "RANKED QUEUE",
            0xFFFF0000,
            new Vector3(-2353f, -9224f, 1411f)
        ),
        new(
            new Vector3(-2546f, -8967f, 1214f),
            100f,
            null,
            "LEAVE QUEUE",
            0xFF0000FF,
            new Vector3(-2571f, -8942f, 1414f)
        )
    };
    private const float DuelArenaCourtHalfSize = 250f;
    private const float DuelArenaCourtHeight = 200f;
    private const float DuelArenaPlayerMargin = 48f;
    private const float DuelArenaBarrierExpansion = 200f;
    private const float DuelArenaRadiusMarkerWidth = 2f;
    // 32 segments per circle (was 48): 416 beam entities for 13 arenas instead of 624,
    // which the server has to check for every player every tick. Still reads as round.
    private const int DuelArenaCircleSegments = 32;
    private static readonly float DuelArenaSphereRadius = MathF.Sqrt(
        2f * DuelArenaCourtHalfSize * DuelArenaCourtHalfSize +
        DuelArenaCourtHeight * DuelArenaCourtHeight / 4f
    ) + DuelArenaPlayerMargin * 2f + DuelArenaBarrierExpansion;
    private static readonly float PracticeBotDuelPlayerRadius =
        DuelArenaSphereRadius - DuelArenaPlayerMargin;
    private static readonly float PracticeBotMaximumHorizontalRadius =
        MathF.Sqrt(MathF.Max(
            0f,
            PracticeBotDuelPlayerRadius * PracticeBotDuelPlayerRadius -
                DuelArenaCourtHeight * DuelArenaCourtHeight / 4f
        ));
    private static readonly float PracticeBotMaximumPossibleRange =
        2f * PracticeBotMaximumHorizontalRadius;
    private static readonly float PracticeBotMinimumRange =
        PracticeBotMaximumPossibleRange >= PracticeBotDuelMinRange
            ? PracticeBotDuelMinRange
            : PracticeBotMaximumPossibleRange * 0.7f;
    private static readonly float PracticeBotMaximumRange =
        Math.Min(
            PracticeBotDuelMaxRange,
            PracticeBotMaximumPossibleRange
        );
    private static readonly float PracticeBotMinimumRangeSquared =
        PracticeBotMinimumRange * PracticeBotMinimumRange;
    private static readonly float PracticeBotMaximumRangeSquared =
        PracticeBotMaximumRange * PracticeBotMaximumRange;
    private static readonly float DuelArenaOuterRadius =
        DuelArenaSphereRadius + DuelArenaPlayerMargin;
    private static readonly float DuelArenaOuterRadiusSquared =
        DuelArenaOuterRadius * DuelArenaOuterRadius;
    // Beyond this distance from every arena nobody can reach a wall within
    // FarArenaCheckIntervalMs, so the arena search pauses for that long.
    private static readonly float FarArenaRadius =
        DuelArenaOuterRadius + FarArenaSafetyDistance;
    private static readonly float FarArenaRadiusSquared =
        FarArenaRadius * FarArenaRadius;
    private static readonly Color DuelArenaCircleColor =
        Color.FromArgb(128, 255, 255, 255);
    // Server lifesteal: 100% of bullet damage dealt everywhere, except duels and bot
    // matches, which use DuelLifestealPercent (85% unless changed with /lifesteal).
    private const float DefaultServerLifestealPercent = 100f;
    private const float DefaultDuelLifestealPercent = 85f;
    private const float LegacyHandicapLifestealReduction = 10f;
    private const float HardBotLifestealPercent = 40f;
    public float DuelLifestealPercent { get; private set; } = DefaultDuelLifestealPercent;

    public void SetDuelLifestealPercent(float percent) =>
        DuelLifestealPercent = Math.Clamp(percent, 0f, 100f);
    private const int MaxPlayerSlots = 64;
    private const long PracticeBotZoneCheckIntervalMs = 100;
    private const long FarArenaCheckIntervalMs = 100;
    // Larger than anyone can travel in FarArenaCheckIntervalMs (dashes included).
    private const float FarArenaSafetyDistance = 600f;
    private const long PlayerNameCacheLifetimeMs = 1000;
    // Arena boundary: players slightly outside are pushed back by velocity only (no
    // teleport, so lag compensation and interpolation are not broken). Only a player
    // this far outside (dash, knockback, teleport) is hard-teleported back in.
    private const float BoundaryHardCorrectionDistance = 96f;
    private const float BoundaryMinPushBackSpeed = 60f;
    private const float BoundaryMaxPushBackSpeed = 350f;
    private const float DefaultBulletLifestealPercent = 28f;
    private const float DefaultBlitzBulletsFireRatePercent = 22f;
    private const float ArenaScoreboardFontSize = 150f;
    private const float ArenaScoreboardWorldUnitsPerPx = 0.3f;
    private const float ArenaScoreboardHeightAboveCourt = 80f;
    private const float ArenaScoreboardBackgroundDepthOffset = -1f;
    private const int ArenaScoreboardBackgroundPadding = 8;
    private const byte ArenaScoreboardBackgroundAlpha = 160;
    private const char ArenaScoreboardBackgroundGlyph = '\u2588';
    private const float DuelBlitzBulletsFireRatePercent =
        DefaultBlitzBulletsFireRatePercent / 2f;
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
    private const float DuelHighVelocityRoundsBulletSpeedPercent = 100f;
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
    private const string MatchTeleportEffect =
        "particles/items/gold_received_coins_player.vpcf";
    // Only used by the /bottest fx debug option.
    private const string RespawnTeleportEffect =
        "particles/generic/player_respawn_deploy.vpcf";
    // The Haze model players use. Practice bots were spawning with an old model path
    // (models/heroes_wip/haze/haze.vmdl) that no longer exists in the game; clients
    // logged it as missing and then crashed in client.dll while updating the bot.
    public const string HazeModel = "models/heroes_staging/haze/haze.vmdl";
    // Hero briefly selected on connect before swapping to Haze.
    public const string TemporaryConnectHeroName = "hero_inferno";
    // Precached at map load (see aimLOCK.OnPrecacheResources).
    public static readonly string[] PrecachedParticles =
    {
        MatchTeleportEffect
    };
    public static readonly string[] PrecachedModels =
    {
        HazeModel
    };
    public const float DefaultPracticeBotStrafeSpeed = 2500f;
    private static readonly PracticeBotDifficultyProfile EasyBotProfile = new(
        1200f, 50f, 0.45d, 0.45d, 0.85d, 0.4d, 0.7d, 90f, 300f,
        60f, 20f, 0.25d
    );
    private static readonly PracticeBotDifficultyProfile MediumBotProfile = new(
        1800f, 50f, 0.65d, 0.18d, 0.38d, 0.55d, 0.85d, 75f, 260f,
        30f, 9f, 0.12d
    );
    private static readonly PracticeBotDifficultyProfile HardBotProfile = new(
        DefaultPracticeBotStrafeSpeed, 40f, 0.8d, 0.1d, 0.25d, 0.65d, 0.95d,
        80f, 300f, 18f, 6f, 0.1d
    );
    private readonly record struct PracticeBotDifficultyProfile(
        float StrafeSpeed,
        float ArrivalDistance,
        double DirectionChangeChance,
        double StrafeDurationMinimum,
        double StrafeDurationMaximum,
        double StepFactorMinimum,
        double StepFactorMaximum,
        float StepDistanceMinimum,
        float StepDistanceMaximum,
        float HorizontalAimError,
        float VerticalAimError,
        double AimUpdateInterval
    );
    private readonly record struct PracticeBotZone(
        Vector3 Center,
        float Radius,
        string Difficulty,
        string Label,
        uint LabelColorABGR,
        Vector3 LabelPosition
    )
    {
        public float RadiusSquared { get; } = Radius * Radius;
    }
    private readonly record struct QueueEntryZone(
        Vector3 Center,
        float Radius,
        MatchQueueType? QueueType,
        string Label,
        uint LabelColorABGR,
        Vector3 LabelPosition
    )
    {
        public float RadiusSquared { get; } = Radius * Radius;
    }
    private sealed class PracticeBotState
    {
        public uint ControllerHandle { get; init; }
        public string MatchKey { get; init; } = "";
        public string DisplayName { get; set; } = "";
        public uint OwnerControllerHandle { get; init; }
        public int OwnerSlot { get; init; } = -1;
        // Player slots (one bit each) that are sent this bot: its opponent and anyone
        // spectating its match. Rebuilt when botViewerMasksDirty is set.
        public ulong ViewerMask;
        public string OwnerName { get; init; } = "";
        public CCitadelPlayerController? TargetController { get; set; }
        public string DifficultyName { get; set; } = "Hard";
        public bool IsHard { get; set; } = true;
        public PracticeBotDifficultyProfile Difficulty { get; set; } =
            HardBotProfile;
        public Vector3 LookAt;
        public Vector3 AimError;
        public Vector3 StrafeTarget;
        public int StrafeDirection = 1;
        public DateTimeOffset NextMovementAdjustment;
        public DateTimeOffset NextAimUpdate;
    }
    private readonly aimLOCK server;
    private readonly HashSet<string> respawnRecoveryInProgress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, DuelMatch> spectatingMatches = new();
    private readonly HashSet<uint> initialHeroSelectionPending = new();
    private readonly HashSet<uint> initialHeroSelectionIssued = new();
    private readonly HashSet<uint> initialHeroSelectionCompleted = new();
    private readonly HashSet<uint> initialHeroHazeSelectionStarted = new();
    private readonly HashSet<uint> initialAbilityCurrenciesGranted = new();
    private readonly Dictionary<uint, uint> initialHeroPawnInitializationHandles =
        new();
    private bool initialSpawnRecoverySweepStarted;
    private readonly Dictionary<uint, PracticeBotState> practiceBots = new();
    private readonly Dictionary<uint, float> practiceBotStrafeSpeedOverrides =
        new();
    private readonly Dictionary<
        int,
        (DuelMatch Match, CPointWorldText Background, CPointWorldText Text)
    > arenaScoreboards = new();
    private bool practiceBotsPreviousAiEnabled;
    // Indexed by player slot * zone count + zone index. Avoids building a string key
    // for every zone on every usercmd batch.
    private readonly bool[] playersInsidePracticeBotZones =
        new bool[MaxPlayerSlots * PracticeBotZones.Length];
    private readonly bool[] playersInsideQueueEntryZones =
        new bool[MaxPlayerSlots * QueueEntryZones.Length];
    private readonly long[] nextPracticeBotZoneCheck = new long[MaxPlayerSlots];
    // Players nowhere near an arena only need the arena-entry check ~10 times a second.
    private readonly long[] nextFarArenaCheck = new long[MaxPlayerSlots];
    // PlayerName is read from native memory and allocates a new string on every read.
    // Hot paths (usercmds, damage, HUD clock) use this per-slot cache instead.
    private readonly string?[] cachedPlayerNames = new string?[MaxPlayerSlots];
    private readonly uint[] cachedPlayerNameHandles = new uint[MaxPlayerSlots];
    private readonly long[] cachedPlayerNameExpiry = new long[MaxPlayerSlots];
    private readonly List<CPointWorldText> entryZoneLabelEntities = new();
    private readonly List<CBeam> duelArenaRadiusBeams = new();
    private readonly Vector3[] duelArenaSphereCenters;
    private readonly Dictionary<ulong, (int ArenaIndex, bool IsInside)>
        duelArenaBoundaryStates = new();
    private readonly Dictionary<string, TimeZoneInfo> resolvedTimeZones =
        new(StringComparer.Ordinal);

    public PlayerManager(aimLOCK server)
    {
        this.server = server;
        duelArenaSphereCenters = new Vector3[server.DuelLocations.Length];
        for (int i = 0; i < duelArenaSphereCenters.Length; i++)
            duelArenaSphereCenters[i] =
                GetDuelArenaSphereCenter(server.DuelLocations[i]);
    }

    private bool duelLoopStarted;

    /// <summary>
    /// The one repeating duel loop (20 times a second, one pass over the players, and
    /// nothing at all while no duel is running). Per duelist it caps health at max
    /// health (every pass) and raises the Fixation stack limit (every 10th pass).
    /// Burst Fire is not polled here: see citadel_ability_cooldown_max.
    /// </summary>
    public void StartDuelLoop()
    {
        if (duelLoopStarted)
            return;

        duelLoopStarted = true;
        server.ScheduleOnce(DuelHealthCapCheckIntervalSeconds.Seconds(), DuelLoopTick);
    }

    private void DuelLoopTick()
    {
        server.ScheduleOnce(DuelHealthCapCheckIntervalSeconds.Seconds(), DuelLoopTick);
        ++duelLoopTick;

        // Souls: once a second, put every player back at the max in case the game
        // changed them in a way the currency hook didn't catch (hero setup, respawn).
        if (duelLoopTick % 20 == 0)
        {
            foreach (var player in Players.GetAll())
            {
                if (player.IsBot)
                    continue;
                var soulsPawn = player.GetHeroPawn();
                if (soulsPawn is { IsValid: true })
                    TopUpSouls(soulsPawn);
            }
        }

        if (server.ActiveMatches.Count == 0)
            return;

        bool checkStackCaps = duelLoopTick % 10 == 0;

        foreach (var player in Players.GetAll())
        {
            if (
                !TryGetActiveMatch(player, out var match) ||
                match.Finished ||
                !match.ArenaInitialized
            )
                continue;

            var pawn = player.GetHeroPawn();
            if (pawn is not { IsValid: true, IsAlive: true })
                continue;

            int maxHealth = pawn.GetMaxHealth();
            if (pawn.Health > maxHealth)
                pawn.Health = maxHealth;

            CheckDuelHealthLedger(pawn, match);

            if (checkStackCaps)
                RaiseFixationStackCap(pawn);
        }
    }

    /* Burst Fire always on */

    // Burst Fire never goes on cooldown because the server sets
    // citadel_ability_cooldown_max (see aimLOCK.ApplyServerConVars), the same way other
    // 1v1 servers do it; its buff is also applied permanently in duels
    // (ApplyPermanentBurstFire). Both use the game's own values, so players' games
    // predict the same fire rate as the server. Nothing runs per hit or on a timer.

    private int duelLoopTick;

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

    /* Fixation stack cap */

    // The Fixation stacks on the person being shot are their own modifier with their
    // own stack limit (80 with Fixation fully upgraded), separate from the passive whose
    // MaxStacks is set to 999. Raise that limit on the stack modifier itself.
    private const short FixationTargetMaxStacks = 999;
    private static readonly SchemaAccessor<short> ModifierMaxStackCount =
        new("CBaseModifier"u8, "m_iMaxStackCount"u8);
    private static readonly SchemaAccessor<short> ModifierStackCount =
        new("CBaseModifier"u8, "m_iStackCount"u8);

    private void RaiseFixationStackCap(CCitadelPlayerPawn pawn)
    {
        var modifiers = pawn.ModifierProp?.Modifiers;
        if (modifiers == null)
            return;

        foreach (var modifier in modifiers)
        {
            if (!IsFixationModifier(modifier))
                continue;

            short max = ModifierMaxStackCount.Get(modifier.Handle);
            if (max <= 0 || max >= FixationTargetMaxStacks)
                continue;

            // Plain write, no network notify: modifiers are not entities, and only
            // the server's stack logic needs the new limit.
            nint address = ModifierMaxStackCount.GetAddress(modifier.Handle);
            if (address - modifier.Handle <= 0x10 || address - modifier.Handle > 0x1000)
                return;

            System.Runtime.InteropServices.Marshal.WriteInt16(address, FixationTargetMaxStacks);
        }
    }

    /// <summary>
    /// /fixdebug: lists every modifier on you and your duel opponent that came from a
    /// Fixation ability, with its stack count and limit, to the chat and server log.
    /// </summary>
    public void FixationDebugCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
            return;

        var targets = new List<CCitadelPlayerController> { player };
        if (TryGetActiveMatch(player, out var match))
        {
            foreach (var participant in GetMatchParticipants(match))
            {
                if (participant.EntityHandle != player.EntityHandle)
                    targets.Add(participant);
            }
        }

        foreach (var target in targets)
        {
            var pawn = target.GetHeroPawn();
            var modifiers = pawn?.ModifierProp?.Modifiers;
            if (pawn == null || modifiers == null)
                continue;

            string who = GetCachedPlayerName(target);
            int found = 0;
            foreach (var modifier in modifiers)
            {
                uint abilityHandle = ModifierAbilityHandle.Get(modifier.Handle);
                string name = modifier.SubclassVData?.Name ?? "?";
                bool fromFixation = abilityHandle != 0 && abilityHandle != 0xFFFFFFFF &&
                    server.IsFixationAbility(abilityHandle);
                if (!fromFixation && !name.Contains("stacking", StringComparison.Ordinal))
                    continue;

                found++;
                string line =
                    $"[fixdebug] {who}: {name} stacks={ModifierStackCount.Get(modifier.Handle)} " +
                    $"max={ModifierMaxStackCount.Get(modifier.Handle)} fromFixation={fromFixation}";
                caller.Reply(line);
                Console.WriteLine($"[aimLOCK] {line}");
            }

            if (found == 0)
                caller.Reply($"[fixdebug] {who}: no Fixation modifiers.");
        }
    }

    public void PlayMatchTeleportEffect(CCitadelPlayerPawn pawn)
    {
        SpawnAttachedEffect(pawn, MatchTeleportEffect);
    }

    public void RemoveExistingBots()
    {
        foreach (var bot in Players.GetAll().Where(player => player.IsBot).ToArray())
        {
            bot.Kick(
                "Use /bot to spawn the practice target.",
                ENetworkDisconnectionReason.NetworkDisconnectKicked
            );
        }

        practiceBots.Clear();
        pendingBotMatchStarts.Clear();
        pendingBotReservations.Clear();
        pendingBotMatchPlayers.Clear();
        reservedBotArenas.Clear();
        RestorePracticeBotAiIfIdle();
    }

    public void StartPracticeBotMatch(
        Caller caller,
        string? difficulty = "hard"
    )
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to start a bot match.");
            return;
        }

        TryStartPracticeBotMatch(
            player,
            difficulty,
            message => caller.Reply(message)
        );
    }

    public void ProcessUsercmds(ProcessUsercmdsEvent args)
    {
        // Runs for every player's usercmd batch (roughly every tick per player): one
        // controller/pawn lookup shared by every handler.
        if (
            args.Paused ||
            args.Controller is not { IsValid: true } controller
        )
            return;

        var pawn = controller.GetHeroPawn();
        if (pawn is not { IsValid: true })
            return;

        DuelMatch? activeMatch = EnforceDuelArenaBounds(controller, pawn);
        if (controller is not CCitadelPlayerController player)
            return;

        if (player.IsBot)
            ProcessPracticeBotUsercmds(args, controller, pawn, activeMatch);
        else if (activeMatch == null)
            ProcessPracticeBotDoorwayZone(player, pawn);
    }

    private void ProcessPracticeBotDoorwayZone(
        CCitadelPlayerController player,
        CCitadelPlayerPawn pawn
    )
    {
        int slot = player.Slot;
        if ((uint)slot >= MaxPlayerSlots)
            return;

        // Zone entry doesn't need tick precision; check ~10 times a second. Indexed by
        // slot instead of a tuple set lookup per zone per usercmd.
        long now = Environment.TickCount64;
        if (now < nextPracticeBotZoneCheck[slot])
            return;
        nextPracticeBotZoneCheck[slot] = now + PracticeBotZoneCheckIntervalMs;

        Vector3 position = pawn.Position;
        for (int zoneIndex = 0; zoneIndex < PracticeBotZones.Length; zoneIndex++)
        {
            PracticeBotZone zone = PracticeBotZones[zoneIndex];
            int entryIndex = slot * PracticeBotZones.Length + zoneIndex;
            bool isInsideZone =
                Vector3.DistanceSquared(position, zone.Center) <=
                zone.RadiusSquared;

            if (!isInsideZone)
            {
                playersInsidePracticeBotZones[entryIndex] = false;
                continue;
            }

            if (playersInsidePracticeBotZones[entryIndex])
                continue;

            playersInsidePracticeBotZones[entryIndex] = true;
            TryStartPracticeBotMatch(
                player,
                zone.Difficulty,
                message => Chat.PrintToChat(player, message)
            );
        }

        for (int zoneIndex = 0; zoneIndex < QueueEntryZones.Length; zoneIndex++)
        {
            QueueEntryZone zone = QueueEntryZones[zoneIndex];
            int entryIndex = slot * QueueEntryZones.Length + zoneIndex;
            bool isInsideZone =
                Vector3.DistanceSquared(position, zone.Center) <=
                zone.RadiusSquared;

            if (!isInsideZone)
            {
                playersInsideQueueEntryZones[entryIndex] = false;
                continue;
            }

            if (playersInsideQueueEntryZones[entryIndex])
                continue;

            playersInsideQueueEntryZones[entryIndex] = true;
            if (zone.QueueType is MatchQueueType queueType)
                server.Queue.JoinQueueFromZone(player, queueType);
            else
                server.Queue.LeaveQueue(Caller.Of(player));
        }
    }

    private void ClearPracticeBotZoneState(int slot)
    {
        if ((uint)slot >= MaxPlayerSlots)
            return;

        Array.Clear(
            playersInsidePracticeBotZones,
            slot * PracticeBotZones.Length,
            PracticeBotZones.Length
        );
        Array.Clear(
            playersInsideQueueEntryZones,
            slot * QueueEntryZones.Length,
            QueueEntryZones.Length
        );
        nextPracticeBotZoneCheck[slot] = 0;
    }

    private void TryStartPracticeBotMatch(
        CCitadelPlayerController player,
        string? difficulty,
        Action<string> reply
    )
    {
        if (IsSpectating(player))
        {
            reply("Leave spectating before starting a bot match.");
            return;
        }

        if (TryGetActiveMatch(player, out _))
        {
            reply("You are already in a match.");
            return;
        }


        var playerPawn = player.GetHeroPawn();
        if (playerPawn is not { IsValid: true })
        {
            reply("Your hero is not available to start a bot match.");
            return;
        }

        if (server.DuelLocations.Length == 0)
        {
            reply("No duel arenas are configured.");
            return;
        }

        if (!TryGetPracticeBotProfile(difficulty, out var selectedProfile))
        {
            reply("Choose a bot difficulty: /bot easy, /bot medium, or /bot hard.");
            return;
        }

        if (pendingBotMatchPlayers.Contains(player.EntityHandle))
        {
            reply("Your bot match is already starting.");
            return;
        }

        if (!server.Queue.TryGetAvailableArena(out int arenaIndex))
        {
            reply("All duel arenas are currently in use.");
            return;
        }

        Vector3 arena = server.DuelLocations[arenaIndex];

        // A bot must be created close to the player: one created far away crashes the
        // player's game when it first receives it (and the game of anyone who joins
        // while it exists). So the player goes into the arena first, and the bot is
        // created at its own arena spot a moment later, once the player's game has them
        // there. The arena is reserved in the meantime.
        Vector3 playerSpot = GetDuelArenaSphereCenter(arena) +
            new Vector3(-aimLOCK.DUEL_SPAWN_OFFSET, 0f, 0f);
        PlayMatchTeleportEffect(playerPawn);
        playerPawn.Teleport(playerSpot, null, Vector3.Zero);
        RegisterArenaBoundaryPosition(player, playerSpot);
        reservedBotArenas.Add(arenaIndex);
        pendingBotMatchPlayers.Add(player.EntityHandle);
        uint playerHandle = player.EntityHandle;

        server.ScheduleOnce(PracticeBotSpawnDelaySeconds.Seconds(), () =>
        {
            var currentPawn = player.IsValid ? player.GetHeroPawn() : null;
            if (
                currentPawn is not { IsValid: true, IsAlive: true } ||
                server.ActiveMatches.ContainsKey(GetMatchParticipantName(player)) ||
                IsSpectating(player)
            )
            {
                ReleasePendingBotMatch(playerHandle, arenaIndex);
                return;
            }

            var botController = CreatePracticeBot(
                player,
                currentPawn,
                arena,
                selectedProfile,
                reply
            );
            if (botController == null)
            {
                ReleasePendingBotMatch(playerHandle, arenaIndex);
                return;
            }

            // Creating the bot is the heaviest frame of a bot match: the game builds the
            // bot, then loads its hero again a few frames later. The match setup (items,
            // health, Fixation for both players) waits until that hero load is done, so
            // the work lands on separate frames and is done once instead of twice.
            uint botHandle = botController.EntityHandle;
            pendingBotReservations[botHandle] = (playerHandle, arenaIndex);
            pendingBotMatchStarts[botHandle] = () =>
            {
                ReleasePendingBotMatch(playerHandle, arenaIndex);
                if (
                    !player.IsValid ||
                    !botController.IsValid ||
                    server.ActiveMatches.ContainsKey(GetMatchParticipantName(player)) ||
                    IsSpectating(player)
                )
                {
                    RemovePracticeBot(botHandle);
                    return;
                }

                // Named only now: the bot's hero reload resets its name.
                SetPracticeBotDifficultyName(botController, difficulty);
                if (!server.Queue.StartBotMatch(
                    player,
                    botController,
                    arena,
                    reply,
                    alreadyInArena: true
                ))
                {
                    RemovePracticeBot(botHandle);
                    return;
                }

                // The bot's Fixation, once the match setup is in.
                server.ScheduleOnce(0.25.Seconds(), () =>
                {
                    var botPawn = botController.IsValid ? botController.GetHeroPawn() : null;
                    if (botPawn is { IsValid: true } && botPawn.HeroID == Heroes.Haze)
                        ApplyHazeLoadout(botPawn, force: true);
                });
            };

            // Fallback in case the hero-loaded hook never fires for the bot.
            server.ScheduleOnce(
                PendingBotMatchTimeoutSeconds.Seconds(),
                () => RunPendingBotMatchStart(botHandle)
            );
        });
    }

    private const double PracticeBotSpawnDelaySeconds = 0.5;
    private const double PendingBotMatchTimeoutSeconds = 1.5;
    private const double BotMatchStartAfterHeroLoadSeconds = 0.2;
    private readonly Dictionary<uint, Action> pendingBotMatchStarts = new();
    // Bot -> (player, arena) held for its match while it is still being set up.
    private readonly Dictionary<uint, (uint PlayerHandle, int ArenaIndex)>
        pendingBotReservations = new();
    private readonly HashSet<uint> pendingBotMatchPlayers = new();
    private readonly HashSet<int> reservedBotArenas = new();

    /// <summary>Arenas held for a bot match that is still being set up.</summary>
    public bool IsArenaReserved(int arenaIndex) => reservedBotArenas.Contains(arenaIndex);

    private void ReleasePendingBotMatch(uint playerHandle, int arenaIndex)
    {
        pendingBotMatchPlayers.Remove(playerHandle);
        reservedBotArenas.Remove(arenaIndex);
    }

    /// <summary>
    /// Called from the hero-initialized hook. When it is a practice bot whose match is
    /// waiting, the match starts a moment later (on a quiet frame).
    /// </summary>
    public void OnPracticeBotHeroInitialized(CCitadelPlayerPawn pawn)
    {
        if (
            pendingBotMatchStarts.Count == 0 ||
            pawn.Controller is not { } controller ||
            !pendingBotMatchStarts.ContainsKey(controller.EntityHandle)
        )
            return;

        uint botHandle = controller.EntityHandle;
        server.ScheduleOnce(
            BotMatchStartAfterHeroLoadSeconds.Seconds(),
            () => RunPendingBotMatchStart(botHandle)
        );
    }

    private void RunPendingBotMatchStart(uint botHandle)
    {
        pendingBotReservations.Remove(botHandle);
        if (pendingBotMatchStarts.Remove(botHandle, out var start))
            start();
    }

    /// <summary>
    /// Drops a bot's pending match start (bot removed or gone before its match began)
    /// and frees the player and arena it was holding.
    /// </summary>
    private void CancelPendingBotMatch(uint botHandle)
    {
        pendingBotMatchStarts.Remove(botHandle);
        if (pendingBotReservations.Remove(botHandle, out var reservation))
            ReleasePendingBotMatch(reservation.PlayerHandle, reservation.ArenaIndex);
    }

    /* Fixation reset on kill */

    private static readonly SchemaAccessor<uint> ModifierAbilityHandle =
        new("CBaseModifier"u8, "m_hAbility"u8);

    /// <summary>
    /// Removes every modifier that came from a Fixation ability (the stacks on the
    /// person who was shot, and anything they applied). Used on every kill so each round
    /// starts from zero.
    /// </summary>
    private void RemoveFixationModifiers(CCitadelPlayerPawn pawn)
    {
        var modifiers = pawn.ModifierProp?.Modifiers;
        if (modifiers == null || modifiers.Count == 0)
            return;

        // Collected first, then removed: removing while walking the list could skip
        // the modifier that slides into the removed one's place.
        fixationModifierScratch.Clear();
        foreach (var modifier in modifiers)
        {
            if (IsFixationModifier(modifier))
                fixationModifierScratch.Add(modifier);
        }

        foreach (var modifier in fixationModifierScratch)
            pawn.RemoveModifier(modifier);

        fixationModifierScratch.Clear();
    }

    private readonly List<CBaseModifier> fixationModifierScratch = new();

    // Modifier type (VData pointer) -> is it one of Fixation's own modifiers
    // ("ability_stacking_damage/..."). Cached so the name is read once per type.
    private readonly Dictionary<nint, bool> fixationModifierTypes = new();
    private const string FixationModifierNamePrefix = "ability_stacking_damage/";

    /// <summary>
    /// A modifier is Fixation's when it came from a Fixation ability, or when it is one
    /// of Fixation's own modifier types. The second check catches stacks that have no
    /// ability attached (those were left behind before).
    /// </summary>
    private bool IsFixationModifier(CBaseModifier modifier)
    {
        uint abilityHandle = ModifierAbilityHandle.Get(modifier.Handle);
        if (
            abilityHandle != 0 &&
            abilityHandle != 0xFFFFFFFF &&
            server.IsFixationAbility(abilityHandle)
        )
            return true;

        var vdata = modifier.SubclassVData;
        if (vdata == null)
            return false;

        nint type = vdata.Handle;
        if (!fixationModifierTypes.TryGetValue(type, out bool isFixation))
        {
            if (fixationModifierTypes.Count > 2048)
                fixationModifierTypes.Clear();

            isFixation =
                vdata.Name?.StartsWith(FixationModifierNamePrefix, StringComparison.Ordinal) == true;
            fixationModifierTypes[type] = isFixation;
        }

        return isFixation;
    }

    public void ClearModifierTypeCache() => fixationModifierTypes.Clear();

    private static bool TryGetPracticeBotProfile(
        string? difficulty,
        out PracticeBotDifficultyProfile profile
    )
    {
        switch (difficulty?.Trim().ToLowerInvariant())
        {
            case "easy":
                profile = EasyBotProfile;
                return true;
            case "medium":
                profile = MediumBotProfile;
                return true;
            case "hard":
            case null:
            case "":
                profile = HardBotProfile;
                return true;
            default:
                profile = HardBotProfile;
                return false;
        }
    }

    private CCitadelPlayerController? CreatePracticeBot(
        CCitadelPlayerController owner,
        CCitadelPlayerPawn playerPawn,
        Vector3 arena,
        PracticeBotDifficultyProfile difficulty,
        Action<string> reply
    )
    {
        // The player is already in the arena by now (see TryStartPracticeBotMatch),
        // so the bot's own arena spot is close to them.
        var spawnPosition = GetDuelArenaSphereCenter(arena) +
            new Vector3(aimLOCK.DUEL_SPAWN_OFFSET, 0f, 0f);
        int botTeam = 3;

        if (practiceBots.Count == 0)
            practiceBotsPreviousAiEnabled = Bots.AiEnabled;

        if (
            practiceBots.Count == 0 &&
            practiceBotsPreviousAiEnabled &&
            !Bots.SetAiEnabled(false)
        )
        {
            reply("Could not take control of the practice bot.");
            return null;
        }

        // assignLane: false. With the default (true) the game gives the bot a lane,
        // and every lane objective on this map has been removed; the bot's lane state
        // is networked to clients, which is a candidate for the client crash on spawn.
        var bot = Bots.Add(
            Heroes.Haze,
            botTeam,
            spawnPosition,
            BotDifficulty.Medium,
            assignLane: false
        );
        // Same tick as the spawn, before the bot is ever sent to a client.
        if (bot is { IsValid: true })
            bot.SetModel(HazeModel);

        if (bot is not { IsValid: true })
        {
            RestorePracticeBotAiIfIdle();

            reply("Could not spawn a practice bot here.");
            return null;
        }

        var botController = bot.Controller;
        if (botController == null)
        {
            RestorePracticeBotAiIfIdle();
            server.ScheduleOnce(1.Seconds(), RemoveUntrackedBots);

            reply("The practice bot did not finish loading.");
            return null;
        }

        if (practiceBotStrafeSpeedOverrides.TryGetValue(
            owner.EntityHandle,
            out float strafeSpeed
        ))
        {
            difficulty = difficulty with { StrafeSpeed = strafeSpeed };
        }

        var state = new PracticeBotState
        {
            ControllerHandle = botController.EntityHandle,
            MatchKey = $"{PracticeBotMatchKeyPrefix}{botController.EntityHandle}",
            DisplayName = botController.PlayerName,
            OwnerControllerHandle = owner.EntityHandle,
            OwnerSlot = owner.Slot,
            OwnerName = owner.PlayerName,
            TargetController = owner,
            Difficulty = difficulty,
            LookAt = playerPawn.EyePosition,
        };
        practiceBots.Add(state.ControllerHandle, state);
        botViewerMasksDirty = true;
        ResetPracticeBotMovement(
            state,
            spawnPosition,
            DateTimeOffset.UtcNow
        );
        state.NextAimUpdate = DateTimeOffset.MinValue;

        return botController;
    }

    private void SetPracticeBotDifficultyName(
        CCitadelPlayerController botController,
        string? difficulty
    )
    {
        if (!practiceBots.TryGetValue(botController.EntityHandle, out var state))
            return;

        string difficultyName = difficulty?.Trim().ToLowerInvariant() switch
        {
            "easy" => "Easy",
            "medium" => "Medium",
            _ => "Hard"
        };
        state.DifficultyName = difficultyName;
        state.IsHard = string.Equals(difficultyName, "Hard", StringComparison.OrdinalIgnoreCase);
        state.DisplayName = $"Bot ({difficultyName})";
        botController.PlayerName = state.DisplayName;
        InvalidateCachedPlayerName(botController);
    }

    public void SetPracticeBotStrafeSpeed(Caller caller, float speed)
    {
        if (
            !float.IsFinite(speed) ||
            speed < MinPracticeBotStrafeSpeed ||
            speed > MaxPracticeBotStrafeSpeed
        )
        {
            caller.Reply(
                $"Choose a bot strafe speed from {MinPracticeBotStrafeSpeed:F0} to {MaxPracticeBotStrafeSpeed:F0} units per second."
            );
            return;
        }

        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to set your practice bot speed.");
            return;
        }

        practiceBotStrafeSpeedOverrides[player.EntityHandle] = speed;
        foreach (var state in practiceBots.Values.Where(state =>
            state.OwnerControllerHandle == player.EntityHandle))
        {
            state.Difficulty = state.Difficulty with { StrafeSpeed = speed };
        }
        caller.Reply($"Bot strafe speed set to {speed:F0} units per second.");
    }

    public void ConfigurePracticeBotStrafe(
        CCitadelPlayerPawn playerPawn,
        CCitadelPlayerPawn botPawn
    )
    {
        if (
            botPawn.Controller is not { } botController ||
            !practiceBots.TryGetValue(botController.EntityHandle, out var state) ||
            playerPawn.Controller?.EntityHandle != state.OwnerControllerHandle
        )
            return;

        state.TargetController =
            playerPawn.Controller as CCitadelPlayerController;
        state.LookAt = playerPawn.EyePosition;
        state.AimError = Vector3.Zero;
        ResetPracticeBotMovement(
            state,
            botPawn.Position,
            DateTimeOffset.UtcNow
        );
        state.NextAimUpdate = DateTimeOffset.MinValue;
    }

    private void ResetPracticeBotMovement(
        PracticeBotState state,
        Vector3 botPosition,
        DateTimeOffset now
    )
    {
        state.StrafeTarget = botPosition;
        if (
            server.random.NextDouble() <
            state.Difficulty.DirectionChangeChance
        )
            state.StrafeDirection *= -1;
        state.NextMovementAdjustment = now;
    }

    public void OnDuelMatchFinished(DuelMatch match)
    {
        server.Queue.OnMatchFinished(match);
        var bot = practiceBots.Values.FirstOrDefault(state =>
            string.Equals(
                match.Player1,
                state.MatchKey,
                StringComparison.OrdinalIgnoreCase
            ) ||
            string.Equals(
                match.Player2,
                state.MatchKey,
                StringComparison.OrdinalIgnoreCase
            )
        );
        if (bot != null)
            RemovePracticeBot(bot.ControllerHandle);

        ResetArenaScoreboardWhenParticipantsLeave(match);
    }

    public void ShowArenaScoreboard(DuelMatch match)
    {
        int arenaIndex = match.ArenaIndex;
        if (
            arenaIndex < 0 ||
            arenaIndex >= server.DuelLocations.Length ||
            server.DuelLocations[arenaIndex] != match.ArenaPosition
        )
        {
            Console.WriteLine(
                $"[aimLOCK] Cannot show scoreboard for invalid duel arena {arenaIndex}."
            );
            return;
        }

        if (
            arenaScoreboards.TryGetValue(arenaIndex, out var existing) &&
            ReferenceEquals(existing.Match, match)
        )
        {
            UpdateArenaScoreboard(match);
            return;
        }

        RemoveArenaScoreboard(arenaIndex);

        string[] lines = BuildArenaScoreboardLines(match);
        int longestLine = lines.Max(line => line.Length);
        int backgroundGlyphCount = Math.Max(
            1,
            (int)Math.Ceiling(longestLine / 2d) +
                ArenaScoreboardBackgroundPadding
        );
        int backgroundLineCount = lines.Length;
        string backgroundLine = new(
            ArenaScoreboardBackgroundGlyph,
            backgroundGlyphCount
        );
        string backgroundMessage = string.Join(
            "\n",
            Enumerable.Repeat(backgroundLine, backgroundLineCount)
        );
        Vector3 position = GetDuelArenaSphereCenter(match.ArenaPosition) +
            new Vector3(
                0f,
                0f,
                DuelArenaCourtHeight + ArenaScoreboardHeightAboveCourt
            );

        var background = CPointWorldText.Create(
            backgroundMessage,
            position,
            fontSize: ArenaScoreboardFontSize,
            worldUnitsPerPx: ArenaScoreboardWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1
        );
        if (background == null)
        {
            Console.WriteLine(
                $"[aimLOCK] Failed to create the scoreboard background for duel arena {arenaIndex + 1}."
            );
            return;
        }

        background.ColorABGR =
            (uint)ArenaScoreboardBackgroundAlpha << 24;
        background.Fullbright = true;
        background.DepthOffset = ArenaScoreboardBackgroundDepthOffset;
        background.JustifyHorizontal = HorizontalJustify.Center;
        background.Teleport(angles: ArenaScoreboardAngles);

        var text = CPointWorldText.Create(
            string.Join("\n", lines),
            position,
            fontSize: ArenaScoreboardFontSize,
            worldUnitsPerPx: ArenaScoreboardWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1
        );
        if (text == null)
        {
            background.Remove();
            Console.WriteLine(
                $"[aimLOCK] Failed to create the scoreboard for duel arena {arenaIndex + 1}."
            );
            return;
        }

        text.ColorABGR = 0xFFFFFFFF;
        text.Fullbright = true;
        text.JustifyHorizontal = HorizontalJustify.Center;
        text.Teleport(angles: ArenaScoreboardAngles);
        arenaScoreboards[arenaIndex] = (match, background, text);
    }

    public void UpdateArenaScoreboard(DuelMatch match)
    {
        if (
            !arenaScoreboards.TryGetValue(match.ArenaIndex, out var scoreboard) ||
            !ReferenceEquals(scoreboard.Match, match)
        )
            return;

        string[] lines = BuildArenaScoreboardLines(match);
        if (
            scoreboard.Text is { IsValid: true } &&
            scoreboard.Background is { IsValid: true }
        )
        {
            scoreboard.Text.SetMessage(string.Join("\n", lines));
            return;
        }

        RemoveArenaScoreboard(match.ArenaIndex);
        ShowArenaScoreboard(match);
    }

    public void RefreshArenaScoreboards()
    {
        DuelMatch[] activeMatches = arenaScoreboards.Values
            .Select(scoreboard => scoreboard.Match)
            .ToArray();
        foreach (DuelMatch match in activeMatches)
            UpdateArenaScoreboard(match);
    }

    public void RemoveArenaScoreboards()
    {
        foreach (int arenaIndex in arenaScoreboards.Keys.ToArray())
            RemoveArenaScoreboard(arenaIndex);
    }

    private string[] BuildArenaScoreboardLines(DuelMatch match)
    {
        return new[]
        {
            GetParticipantDisplayName(match.Player1),
            $"{match.Score1}  -  {match.Score2}",
            GetParticipantDisplayName(match.Player2)
        };
    }

    private string GetParticipantDisplayName(string participantName)
    {
        var participant = FindMatchParticipant(participantName);
        return participant is { IsBot: true }
            ? GetPracticeBotDisplayName(participant) ?? participant.PlayerName
            : participant == null
                ? GetOfflineParticipantName(participantName)
                : server.GuildManager.FormatPlayerNameWithSelectedTag(participant);
    }

    private void ResetArenaScoreboardWhenParticipantsLeave(DuelMatch match)
    {
        if (
            !arenaScoreboards.TryGetValue(match.ArenaIndex, out var scoreboard) ||
            !ReferenceEquals(scoreboard.Match, match)
        )
            return;

        var onlinePlayers = Players.GetAll().ToArray();
        bool player1InArena = IsMatchParticipantInArena(
            match.Player1,
            match.ArenaPosition,
            onlinePlayers
        );
        bool player2InArena = IsMatchParticipantInArena(
            match.Player2,
            match.ArenaPosition,
            onlinePlayers
        );
        if (!player1InArena && !player2InArena)
        {
            RemoveArenaScoreboard(match.ArenaIndex);
            return;
        }

        server.ScheduleOnce(0.25.Seconds(), () =>
            ResetArenaScoreboardWhenParticipantsLeave(match));
    }

    private bool IsMatchParticipantInArena(
        string participantName,
        Vector3 arenaPosition,
        IEnumerable<CCitadelPlayerController> onlinePlayers
    )
    {
        var participant = FindMatchParticipant(
            participantName,
            onlinePlayers
        );
        var pawn = participant?.GetHeroPawn();
        return pawn is { IsValid: true } &&
            Vector3.DistanceSquared(
                pawn.Position,
                GetDuelArenaSphereCenter(arenaPosition)
            ) <= DuelArenaOuterRadiusSquared;
    }

    private void RemoveArenaScoreboard(int arenaIndex)
    {
        if (!arenaScoreboards.Remove(arenaIndex, out var scoreboard))
            return;

        if (scoreboard.Text is { IsValid: true })
            scoreboard.Text.Remove();
        if (scoreboard.Background is { IsValid: true })
            scoreboard.Background.Remove();
    }

    public bool IsPracticeBotController(CBasePlayerController? controller) =>
        controller != null &&
        practiceBots.Count != 0 &&
        practiceBots.ContainsKey(controller.EntityHandle);

    public bool IsPracticeBot(CBaseEntity entity) =>
        entity is CCitadelPlayerPawn pawn &&
        pawn.Controller is { } controller &&
        practiceBots.ContainsKey(controller.EntityHandle);

    /// <summary>
    /// The controller's player name, cached per slot for up to a second. Keyed by entity
    /// handle too, so a new player taking over a slot never sees the previous name.
    /// </summary>
    public string GetCachedPlayerName(CBasePlayerController player)
    {
        int slot = player.Slot;
        if ((uint)slot >= MaxPlayerSlots)
            return player.PlayerName;

        long now = Environment.TickCount64;
        uint handle = player.EntityHandle;
        if (
            cachedPlayerNames[slot] is { } cachedName &&
            cachedPlayerNameHandles[slot] == handle &&
            now < cachedPlayerNameExpiry[slot]
        )
            return cachedName;

        string name = player.PlayerName;
        cachedPlayerNames[slot] = name;
        cachedPlayerNameHandles[slot] = handle;
        cachedPlayerNameExpiry[slot] = now + PlayerNameCacheLifetimeMs;
        return name;
    }

    public void InvalidateCachedPlayerName(CBasePlayerController player)
    {
        int slot = player.Slot;
        if ((uint)slot < MaxPlayerSlots)
            cachedPlayerNames[slot] = null;
    }

    /// <summary>
    /// ChangeTeam only when the team actually differs. A team change re-networks the
    /// player and their hero; doing it when nothing changes (every /spawn, match end)
    /// was pure overhead on each teleport.
    /// </summary>
    private const string DisarmedModifierName = "modifier_citadel_disarmed";

    public static void RemoveAllDisarms(CCitadelPlayerPawn pawn)
    {
        for (int i = 0; i < 8 && pawn.RemoveModifier(DisarmedModifierName); i++)
        {
        }
    }

    public static void EnsureTeam(CCitadelPlayerController? controller, int team)
    {
        if (controller is { IsValid: true } && controller.TeamNum != team)
            controller.ChangeTeam(team);
    }

    public bool TryGetActiveMatch(
        CBasePlayerController player,
        out DuelMatch match
    )
    {
        // Called from the usercmd, damage and HUD paths: no work when nothing is
        // running, and the cached name instead of a fresh native string read.
        if (server.ActiveMatches.Count == 0)
        {
            match = null!;
            return false;
        }

        if (
            practiceBots.Count != 0 &&
            practiceBots.TryGetValue(player.EntityHandle, out var bot) &&
            server.ActiveMatches.TryGetValue(bot.MatchKey, out match!)
        )
            return true;

        if (server.ActiveMatches.TryGetValue(GetMatchParticipantName(player), out match!))
            return true;

        match = null!;
        return false;
    }

    public bool IsBotMatch(DuelMatch match) =>
        FindMatchParticipant(match.Player1)?.IsBot == true ||
        FindMatchParticipant(match.Player2)?.IsBot == true;

    public bool IsSpectating(CCitadelPlayerController player) =>
        spectatingMatches.ContainsKey(player.PlayerSteamId);

    public void SpectateCommand(Caller caller, params string[] arguments)
    {
        var matches = server.ActiveMatches.Values
            .Distinct()
            .Where(match => !match.Finished)
            .ToArray();

        if (
            arguments.Length == 1 &&
            string.Equals(arguments[0], "list", StringComparison.OrdinalIgnoreCase)
        )
        {
            if (matches.Length == 0)
            {
                caller.Reply("There are no active matches.");
                return;
            }

            foreach (var match in matches)
            {
                string state = match.ArenaInitialized
                    ? ""
                    : " | initializing";
                caller.Reply(
                    $"{GetMatchParticipantDisplayName(match.Player1)} vs " +
                    $"{GetMatchParticipantDisplayName(match.Player2)} | " +
                    $"{match.Score1}-{match.Score2}{state}"
                );
            }
            return;
        }

        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to spectate a match.");
            return;
        }

        if (arguments.Length == 0)
        {
            caller.Reply("Use /spectate list or /spectate <player name>.");
            return;
        }

        if (TryGetActiveMatch(player, out _))
        {
            caller.Reply("You cannot spectate while you are in a match.");
            return;
        }

        if (
            server.RankedQueue.Concat(server.UnrankedQueue).Any(entry =>
                entry.SteamId == player.PlayerSteamId
            )
        )
        {
            caller.Reply("Leave your queue before spectating a match.");
            return;
        }

        string targetName = string.Join(" ", arguments).Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            caller.Reply("Use /spectate list or /spectate <player name>.");
            return;
        }

        bool MatchesParticipantExactly(string matchKey) =>
            string.Equals(
                matchKey,
                targetName,
                StringComparison.OrdinalIgnoreCase
            ) ||
            string.Equals(
                GetMatchParticipantDisplayName(matchKey),
                targetName,
                StringComparison.OrdinalIgnoreCase
            );
        bool MatchesParticipantPrefix(string matchKey)
        {
            string displayName = GetMatchParticipantDisplayName(matchKey);
            return matchKey.StartsWith(
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                displayName.StartsWith(
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                );
        }

        var exactMatches = matches
            .Where(match =>
                MatchesParticipantExactly(match.Player1) ||
                MatchesParticipantExactly(match.Player2)
            )
            .ToArray();
        var matchingMatches = exactMatches.Length > 0
            ? exactMatches
            : matches
                .Where(match =>
                    MatchesParticipantPrefix(match.Player1) ||
                    MatchesParticipantPrefix(match.Player2)
                )
                .ToArray();

        if (matchingMatches.Length == 0)
        {
            caller.Reply(
                $"No active match contains a player matching \"{targetName}\". " +
                "Use /spectate list to see active matches."
            );
            return;
        }

        if (matchingMatches.Length > 1)
        {
            caller.Reply(
                "That name matches multiple active matches; use a full player name."
            );
            return;
        }

        var matchToSpectate = matchingMatches[0];
        if (!matchToSpectate.ArenaInitialized)
        {
            caller.Reply("That match is still initializing; try again shortly.");
            return;
        }

        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            caller.Reply("Your hero is not available to spectate.");
            return;
        }

        var (position, angles) = GetMatchSpectatorView(matchToSpectate);
        pawn.Teleport(position, angles, null);
        spectatingMatches[player.PlayerSteamId] = matchToSpectate;
        botViewerMasksDirty = true;
        pawn.AddModifier("modifier_citadel_disarmed");
        caller.Reply(
            $"Spectating {GetMatchParticipantDisplayName(matchToSpectate.Player1)} " +
            $"vs {GetMatchParticipantDisplayName(matchToSpectate.Player2)}."
        );
    }

    public void StopSpectating(CCitadelPlayerController player)
    {
        spectatingMatches.Remove(player.PlayerSteamId);
        botViewerMasksDirty = true;
        if (player.GetHeroPawn() is { } disarmedPawn)
            RemoveAllDisarms(disarmedPawn);
    }

    private string GetMatchParticipantDisplayName(string participantName)
    {
        var participant = FindMatchParticipant(participantName);
        if (participant == null)
            return GetOfflineParticipantName(participantName);

        return GetPracticeBotDisplayName(participant) ??
            server.GuildManager.FormatPlayerNameWithSelectedTag(participant);
    }

    private (Vector3 Position, Vector3 Angles) GetMatchSpectatorView(
        DuelMatch match
    )
    {
        Vector3 center = GetDuelArenaSphereCenter(match.ArenaPosition);
        float radius = DuelArenaSphereRadius;
        Vector3 position = center + new Vector3(0f, -radius - 128f, 180f);
        Vector3 direction = center - position;
        float horizontalDistance = MathF.Sqrt(
            direction.X * direction.X + direction.Y * direction.Y
        );
        float pitch = MathF.Atan2(
            -direction.Z,
            horizontalDistance
        ) * (180f / MathF.PI);
        float yaw = MathF.Atan2(
            direction.Y,
            direction.X
        ) * (180f / MathF.PI);

        return (position, new Vector3(pitch, yaw, 0f));
    }

    public void ReturnMatchSpectators(DuelMatch match)
    {
        ulong[] spectatorIds = spectatingMatches
            .Where(entry => ReferenceEquals(entry.Value, match))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (ulong steamId in spectatorIds)
        {
            spectatingMatches.Remove(steamId);
            botViewerMasksDirty = true;
            ReturnSpectatorToSpawn(steamId);
        }
    }

    private void ReturnSpectatorToSpawn(
        ulong steamId,
        int retriesRemaining = 20,
        CCitadelPlayerController? cachedPlayer = null
    )
    {
        var player = cachedPlayer is { IsValid: true } &&
            cachedPlayer.PlayerSteamId == steamId
                ? cachedPlayer
                : Players.GetAll().FirstOrDefault(controller =>
                    controller.PlayerSteamId == steamId
                );
        if (player is not { IsValid: true })
            return;

        if (player.GetHeroPawn() is { } disarmedPawn)
            RemoveAllDisarms(disarmedPawn);
        EnsureTeam(player, 2);
        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            if (retriesRemaining <= 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not return spectator {player.PlayerName} to spawn."
                );
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                ReturnSpectatorToSpawn(
                    steamId,
                    retriesRemaining - 1,
                    player
                ));
            return;
        }

        pawn.Teleport(aimLOCK.MidtownSpawnPosition, null, null);
        PlayMatchTeleportEffect(pawn);
        server.UIManager.RestoreServerInfoAfterSpawn(player);
        Chat.PrintToChat(player, "The match ended; you have been returned to spawn.");
    }

    public string GetMatchParticipantName(CBasePlayerController player) =>
        practiceBots.TryGetValue(player.EntityHandle, out var bot)
            ? bot.MatchKey
            : (
                player as CCitadelPlayerController ??
                Players.GetAll().FirstOrDefault(candidate =>
                    candidate.EntityHandle == player.EntityHandle
                )
            ) is { } controller
                ? controller.PlayerSteamId.ToString(CultureInfo.InvariantCulture)
                : player.PlayerName;

    public string? GetPracticeBotDisplayName(
        CBasePlayerController player
    ) =>
        practiceBots.TryGetValue(player.EntityHandle, out var bot)
            ? bot.DisplayName
            : null;

    public string GetPracticeBotDifficultyName(
        CCitadelPlayerController bot
    ) =>
        practiceBots.TryGetValue(bot.EntityHandle, out var state)
            ? state.DifficultyName
            : "Hard";

    public CCitadelPlayerController? FindMatchParticipant(
        string participantName
    )
    {
        return FindMatchParticipant(participantName, Players.GetAll());
    }

    private const string PracticeBotMatchKeyPrefix = "__aimlock_bot_";

    private CCitadelPlayerController? FindMatchParticipant(
        string participantName,
        IEnumerable<CCitadelPlayerController> players
    )
    {
        // Called many times per round (HUD, countdown, respawns, damage checks):
        // one pass, no LINQ, no native string read per player.
        bool hasSteamId = ulong.TryParse(
            participantName,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ulong steamId
        );
        uint botHandle = 0;
        if (
            practiceBots.Count != 0 &&
            participantName.StartsWith(PracticeBotMatchKeyPrefix, StringComparison.Ordinal)
        )
        {
            foreach (var state in practiceBots.Values)
            {
                if (string.Equals(participantName, state.MatchKey, StringComparison.OrdinalIgnoreCase))
                {
                    botHandle = state.ControllerHandle;
                    break;
                }
            }
        }

        foreach (var player in players)
        {
            if (botHandle != 0)
            {
                if (player.EntityHandle == botHandle)
                    return player;
                continue;
            }

            if (!player.IsBot && hasSteamId && player.PlayerSteamId == steamId)
                return player;
        }

        return null;
    }

    private string GetOfflineParticipantName(string participantName) =>
        ulong.TryParse(
            participantName,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ulong steamId
        ) &&
        server.PlayerRecords.TryGetValue(steamId, out var record) &&
        !string.IsNullOrWhiteSpace(record.LastKnownName)
            ? record.LastKnownName
            : participantName;

    private IEnumerable<CCitadelPlayerController> GetMatchParticipants(
        DuelMatch match
    )
    {
        var onlinePlayers = Players.GetAll().ToArray();
        var player1 = FindMatchParticipant(match.Player1, onlinePlayers);
        var player2 = FindMatchParticipant(match.Player2, onlinePlayers);

        if (player1 != null)
            yield return player1;
        if (
            player2 != null &&
            (player1 == null || player2.EntityHandle != player1.EntityHandle)
        )
            yield return player2;
    }

    public bool BlockPracticeBotDamage(TakeDamageEvent args)
    {
        if (IsPracticeBot(args.Entity))
            return true;

        var attackerPawn = args.Info.Attacker as CCitadelPlayerPawn;
        return attackerPawn?.Controller is { } attacker &&
            IsSpectating(attacker);
    }


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

    public void RestoreDuelBulletLifesteal(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch
    )
    {
        pawn.RemoveModifier("upgrade_vampire/modifier_intrinsic_base");
        if (!hadItemBeforeMatch)
            return;

        pawn.AddModifier(
            "upgrade_vampire/modifier_intrinsic_base",
            abilityValues: new()
            {
                ["BulletLifestealPercent"] = DefaultBulletLifestealPercent,
            }
        );
    }

    // Swift Striker at its normal value (20% fire rate) in duels: a changed value only
    // exists on the server, so players' games predicted shots at a different fire
    // rate than the server fired them.
    public void ApplyDuelBlitzBullets(CCitadelPlayerPawn pawn)
    {
        if (
            pawn.AbilityComponent?.FindAbilityByName("upgrade_blitz_bullets")
            == null
        )
        {
            pawn.AddItem("upgrade_blitz_bullets");
        }
    }

    public void RestoreDuelBlitzBullets(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch
    )
    {
        if (!hadItemBeforeMatch)
            pawn.RemoveItem("upgrade_blitz_bullets");
    }

    public void RemoveDuelItemIfGranted(
        CCitadelPlayerPawn pawn,
        string itemName,
        bool hadItemBeforeMatch
    )
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
                pawn.AddItem(itemName);
        }

        ApplyDuelHighVelocityRounds(pawn);
        ApplyDuelBlitzBullets(pawn);
        ApplyPermanentBurstFire(pawn);
    }

    private static void ApplyDuelHighVelocityRounds(CCitadelPlayerPawn pawn)
    {
        const string itemName = "upgrade_high_velocity_mag";
        const string modifierName = $"{itemName}/modifier_intrinsic_base";
        if (pawn.AbilityComponent?.FindAbilityByName(itemName) == null)
        {
            Console.WriteLine(
                "[aimLOCK] Cannot set duel bullet speed; High-Velocity Mag is missing."
            );
            return;
        }

        pawn.RemoveModifier(modifierName);
        pawn.AddModifier(
            modifierName,
            abilityValues: new()
            {
                ["BonusBulletSpeedPercent"] = DuelHighVelocityRoundsBulletSpeedPercent,
            }
        );
    }

    public void RestoreDuelHighVelocityRounds(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch
    )
    {
        const string itemName = "upgrade_high_velocity_mag";
        pawn.RemoveModifier($"{itemName}/modifier_intrinsic_base");
        if (hadItemBeforeMatch)
            pawn.AddModifier($"{itemName}/modifier_intrinsic_base");
        else
            pawn.RemoveItem(itemName);
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
            return;

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

    private static void RestoreDuelItemHealthRegen(
        CCitadelPlayerPawn pawn,
        string itemName,
        bool hadItemBeforeMatch
    )
    {
        string modifierName = $"{itemName}/modifier_intrinsic_base";
        pawn.RemoveModifier(modifierName);
        if (hadItemBeforeMatch)
            pawn.AddModifier(modifierName);
    }

    private void ProcessPracticeBotUsercmds(
        ProcessUsercmdsEvent args,
        CBasePlayerController controller,
        CCitadelPlayerPawn pawn,
        DuelMatch? practiceBotMatch
    )
    {
        if (
            !controller.IsBot ||
            !practiceBots.TryGetValue(controller.EntityHandle, out var state)
        )
        {
            return;
        }

        var target = state.TargetController;
        if (
            target is not { IsValid: true } ||
            target.EntityHandle != state.OwnerControllerHandle
        )
        {
            target = Players.GetAll().FirstOrDefault(player =>
                !player.IsBot &&
                player.EntityHandle == state.OwnerControllerHandle
            );
            state.TargetController = target;
        }

        var targetPawn = target?.GetHeroPawn();
        if (targetPawn is not { IsValid: true })
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        UpdatePracticeBotAim(state, now, targetPawn);
        var moveTo = UpdatePracticeBotDuelMovement(
            state,
            pawn,
            targetPawn,
            now,
            practiceBotMatch
        );
        bool canAttack =
            practiceBotMatch is
            {
                Finished: false,
                ArenaInitialized: true,
                CountdownActive: false,
                CombatActive: true
            };

        foreach (var usercmd in args.Usercmds)
        {
            Bots.SetMoveInput(
                usercmd,
                pawn,
                moveTo,
                state.LookAt,
                jump: false
            );

            var baseCommand = usercmd.Base ??= new CBaseUserCmdPB();
            var buttons = baseCommand.ButtonsPb ??= new CInButtonStatePB();
            if (canAttack)
            {
                buttons.Buttonstate1 |= (ulong)InputButton.Attack;
                buttons.Buttonstate2 |= (ulong)InputButton.Attack;
            }
            else
            {
                buttons.Buttonstate1 &= ~(ulong)InputButton.Attack;
                buttons.Buttonstate2 &= ~(ulong)InputButton.Attack;
            }
        }
    }

    private DuelMatch? EnforceDuelArenaBounds(
        CBasePlayerController controller,
        CCitadelPlayerPawn pawn
    )
    {
        if (TryGetActiveMatch(controller, out var match))
        {
            if (match.Finished || !match.ArenaInitialized)
                return match;

            EnforceSphereBoundary(
                pawn,
                match.ArenaPosition,
                isInside: true,
                DuelArenaSphereRadius
            );
            return match;
        }

        ulong steamId = controller.PlayerSteamId;
        int slot = controller.Slot;
        bool hasSlot = (uint)slot < MaxPlayerSlots;
        bool hasBoundaryState =
            duelArenaBoundaryStates.TryGetValue(steamId, out var boundaryState);
        long now = Environment.TickCount64;
        if (!hasBoundaryState && hasSlot && now < nextFarArenaCheck[slot])
            return null;

        Vector3 position = pawn.Position;
        if (!hasBoundaryState)
        {
            int nearestArenaIndex = -1;
            float nearestDistanceSquared = float.MaxValue;
            for (int i = 0; i < server.DuelLocations.Length; i++)
            {
                Vector3 offset = position - duelArenaSphereCenters[i];
                if (
                    MathF.Abs(offset.X) > FarArenaRadius ||
                    MathF.Abs(offset.Y) > FarArenaRadius ||
                    MathF.Abs(offset.Z) > FarArenaRadius
                )
                    continue;

                float distanceSquared = offset.LengthSquared();
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestArenaIndex = i;
                    nearestDistanceSquared = distanceSquared;
                }
            }

            if (
                nearestArenaIndex < 0 ||
                nearestDistanceSquared > DuelArenaOuterRadiusSquared
            )
            {
                // Well clear of every arena: nobody can reach a wall within the next
                // interval, so skip the arena search until then.
                if (
                    hasSlot &&
                    (nearestArenaIndex < 0 || nearestDistanceSquared > FarArenaRadiusSquared)
                )
                    nextFarArenaCheck[slot] = now + FarArenaCheckIntervalMs;
                return null;
            }

            float interiorRadius =
                DuelArenaSphereRadius - DuelArenaPlayerMargin;
            boundaryState = (
                nearestArenaIndex,
                nearestDistanceSquared <= interiorRadius * interiorRadius
            );
            duelArenaBoundaryStates[steamId] = boundaryState;
        }
        else if (
            Vector3.DistanceSquared(
                position,
                duelArenaSphereCenters[boundaryState.ArenaIndex]
            ) > DuelArenaOuterRadiusSquared
        )
        {
            duelArenaBoundaryStates.Remove(steamId);
            return null;
        }

        EnforceSphereBoundary(
            pawn,
            server.DuelLocations[boundaryState.ArenaIndex],
            boundaryState.IsInside,
            DuelArenaSphereRadius
        );
        return null;
    }

    public void RegisterArenaBoundaryPosition(
        CCitadelPlayerController player,
        Vector3 position
    )
    {
        int nearestArenaIndex = -1;
        float nearestDistanceSquared = float.MaxValue;
        for (int i = 0; i < server.DuelLocations.Length; i++)
        {
            float distanceSquared = Vector3.DistanceSquared(
                position,
                GetDuelArenaSphereCenter(server.DuelLocations[i])
            );
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestArenaIndex = i;
                nearestDistanceSquared = distanceSquared;
            }
        }

        float arenaSphereRadius = DuelArenaSphereRadius;
        float detectionRadius = arenaSphereRadius + DuelArenaPlayerMargin;
        if (
            nearestArenaIndex < 0 ||
            nearestDistanceSquared > detectionRadius * detectionRadius
        )
        {
            duelArenaBoundaryStates.Remove(player.PlayerSteamId);
            return;
        }

        float interiorRadius = arenaSphereRadius - DuelArenaPlayerMargin;
        duelArenaBoundaryStates[player.PlayerSteamId] = (
            nearestArenaIndex,
            nearestDistanceSquared <= interiorRadius * interiorRadius
        );
    }

    private static void EnforceSphereBoundary(
        CCitadelPlayerPawn pawn,
        Vector3 arenaPosition,
        bool isInside,
        float sphereRadius
    )
    {
        Vector3 sphereCenter = GetDuelArenaSphereCenter(arenaPosition);
        Vector3 offset = pawn.Position - sphereCenter;
        float distanceSquared = offset.LengthSquared();
        float boundaryRadius = isInside
            ? sphereRadius - DuelArenaPlayerMargin
            : sphereRadius;
        if (
            isInside
                ? distanceSquared <= boundaryRadius * boundaryRadius
                : distanceSquared >= boundaryRadius * boundaryRadius
        )
            return;

        float distance = MathF.Sqrt(distanceSquared);
        Vector3 outward = distance > 0f ? offset / distance : Vector3.UnitX;
        // Direction that leads back into the allowed area.
        Vector3 allowedDirection = isInside ? -outward : outward;
        float penetration = isInside
            ? distance - boundaryRadius
            : boundaryRadius - distance;

        Vector3 velocity = pawn.AbsVelocity;
        float speedTowardAllowed = Vector3.Dot(velocity, allowedDirection);

        if (penetration > BoundaryHardCorrectionDistance)
        {
            // Escaped far past the wall (dash, knockback, teleport): snap back once.
            if (speedTowardAllowed < 0f)
                velocity -= allowedDirection * speedTowardAllowed;

            pawn.Teleport(
                sphereCenter + outward * boundaryRadius,
                null,
                velocity
            );
            return;
        }

        // Soft wall: cancel movement through the boundary and push back in, changing
        // velocity only. Teleporting the position here (the old behaviour) ran on every
        // usercmd batch while a player strafed against the wall, which broke
        // interpolation and lag-compensation history for that player and caused
        // rubber-banding and missed shots.
        float pushBackSpeed = Math.Clamp(
            penetration * 8f,
            BoundaryMinPushBackSpeed,
            BoundaryMaxPushBackSpeed
        );
        if (speedTowardAllowed >= pushBackSpeed)
            return;

        velocity += allowedDirection * (pushBackSpeed - speedTowardAllowed);
        pawn.Teleport(null, null, velocity);
    }

    public void CreateDuelArenaRadiusMarkers()
    {
        int expectedBeamCount =
            server.DuelLocations.Length * DuelArenaCircleSegments;
        if (duelArenaRadiusBeams.Count(beam => beam.IsValid) == expectedBeamCount)
            return;

        CBeam.RemoveAll(duelArenaRadiusBeams);

        for (int arenaIndex = 0; arenaIndex < server.DuelLocations.Length; arenaIndex++)
        {
            Vector3 arena = server.DuelLocations[arenaIndex];
            Vector3 sphereCenter = GetDuelArenaSphereCenter(arena);
            float radius = DuelArenaSphereRadius - DuelArenaPlayerMargin;
            var points = new Vector3[DuelArenaCircleSegments];
            for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
            {
                float angle =
                    pointIndex * (2f * MathF.PI / DuelArenaCircleSegments);
                points[pointIndex] = sphereCenter + new Vector3(
                    MathF.Cos(angle) * radius,
                    MathF.Sin(angle) * radius,
                    0f
                );
            }

            var circleBeams = CBeam.CreatePolyline(
                points,
                DuelArenaRadiusMarkerWidth,
                DuelArenaCircleColor,
                closed: true
            );
            if (circleBeams.Count != DuelArenaCircleSegments)
            {
                CBeam.RemoveAll(circleBeams);
                CBeam.RemoveAll(duelArenaRadiusBeams);
                Console.WriteLine("[aimLOCK] Failed to create a complete duel arena boundary circle.");
                return;
            }

            duelArenaRadiusBeams.AddRange(circleBeams);
        }

        Console.WriteLine($"[aimLOCK] Created {server.DuelLocations.Length} translucent duel arena boundary circles.");
    }

    public void RemoveDuelArenaRadiusMarkers() =>
        CBeam.RemoveAll(duelArenaRadiusBeams);

    public void CreateEntryZoneLabels()
    {
        // One pass over the map for all zones instead of one full scan per zone.
        foreach (var entity in Entities.All)
        {
            if (!entity.Is<CPointWorldText>())
                continue;

            Vector3 entityPosition = entity.Position;
            bool isExistingZoneLabel = false;
            foreach (Vector3 removedLabelPosition in RemovedBotsLabelPositions)
            {
                if (
                    Vector3.DistanceSquared(entityPosition, removedLabelPosition) <=
                    PracticeBotDoorwayLabelNearbyDistanceSquared
                )
                {
                    isExistingZoneLabel = true;
                    break;
                }
            }

            foreach (var zone in PracticeBotZones)
            {
                if (isExistingZoneLabel)
                    break;

                if (
                    Vector3.DistanceSquared(entityPosition, zone.LabelPosition) <=
                        PracticeBotDoorwayLabelNearbyDistanceSquared
                )
                {
                    isExistingZoneLabel = true;
                    break;
                }
            }

            if (!isExistingZoneLabel)
            {
                foreach (var zone in QueueEntryZones)
                {
                    if (
                        Vector3.DistanceSquared(entityPosition, zone.LabelPosition) <=
                            PracticeBotDoorwayLabelNearbyDistanceSquared
                        || (
                            zone.QueueType == MatchQueueType.Ranked &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(30f, 0f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                        || (
                            zone.QueueType == MatchQueueType.Ranked &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(55f, 0f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                        || (
                            zone.QueueType == null &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(-100f, 0f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                        || (
                            zone.QueueType == null &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(0f, -25f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                        || (
                            zone.QueueType == null &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(25f, 0f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                    )
                    {
                        isExistingZoneLabel = true;
                        break;
                    }
                }
            }

            if (isExistingZoneLabel)
                entity.Remove();
        }

        foreach (var zone in PracticeBotZones)
            CreateZoneLabel(zone.Label, zone.LabelColorABGR, zone.LabelPosition);
        foreach (var zone in QueueEntryZones)
        {
            CreateZoneLabel(
                zone.Label,
                zone.LabelColorABGR,
                zone.LabelPosition,
                QueueEntryLabelHorizontalPadding
            );
        }
    }

    private void CreateZoneLabel(
        string label,
        uint labelColorABGR,
        Vector3 labelPosition,
        float horizontalPadding = PracticeBotDoorwayLabelHorizontalPadding
    )
    {
        float characterWidth =
            PracticeBotDoorwayLabelFontSize *
            PracticeBotDoorwayLabelWorldUnitsPerPx *
            0.41f;
        float glyphWidth =
            PracticeBotDoorwayLabelFontSize *
            PracticeBotDoorwayLabelWorldUnitsPerPx;
        int glyphCount = Math.Max(
            1,
            (int)Math.Ceiling(
                (
                    label.Length * characterWidth +
                    horizontalPadding
                ) / glyphWidth
            )
        );
        string backgroundMessage = new(
            PracticeBotDoorwayLabelBackgroundGlyph,
            glyphCount
        );

        var backgroundText = CPointWorldText.Create(
            backgroundMessage,
            labelPosition,
            fontSize: PracticeBotDoorwayLabelFontSize,
            worldUnitsPerPx: PracticeBotDoorwayLabelWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1
        );
        if (backgroundText == null)
        {
            Console.WriteLine(
                $"[aimLOCK] Failed to create the {label} zone label background."
            );
            return;
        }

        backgroundText.ColorABGR =
            (uint)PracticeBotDoorwayLabelBackgroundAlpha << 24;
        backgroundText.Fullbright = true;
        backgroundText.DepthOffset =
            PracticeBotDoorwayLabelBackgroundDepthOffset;
        backgroundText.JustifyHorizontal = HorizontalJustify.Center;
        backgroundText.Teleport(angles: PracticeBotDoorwayLabelAngles);
        entryZoneLabelEntities.Add(backgroundText);

        var labelText = CPointWorldText.Create(
            label,
            labelPosition,
            fontSize: PracticeBotDoorwayLabelFontSize,
            worldUnitsPerPx: PracticeBotDoorwayLabelWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1
        );
        if (labelText == null)
        {
            backgroundText.Remove();
            entryZoneLabelEntities.Remove(backgroundText);
            Console.WriteLine(
                $"[aimLOCK] Failed to create the {label} zone label."
            );
            return;
        }

        labelText.ColorABGR = labelColorABGR;
        labelText.Fullbright = true;
        labelText.JustifyHorizontal = HorizontalJustify.Center;
        labelText.Teleport(angles: PracticeBotDoorwayLabelAngles);
        entryZoneLabelEntities.Add(labelText);
    }

    public void RemoveEntryZoneLabels()
    {
        foreach (var labelEntity in entryZoneLabelEntities)
        {
            if (labelEntity is { IsValid: true })
                labelEntity.Remove();
        }

        entryZoneLabelEntities.Clear();
    }

    private static Vector3 GetDuelArenaSphereCenter(Vector3 arena) =>
        arena + new Vector3(0f, 0f, DuelArenaCourtHeight / 2f);

    public Vector3 GetDuelArenaCenter(Vector3 arena) =>
        GetDuelArenaSphereCenter(arena);

    private Vector3 UpdatePracticeBotDuelMovement(
        PracticeBotState state,
        CCitadelPlayerPawn botPawn,
        CCitadelPlayerPawn opponentPawn,
        DateTimeOffset now,
        DuelMatch? match
    )
    {
        var botPosition = botPawn.Position;
        var opponentPosition = opponentPawn.Position;
        Vector3? arenaPosition = match?.ArenaPosition;
        var botPosition2D = new Vector2(botPosition.X, botPosition.Y);
        var opponentPosition2D = new Vector2(
            opponentPosition.X,
            opponentPosition.Y
        );
        bool chooseNextTarget = now >= state.NextMovementAdjustment;
        Vector2 target = default;

        if (!chooseNextTarget)
        {
            target = CorrectPracticeBotStrafeRange(
                new Vector2(
                    state.StrafeTarget.X,
                    state.StrafeTarget.Y
                ),
                botPosition2D,
                opponentPosition2D
            );
            if (arenaPosition is Vector3 activeArena)
            {
                target = KeepPracticeBotTargetInsideArena(
                    target,
                    botPosition,
                    activeArena,
                    PracticeBotDuelPlayerRadius
                );
            }

            chooseNextTarget =
                Vector2.DistanceSquared(botPosition2D, target) <=
                state.Difficulty.ArrivalDistance *
                    state.Difficulty.ArrivalDistance;
        }

        if (chooseNextTarget)
        {
            ChooseNextPracticeBotMovement(
                state,
                botPosition,
                opponentPosition,
                now,
                arenaPosition
            );
            target = new Vector2(
                state.StrafeTarget.X,
                state.StrafeTarget.Y
            );
        }

        return new Vector3(target.X, target.Y, botPosition.Z);
    }

    private void ChooseNextPracticeBotMovement(
        PracticeBotState state,
        Vector3 botPosition,
        Vector3 opponentPosition,
        DateTimeOffset now,
        Vector3? arenaPosition
    )
    {
        state.StrafeDirection =
            server.random.Next(2) == 0 ? -1 : 1;

        var fromOpponent = new Vector2(
            botPosition.X - opponentPosition.X,
            botPosition.Y - opponentPosition.Y
        );
        float distance = fromOpponent.Length();
        Vector2 away = distance > 0.001f
            ? fromOpponent / distance
            : Vector2.UnitX;
        Vector2 right = new(-away.Y, away.X);

        float duration = (float)RandomRange(
            state.Difficulty.StrafeDurationMinimum,
            state.Difficulty.StrafeDurationMaximum
        );
        float stepDistance = Math.Clamp(
            state.Difficulty.StrafeSpeed *
                (float)RandomRange(
                    state.Difficulty.StepFactorMinimum,
                    state.Difficulty.StepFactorMaximum
                ) *
                duration,
            state.Difficulty.StepDistanceMinimum,
            state.Difficulty.StepDistanceMaximum
        );
        Vector2 target = new Vector2(botPosition.X, botPosition.Y)
            + right * state.StrafeDirection * stepDistance;
        target = CorrectPracticeBotStrafeRange(
            target,
            new Vector2(botPosition.X, botPosition.Y),
            new Vector2(opponentPosition.X, opponentPosition.Y)
        );
        if (arenaPosition is Vector3 activeArena)
        {
            target = KeepPracticeBotTargetInsideArena(
                target,
                botPosition,
                activeArena,
                PracticeBotDuelPlayerRadius
            );
        }

        state.StrafeTarget = new Vector3(
            target.X,
            target.Y,
            botPosition.Z
        );
        state.NextMovementAdjustment = now.AddSeconds(duration);
    }

    private Vector2 KeepPracticeBotTargetInsideArena(
        Vector2 target,
        Vector3 botPosition,
        Vector3 arenaPosition,
        float playerRadius
    )
    {
        Vector3 arenaCenter = GetDuelArenaSphereCenter(arenaPosition);
        float verticalOffset = botPosition.Z - arenaCenter.Z;
        float safeRadius = MathF.Max(
            0f,
            playerRadius - PracticeBotArenaBoundaryMargin
        );
        float horizontalRadiusSquared = MathF.Max(
            0f,
            safeRadius * safeRadius - verticalOffset * verticalOffset
        );
        Vector2 center = new(arenaCenter.X, arenaCenter.Y);
        Vector2 offset = target - center;
        float distanceSquared = offset.LengthSquared();
        if (distanceSquared <= horizontalRadiusSquared)
            return target;

        float distance = MathF.Sqrt(distanceSquared);
        Vector2 direction = distance > 0.001f
            ? offset / distance
            : Vector2.UnitX;
        return center + direction * MathF.Sqrt(horizontalRadiusSquared);
    }

    private static Vector2 CorrectPracticeBotStrafeRange(
        Vector2 moveTarget,
        Vector2 botPosition,
        Vector2 opponentPosition
    )
    {
        float currentRangeSquared = Vector2.DistanceSquared(
            botPosition,
            opponentPosition
        );
        if (
            currentRangeSquared >= PracticeBotMinimumRangeSquared &&
            currentRangeSquared <= PracticeBotMaximumRangeSquared
        )
        {
            return moveTarget;
        }

        float targetRange = currentRangeSquared < PracticeBotMinimumRangeSquared
            ? PracticeBotMinimumRange
            : PracticeBotMaximumRange;
        Vector2 targetOffset = moveTarget - opponentPosition;
        if (targetOffset.LengthSquared() < 0.001f)
            targetOffset = botPosition - opponentPosition;
        if (targetOffset.LengthSquared() < 0.001f)
            targetOffset = Vector2.UnitX;

        return opponentPosition +
            Vector2.Normalize(targetOffset) * targetRange;
    }

    private void UpdatePracticeBotAim(
        PracticeBotState state,
        DateTimeOffset now,
        CCitadelPlayerPawn targetPawn
    )
    {
        if (now >= state.NextAimUpdate)
        {
            state.NextAimUpdate =
                now.AddSeconds(state.Difficulty.AimUpdateInterval);

            float errorX = (float)(server.random.NextDouble() * 2d - 1d)
                * state.Difficulty.HorizontalAimError;
            float errorY = (float)(server.random.NextDouble() * 2d - 1d)
                * state.Difficulty.HorizontalAimError;
            float errorZ = (float)(server.random.NextDouble() * 2d - 1d)
                * state.Difficulty.VerticalAimError;
            state.AimError = new Vector3(errorX, errorY, errorZ);
        }

        state.LookAt = targetPawn.EyePosition + state.AimError;
    }

    private double RandomRange(double minimum, double maximum) =>
        minimum + server.random.NextDouble() * (maximum - minimum);

    private void RestorePracticeBotAiIfIdle()
    {
        if (practiceBots.Count != 0)
            return;

        if (practiceBotsPreviousAiEnabled)
            Bots.SetAiEnabled(true);

        practiceBotsPreviousAiEnabled = false;
    }

    private void RemovePracticeBot(uint controllerHandle)
    {
        CancelPendingBotMatch(controllerHandle);
        if (!practiceBots.Remove(controllerHandle))
            return;

        KickBot(controllerHandle);
        RestorePracticeBotAiIfIdle();
    }

    /// <summary>
    /// Kicks a bot by controller handle. Falls back to searching the player list, so a
    /// bot can't be left standing in an arena because the handle lookup came back empty
    /// (a leftover bot far from a player who joins crashes that player's game).
    /// </summary>
    private static void KickBot(uint controllerHandle)
    {
        var bot = CBaseEntity.FromHandle(controllerHandle)?.As<CCitadelPlayerController>();
        if (bot is not { IsValid: true })
        {
            bot = null;
            foreach (var player in Players.GetAll())
            {
                if (player.EntityHandle == controllerHandle)
                {
                    bot = player;
                    break;
                }
            }
        }

        if (bot is not { IsValid: true, IsBot: true })
            return;

        bot.Kick(
            "Practice bot removed.",
            ENetworkDisconnectionReason.NetworkDisconnectKicked
        );
    }

    /// <summary>
    /// Removes every practice bot whose player is gone or whose match no longer exists.
    /// Run when a player leaves and again when one joins.
    /// </summary>
    public void RemoveOrphanPracticeBots(uint departingOwnerHandle = 0)
    {
        if (practiceBots.Count == 0)
            return;

        List<uint>? orphans = null;
        foreach (var state in practiceBots.Values)
        {
            bool ownerGone =
                state.OwnerControllerHandle == departingOwnerHandle ||
                CBaseEntity.FromHandle(state.OwnerControllerHandle) is not { IsValid: true };
            // A bot still waiting for its match to start has no match yet.
            bool matchGone =
                !pendingBotReservations.ContainsKey(state.ControllerHandle) &&
                !pendingBotMatchStarts.ContainsKey(state.ControllerHandle) &&
                !HasActiveMatchFor(state.MatchKey);
            if (ownerGone || matchGone)
                (orphans ??= new()).Add(state.ControllerHandle);
        }

        if (orphans == null)
            return;

        foreach (uint handle in orphans)
            RemovePracticeBot(handle);
    }

    /// <summary>
    /// Kicks every bot that is not a practice bot in a match (/bottest bots and any
    /// other leftover). A player who joins while such a bot exists crashes, so they are
    /// removed whenever a player leaves and before a joining player is sent anything.
    /// </summary>
    public void RemoveUntrackedBots()
    {
        List<CCitadelPlayerController>? leftovers = null;
        foreach (var player in Players.GetAll())
        {
            if (player.IsValid && player.IsBot && !practiceBots.ContainsKey(player.EntityHandle))
                (leftovers ??= new()).Add(player);
        }

        if (leftovers == null)
            return;

        foreach (var bot in leftovers)
        {
            Console.WriteLine($"[aimLOCK] Removing leftover bot {bot.PlayerName}.");
            bot.Kick(
                "Leftover bot removed.",
                ENetworkDisconnectionReason.NetworkDisconnectKicked
            );
        }
    }

    private bool HasActiveMatchFor(string matchKey) =>
        server.ActiveMatches.TryGetValue(matchKey, out var match) && !match.Finished;

    /* Hiding bots from players outside their match (off by default) */

    // Hiding a bot from everyone but its opponent was added when a joining player
    // crashed while a bot existed; that crash turned out to be the server install.
    // Hiding only the bot's hero (or the bot itself) still sends its abilities, items
    // and weapon, which then point at a hero the other player's game never got: that
    // crashes the player who is not in the bot match. So it is off; /hidebots turns
    // it back on for testing.
    public enum BotHideMode { Off, Hero, All }
    public BotHideMode PracticeBotHideMode { get; private set; } = BotHideMode.Off;
    private bool botViewerMasksDirty = true;

    public void SetPracticeBotHideMode(BotHideMode mode)
    {
        PracticeBotHideMode = mode;
        botViewerMasksDirty = true;
    }

    public void HideOtherPlayersBots(CheckTransmitEvent args)
    {
        if (PracticeBotHideMode == BotHideMode.Off || practiceBots.Count == 0)
            return;

        int slot = args.PlayerSlot;
        if (slot < 0 || slot >= MaxPlayerSlots)
            return;

        if (botViewerMasksDirty)
            RebuildBotViewerMasks();

        ulong viewerBit = 1UL << slot;
        foreach (var state in practiceBots.Values)
        {
            if ((state.ViewerMask & viewerBit) != 0)
                continue;

            var bot = CBaseEntity
                .FromHandle(state.ControllerHandle)
                ?.As<CCitadelPlayerController>();
            if (bot is not { IsValid: true })
                continue;

            var pawn = bot.GetHeroPawn();
            if (pawn is { IsValid: true })
                args.Hide(pawn);
            if (PracticeBotHideMode == BotHideMode.All)
                args.Hide(bot);
        }
    }

    private void RebuildBotViewerMasks()
    {
        botViewerMasksDirty = false;
        foreach (var state in practiceBots.Values)
        {
            ulong mask = 0;
            if (state.OwnerSlot >= 0 && state.OwnerSlot < MaxPlayerSlots)
                mask |= 1UL << state.OwnerSlot;

            if (spectatingMatches.Count != 0)
            {
                foreach (var player in Players.GetAll())
                {
                    int slot = player.Slot;
                    if (
                        player.IsBot ||
                        slot < 0 ||
                        slot >= MaxPlayerSlots ||
                        !spectatingMatches.TryGetValue(player.PlayerSteamId, out var watched)
                    )
                        continue;

                    if (
                        string.Equals(watched.Player1, state.MatchKey, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(watched.Player2, state.MatchKey, StringComparison.OrdinalIgnoreCase)
                    )
                        mask |= 1UL << slot;
                }
            }

            state.ViewerMask = mask;
        }
    }

    public void ApplyDuelMatchModifiers(CCitadelPlayerPawn pawn)
    {
        ApplyHazeLoadout(pawn);
        // Not skipped by the loadout's once-per-burst check; does nothing at max level.
        ApplyMaxBoons(pawn);
    }

    public int MaxFixationForOnlinePlayers()
    {
        int updatedPlayers = 0;
        foreach (var controller in Players.GetAll())
        {
            if (!controller.IsValid)
                continue;

            var pawn = controller.GetHeroPawn();
            if (pawn is { IsValid: true } && ApplyFixation(pawn))
                updatedPlayers++;
        }

        return updatedPlayers;
    }

    /// <summary>
    /// The spawn loadout: max boons, Sleep Dagger / Smoke Bomb / Bullet Dance removed,
    /// Fixation unlocked with every upgrade and a 999 stack cap. Safe to call repeatedly.
    /// </summary>
    public void ApplyHazeLoadout(CCitadelPlayerPawn pawn, bool force = false)
    {
        if (!pawn.IsValid)
            return;

        // Several events ask for the loadout around the same moment (hero initialized,
        // respawn, spawn completed, match start). Apply it once per pawn per burst; the
        // hero-initialized hook passes force because abilities were just rebuilt.
        uint pawnHandle = pawn.EntityHandle;
        long now = Environment.TickCount64;
        if (loadoutAppliedAt.Count > 256)
            loadoutAppliedAt.Clear();
        if (
            !force &&
            loadoutAppliedAt.TryGetValue(pawnHandle, out long appliedAt) &&
            now - appliedAt < LoadoutReapplyWindowMs
        )
            return;

        loadoutAppliedAt[pawnHandle] = now;

        // The practice bot never casts abilities (its inputs are only movement and
        // shooting), but it gets the same max boons as players so its health matches.
        if (pawn.Controller is { IsBot: true })
        {
            ApplyMaxBoons(pawn);
            ApplyFixation(pawn);
            return;
        }

        ApplyMaxBoons(pawn);
        RemoveDisabledAbilities(pawn);
        ApplyFixation(pawn);
    }

    /// <summary>
    /// Max boons: the level is written directly and a zero-amount currency change makes
    /// the game recalculate the boon stats. This is how Deadworks' own Deathmatch example
    /// does it. (Feeding souls instead only reached level 3.)
    /// </summary>
    private static void ApplyMaxBoons(CCitadelPlayerPawn pawn)
    {
        if (pawn.Level >= MaxBoonLevel)
            return;

        pawn.Level = MaxBoonLevel;
        pawn.ModifyCurrency(
            ECurrencyType.EGold,
            0,
            ECurrencySource.ECheats,
            silent: true
        );
    }

    private static void RemoveDisabledAbilities(CCitadelPlayerPawn pawn)
    {
        foreach (string abilityName in DisabledHazeAbilities)
        {
            if (pawn.AbilityComponent?.FindAbilityByName(abilityName) != null)
                pawn.RemoveAbility(abilityName);
        }
    }

    private bool ApplyFixation(CCitadelPlayerPawn pawn)
    {
        if (!pawn.IsValid)
            return false;

        var fixation = pawn.AbilityComponent?.FindAbilityByName(FixationAbilityName);
        var signature3Ability = pawn.GetAbilityBySlot(EAbilitySlot.Signature3);
        if (fixation == null || fixation.EntityHandle != signature3Ability?.EntityHandle)
        {
            if (
                fixation != null
                && !pawn.RemoveAbility(fixation)
            )
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not move Fixation to Signature3 for {pawn.Controller?.PlayerName ?? "player"}."
                );
                return false;
            }

            var addedFixation = pawn.AddAbility(
                FixationAbilityName,
                (ushort)EAbilitySlot.Signature3
            );
            if (addedFixation == null || !addedFixation.IsValid)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not add Fixation to {pawn.Controller?.PlayerName ?? "player"}."
                );
                return false;
            }

            fixation = pawn.AbilityComponent?.FindAbilityByName(FixationAbilityName);
            if (fixation == null || !fixation.IsValid)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not find Fixation after adding it to {pawn.Controller?.PlayerName ?? "player"}."
                );
                return false;
            }
        }

        // Networked field: only write it when it actually changes.
        if (fixation.UpgradeBits != FullyUpgradedAbilityBits)
            fixation.UpgradeBits = FullyUpgradedAbilityBits;
        if (fixation.UpgradeBits != FullyUpgradedAbilityBits)
        {
            Console.WriteLine(
                $"[aimLOCK] Could not fully upgrade Fixation for {pawn.Controller?.PlayerName ?? "player"}."
            );
            return false;
        }

        pawn.RemoveModifier(FixationModifierName);
        var fixationModifier = pawn.AddModifier(
            FixationModifierName,
            abilityValues: new()
            {
                ["MaxStacks"] = FixationMaxStacks,
            }
        );
        if (fixationModifier is not { IsValid: true })
        {
            Console.WriteLine(
                $"[aimLOCK] Could not add the Fixation stack modifier to {pawn.Controller?.PlayerName ?? "player"}."
            );
            return false;
        }

        var modifierSchema = fixationModifier.AsSchema<Schema.CBaseModifier>();
        modifierSchema.m_iMaxStackCount = FixationMaxStacks;
        if (modifierSchema.m_iMaxStackCount != FixationMaxStacks)
        {
            Console.WriteLine(
                $"[aimLOCK] Could not set Fixation's runtime stack cap to {FixationMaxStacks} for {pawn.Controller?.PlayerName ?? "player"}."
            );
            return false;
        }

        if (HideFixationPassive)
            HideFromClients(fixationModifier);
        return true;
    }

    // The re-added passive has no link to the ability, so the game client showed it as
    // a permanent status icon on the HUD. The passive only does work on the server (it
    // puts the stacks on whoever gets shot; those still show on the target as normal),
    // so it is no longer sent to clients at all.
    private static readonly SchemaAccessor<bool> ModifierSuppressSend =
        new("CBaseModifier"u8, "m_bSuppressSendModifier"u8);
    private static bool loggedSuppressSendFailure;

    // Crash test switch (/hidepassive): whether the Fixation passive is kept off the
    // clients. Hiding it is one of the few things every Haze on the server (players and
    // bots alike) has that the original plugin didn't, so it is a suspect for the crash
    // when someone joins while another Haze is already on the server.
    public bool HideFixationPassive { get; private set; } = true;

    /// <summary>Turns hiding the Fixation passive on or off and redoes Fixation for everyone.</summary>
    public int SetHideFixationPassive(bool hide)
    {
        HideFixationPassive = hide;
        return MaxFixationForOnlinePlayers();
    }

    private static void HideFromClients(CBaseModifier modifier)
    {
        nint handle = modifier.Handle;
        if (handle == 0)
            return;

        nint address = ModifierSuppressSend.GetAddress(handle);
        // A real field sits past the VData pointer at 0x10; anything else means the
        // field was not found, so don't write.
        if (address - handle <= 0x10 || address - handle > 0x1000)
        {
            if (!loggedSuppressSendFailure)
            {
                loggedSuppressSendFailure = true;
                Console.WriteLine("[aimLOCK] Could not find m_bSuppressSendModifier; Fixation HUD icon stays.");
            }
            return;
        }

        System.Runtime.InteropServices.Marshal.WriteByte(address, 1);
    }

    private void GrantInitialAbilityCurrencies(
        CCitadelPlayerController controller,
        CCitadelPlayerPawn pawn
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !pawn.IsValid ||
            initialAbilityCurrenciesGranted.Contains(controllerHandle)
        )
            return;

        int abilityPoints = pawn.GetCurrency(ECurrencyType.EAbilityPoints);
        int abilityUnlocks = pawn.GetCurrency(ECurrencyType.EAbilityUnlocks);
        pawn.SetCurrency(
            ECurrencyType.EAbilityPoints,
            abilityPoints > int.MaxValue - InitialAbilityCurrencyGrant
                ? int.MaxValue
                : abilityPoints + InitialAbilityCurrencyGrant
        );
        pawn.SetCurrency(
            ECurrencyType.EAbilityUnlocks,
            abilityUnlocks > int.MaxValue - InitialAbilityCurrencyGrant
                ? int.MaxValue
                : abilityUnlocks + InitialAbilityCurrencyGrant
        );
        initialAbilityCurrenciesGranted.Add(controllerHandle);
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

    private void SpawnAttachedEffect(CCitadelPlayerPawn pawn, string effectName)
    {
        var effect = CreateAttachedEffect(pawn, effectName);
        if (effect == null)
            return;

        server.ScheduleOnce(3.Seconds(), () =>
        {
            if (effect.IsValid)
                effect.Destroy();
        });
    }

    /// <summary>Debug only: attaches the respawn effect to any pawn, bots included.</summary>
    public static CParticleSystem? CreateDebugEffect(CCitadelPlayerPawn pawn) =>
        CParticleSystem.Create(RespawnTeleportEffect)
            .AtPosition(pawn.Position)
            .WithControlPoint(0, pawn)
            .AttachedTo(pawn)
            .Spawn();

    private static CParticleSystem? CreateAttachedEffect(
        CCitadelPlayerPawn pawn,
        string effectName
    )
    {
        // No attached effects on bots. Attaching a particle to a bot spawned a moment
        // earlier (hero model not loaded yet on the client) is the likely cause of the
        // client crash at the start of /bot matches: a bare bot never crashed, the bot
        // match did. The effects are cosmetic; players still get theirs.
        if (!pawn.IsValid || pawn.Controller is { IsBot: true })
            return null;

        var effect = CParticleSystem.Create(effectName)
            .AtPosition(pawn.Position)
            .WithControlPoint(0, pawn)
            .AttachedTo(pawn)
            .Spawn();

        if (effect == null)
        {
            Console.WriteLine(
                $"[aimLOCK] Failed to create particle effect '{effectName}'."
            );
            return null;
        }

        return effect;
    }

    public void OnPlayerConnect(ClientFullConnectEvent args)
    {
        if (args.Controller is not { } controller)
            return;

        Console.WriteLine(
            $"Forcing Haze for {controller.PlayerName}"
        );

        RemoveOrphanPracticeBots();
        botViewerMasksDirty = true;

        uint controllerHandle = controller.EntityHandle;
        if (!initialHeroSelectionCompleted.Contains(controllerHandle))
        {
            MarkInitialHeroSelectionPending(controller);
            if (initialHeroSelectionIssued.Contains(controllerHandle))
            {
                controller.ChangeTeam(2);
                if (initialHeroHazeSelectionStarted.Contains(controllerHandle))
                    RequestInitialHazeSelection(controller);
                else
                    RequestInitialSwapHero(controller);
            }
            else
            {
                BeginInitialHazeSpawn(controller);
            }
        }

        server.UIManager.SetServerInfoVisibility(controller, true);
        server.PlayerTagManager.RefreshPlayerTag(controller);
    }

    public void OnPlayerPutInServer(CCitadelPlayerController controller)
    {
        uint controllerHandle = controller.EntityHandle;
        initialHeroSelectionPending.Remove(controllerHandle);
        initialHeroSelectionIssued.Remove(controllerHandle);
        initialHeroSelectionCompleted.Remove(controllerHandle);
        initialHeroHazeSelectionStarted.Remove(controllerHandle);
        initialAbilityCurrenciesGranted.Remove(controllerHandle);
        initialHeroPawnInitializationHandles.Remove(controllerHandle);
        MarkInitialHeroSelectionPending(controller);
        server.ScheduleOnce(0.5.Seconds(), () =>
        {
            if (!controller.IsValid)
                return;

            MarkInitialHeroSelectionPending(controller);
            BeginInitialHazeSpawn(controller);
        });
    }

    public void OnPlayerDisconnect(CCitadelPlayerController controller)
    {
        string playerName = GetMatchParticipantName(controller);
        bool queueChanged =
            server.RankedQueue.RemoveAll(entry =>
                entry.SteamId == controller.PlayerSteamId
            ) > 0;
        queueChanged |=
            server.UnrankedQueue.RemoveAll(entry =>
                entry.SteamId == controller.PlayerSteamId
            ) > 0;

        server.PendingRespawns.Remove(playerName);
        respawnRecoveryInProgress.Remove(playerName);
        StopSpectating(controller);

        uint controllerHandle = controller.EntityHandle;
        initialHeroSelectionPending.Remove(controllerHandle);
        initialHeroSelectionIssued.Remove(controllerHandle);
        initialHeroSelectionCompleted.Remove(controllerHandle);
        initialHeroHazeSelectionStarted.Remove(controllerHandle);
        initialAbilityCurrenciesGranted.Remove(controllerHandle);
        initialHeroPawnInitializationHandles.Remove(controllerHandle);
        practiceBotStrafeSpeedOverrides.Remove(controllerHandle);
        ClearPracticeBotZoneState(controller.Slot);
        InvalidateCachedPlayerName(controller);
        duelArenaBoundaryStates.Remove(controller.PlayerSteamId);

        if (
            TryGetActiveMatch(controller, out var match) &&
            !match.Finished
        )
        {
            ForfeitMatch(playerName);
        }

        // A practice bot that left on its own (or was kicked by /botclear) must not
        // stay registered, or no new bot match could start.
        if (practiceBots.Count != 0 && practiceBots.Remove(controllerHandle))
        {
            CancelPendingBotMatch(controllerHandle);
            RestorePracticeBotAiIfIdle();
        }

        // The leaving player's own bots go with them, whether or not the forfeit above
        // already removed them: a bot left behind crashed the player when they rejoined.
        RemoveLifestealDebug(controller);
        RemoveOrphanPracticeBots(controllerHandle);
        if (!controller.IsBot)
            RemoveUntrackedBots();
        botViewerMasksDirty = true;

        if (queueChanged)
            server.UIManager.RefreshHud();
    }

    public void StartInitialSpawnRecoverySweep()
    {
        if (initialSpawnRecoverySweepStarted)
            return;

        initialSpawnRecoverySweepStarted = true;
        RecoverInitialSpawns(initializeExistingPawns: true);
    }

    private void RecoverInitialSpawns(bool initializeExistingPawns = false)
    {
        bool needsAnotherSweep = false;
        foreach (var controller in Players.GetAll().Where(player => !player.IsBot).ToArray())
        {
            if (!controller.IsValid)
                continue;

            var pawn = controller.GetHeroPawn();
            if (initializeExistingPawns && pawn is { IsValid: true })
            {
                ApplyHazeLoadout(pawn);
                GrantInitialAbilityCurrencies(controller, pawn);
            }

            if (pawn is { IsValid: true, IsAlive: true })
                continue;

            uint controllerHandle = controller.EntityHandle;
            MarkInitialHeroSelectionPending(controller);
            if (
                initialHeroSelectionPending.Contains(controllerHandle) &&
                !initialHeroSelectionIssued.Contains(controllerHandle)
            )
                needsAnotherSweep = true;

            BeginInitialHazeSpawn(controller);
        }

        if (needsAnotherSweep)
            server.ScheduleOnce(1.Seconds(), () => RecoverInitialSpawns());
    }

    public void MarkInitialHeroSelectionPending(
        CCitadelPlayerController controller
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (!controller.IsBot && !initialHeroSelectionCompleted.Contains(controllerHandle))
            initialHeroSelectionPending.Add(controllerHandle);
    }

    public bool IsInitialHeroSelectionPending(
        CCitadelPlayerController controller
    ) => initialHeroSelectionPending.Contains(controller.EntityHandle);

    private void BeginInitialHazeSpawn(
        CCitadelPlayerController controller
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !initialHeroSelectionPending.Contains(controllerHandle) ||
            !initialHeroSelectionIssued.Add(controllerHandle)
        )
            return;

        controller.ChangeTeam(2);
        Console.WriteLine(
            $"[aimLOCK] Selecting a temporary hero for {controller.PlayerName} on connect."
        );
        RequestInitialSwapHero(controller);
    }

    private void RequestInitialSwapHero(CCitadelPlayerController controller)
    {
        if (!HeroTypeExtensions.TryParse(TemporaryConnectHeroName, out var temporaryHero))
        {
            Console.WriteLine(
                "[aimLOCK] Could not resolve hero_inferno for the initial hero swap."
            );
            return;
        }

        controller.SelectHero(temporaryHero);
        WaitForInitialSwapHero(controller, 120);
    }

    private void WaitForInitialSwapHero(
        CCitadelPlayerController controller,
        int retriesRemaining
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !initialHeroSelectionPending.Contains(controllerHandle) ||
            !initialHeroSelectionIssued.Contains(controllerHandle) ||
            initialHeroHazeSelectionStarted.Contains(controllerHandle)
        )
            return;

        var pawn = controller.GetHeroPawn();
        if (pawn is { IsValid: true, IsAlive: true })
        {
            if (
                !initialHeroPawnInitializationHandles.TryGetValue(
                    controllerHandle,
                    out uint registeredPawnHandle
                ) ||
                registeredPawnHandle != pawn.EntityHandle
            )
            {
                uint pawnHandle = pawn.EntityHandle;
                initialHeroPawnInitializationHandles[controllerHandle] =
                    pawnHandle;
                pawn.OnceHeroInitialized(() =>
                    BeginInitialHazeSelection(controller, pawnHandle));
            }

            if (retriesRemaining <= 0)
            {
                RetryInitialSwapHero(controller);
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                WaitForInitialSwapHero(controller, retriesRemaining - 1));
            return;
        }

        if (retriesRemaining <= 0)
        {
            RetryInitialSwapHero(controller);
            return;
        }

        server.ScheduleOnce(0.25.Seconds(), () =>
            WaitForInitialSwapHero(controller, retriesRemaining - 1));
    }

    private void BeginInitialHazeSelection(
        CCitadelPlayerController controller,
        uint initializedPawnHandle
    )
    {
        if (!controller.IsValid)
            return;

        uint controllerHandle = controller.EntityHandle;
        var pawn = controller.GetHeroPawn();
        if (
            pawn is not { IsValid: true, IsAlive: true } ||
            pawn.EntityHandle != initializedPawnHandle ||
            !initialHeroSelectionPending.Contains(controllerHandle) ||
            !initialHeroSelectionIssued.Contains(controllerHandle) ||
            !initialHeroHazeSelectionStarted.Add(controllerHandle)
        )
            return;

        initialHeroPawnInitializationHandles.Remove(controllerHandle);
        RequestInitialHazeSelection(controller);
    }

    private void RequestInitialHazeSelection(
        CCitadelPlayerController controller
    )
    {
        controller.SelectHero(Heroes.Haze);
        WaitForInitialHazeSpawn(controller, 120);
    }

    private void WaitForInitialHazeSpawn(
        CCitadelPlayerController controller,
        int retriesRemaining
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !initialHeroSelectionPending.Contains(controllerHandle) ||
            !initialHeroSelectionIssued.Contains(controllerHandle) ||
            !initialHeroHazeSelectionStarted.Contains(controllerHandle)
        )
            return;

        var pawn = controller.GetHeroPawn();
        if (pawn is { IsValid: true, IsAlive: true })
        {
            if (
                !initialHeroPawnInitializationHandles.TryGetValue(
                    controllerHandle,
                    out uint registeredPawnHandle
                ) ||
                registeredPawnHandle != pawn.EntityHandle
            )
            {
                uint pawnHandle = pawn.EntityHandle;
                initialHeroPawnInitializationHandles[controllerHandle] =
                    pawnHandle;
                pawn.OnceHeroInitialized(() =>
                    CompleteInitialHazeSpawn(controller, pawnHandle));
            }

            if (retriesRemaining <= 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Haze initialization is still pending for {controller.PlayerName}; retrying selection."
                );
                RetryInitialHazeSelection(controller);
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                WaitForInitialHazeSpawn(controller, retriesRemaining - 1));
            return;
        }

        if (retriesRemaining <= 0)
        {
            Console.WriteLine(
                $"[aimLOCK] Haze has not spawned for {controller.PlayerName}; retrying selection."
            );
            RetryInitialHazeSelection(controller);
            return;
        }

        server.ScheduleOnce(0.25.Seconds(), () =>
            WaitForInitialHazeSpawn(controller, retriesRemaining - 1));
    }

    private void RetryInitialHazeSelection(CCitadelPlayerController controller)
    {
        server.ScheduleOnce(2.Seconds(), () =>
        {
            uint controllerHandle = controller.EntityHandle;
            if (
                !controller.IsValid ||
                !initialHeroSelectionPending.Contains(controllerHandle) ||
                !initialHeroSelectionIssued.Contains(controllerHandle) ||
                !initialHeroHazeSelectionStarted.Contains(controllerHandle)
            )
                return;

            RequestInitialHazeSelection(controller);
        });
    }

    private void RetryInitialSwapHero(CCitadelPlayerController controller)
    {
        server.ScheduleOnce(2.Seconds(), () =>
        {
            uint controllerHandle = controller.EntityHandle;
            if (
                !controller.IsValid ||
                !initialHeroSelectionPending.Contains(controllerHandle) ||
                !initialHeroSelectionIssued.Contains(controllerHandle) ||
                initialHeroHazeSelectionStarted.Contains(controllerHandle)
            )
                return;

            RequestInitialSwapHero(controller);
        });
    }

    private void CompleteInitialHazeSpawn(
        CCitadelPlayerController controller,
        uint initializedPawnHandle
    )
    {
        if (!controller.IsValid)
            return;

        uint controllerHandle = controller.EntityHandle;
        var pawn = controller.GetHeroPawn();
        if (
            pawn is not { IsValid: true, IsAlive: true } ||
            pawn.EntityHandle != initializedPawnHandle ||
            !initialHeroSelectionPending.Remove(controllerHandle)
        )
            return;

        initialHeroSelectionIssued.Remove(controllerHandle);
        initialHeroSelectionCompleted.Add(controllerHandle);
        initialHeroHazeSelectionStarted.Remove(controllerHandle);
        initialHeroPawnInitializationHandles.Remove(controllerHandle);
        pawn.Teleport(aimLOCK.MidtownSpawnPosition, null, null);
        ApplyHazeLoadout(pawn);
        GrantInitialAbilityCurrencies(controller, pawn);
        ClearInjectedSouls(controller, SoulsCleanupRetries);
        server.PlayerTagManager.RefreshPlayerTag(controller);
        server.UIManager.SetServerInfoVisibility(controller, true);
        Console.WriteLine(
            $"[aimLOCK] Spawned {controller.PlayerName} as Haze at Midtown."
        );
    }

    /* Souls and buying */

    // Every player keeps this many souls to spend; topped up after each purchase.
    public const int MaxSouls = 999_999;

    /// <summary>
    /// Souls changes from the game. The starting amount is replaced with the max right
    /// away; anything else (dropped on death, hero swap, a sale) is topped back up on
    /// the next tick. Purchases are handled by OnItemPurchase.
    /// </summary>
    public HookResult OnSoulsChanged(ModifyCurrencyEvent args)
    {
        var pawn = args.Pawn;
        if (pawn is not { IsValid: true } || pawn.Controller is { IsBot: true })
            return HookResult.Continue;

        if (args.Source == ECurrencySource.EItemPurchase)
        {
            OnItemPurchase(pawn);
            return HookResult.Continue;
        }

        if (args.Source == ECurrencySource.EStartingAmount)
        {
            pawn.SetCurrency(ECurrencyType.EGold, MaxSouls);
            return HookResult.Stop;
        }

        uint pawnHandle = pawn.EntityHandle;
        server.ScheduleNextTick(() =>
        {
            var current = CBaseEntity.FromHandle(pawnHandle)?.As<CCitadelPlayerPawn>();
            if (current is { IsValid: true })
                TopUpSouls(current);
        });
        return HookResult.Continue;
    }

    private static void TopUpSouls(CCitadelPlayerPawn pawn)
    {
        if (pawn.GetCurrency(ECurrencyType.EGold) != MaxSouls)
            pawn.SetCurrency(ECurrencyType.EGold, MaxSouls);
    }

    // Players (Steam ID) who turned bot mirror off with /botmirror. On by default.
    private readonly HashSet<ulong> botMirrorOff = new();
    // Players (Steam ID) who allow buying in duels (/ab on). Off by default.
    private readonly HashSet<ulong> duelBuyingAllowed = new();

    public bool ToggleBotMirror(CCitadelPlayerController player)
    {
        if (botMirrorOff.Remove(player.PlayerSteamId))
            return true;
        botMirrorOff.Add(player.PlayerSteamId);
        return false;
    }

    public bool AllowsDuelBuying(CCitadelPlayerController player) =>
        duelBuyingAllowed.Contains(player.PlayerSteamId);

    public void SetAllowDuelBuying(CCitadelPlayerController player, bool allow)
    {
        if (allow)
            duelBuyingAllowed.Add(player.PlayerSteamId);
        else
            duelBuyingAllowed.Remove(player.PlayerSteamId);
    }

    // Pawn -> the items it had when a purchase started; checked on the next tick to
    // find what was bought.
    private readonly Dictionary<uint, HashSet<string>> pendingPurchases = new();

    /// <summary>Called when a player spends souls on an item.</summary>
    public void OnItemPurchase(CCitadelPlayerPawn pawn)
    {
        if (pawn is not { IsValid: true } || pawn.Controller is not { IsBot: false })
            return;

        uint pawnHandle = pawn.EntityHandle;
        if (pendingPurchases.ContainsKey(pawnHandle))
            return;

        pendingPurchases[pawnHandle] = GetItemNames(pawn);
        server.ScheduleNextTick(() => HandlePurchase(pawnHandle));
    }

    private static HashSet<string> GetItemNames(CCitadelPlayerPawn pawn)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var abilities = pawn.AbilityComponent?.Abilities;
        if (abilities == null)
            return names;

        foreach (var ability in abilities)
        {
            if (ability is { IsValid: true, IsItem: true })
                names.Add(ability.AbilityName);
        }

        return names;
    }

    private void HandlePurchase(uint pawnHandle)
    {
        if (!pendingPurchases.Remove(pawnHandle, out var before))
            return;

        var pawn = CBaseEntity.FromHandle(pawnHandle)?.As<CCitadelPlayerPawn>();
        if (pawn is not { IsValid: true } || pawn.Controller is not { } buyer)
            return;

        if (
            server.ActiveMatches.Count == 0 ||
            !TryGetActiveMatch(buyer, out var match) ||
            match.Finished
        )
        {
            TopUpSouls(pawn);
            return;
        }

        var opponent = FindMatchParticipant(
            string.Equals(match.Player1, GetMatchParticipantName(buyer), StringComparison.OrdinalIgnoreCase)
                ? match.Player2
                : match.Player1
        );

        foreach (string item in GetItemNames(pawn))
        {
            if (before.Contains(item))
                continue;

            string itemName = GetItemDisplayName(item);
            if (opponent is { IsBot: true })
            {
                if (botMirrorOff.Contains(buyer.PlayerSteamId))
                    continue;

                var botPawn = opponent.GetHeroPawn();
                if (
                    botPawn is { IsValid: true } &&
                    botPawn.AbilityComponent?.FindAbilityByName(item) == null
                )
                {
                    botPawn.AddItem(item);
                    Chat.PrintToChat(buyer, $"The bot bought {itemName} too. (/botmirror to turn this off)");
                }
            }
            else if (!match.BuyingAllowed)
            {
                pawn.SellItem(item, fullRefund: true);
                Chat.PrintToChat(
                    buyer,
                    "Buying items is off in this duel. Both players need /ab on before the duel."
                );
            }
            else if (opponent is { IsValid: true })
            {
                Chat.PrintToChat(opponent, $"{GetCachedPlayerName(buyer)} bought {itemName}.");
            }
        }

        TopUpSouls(pawn);
    }

    private static readonly Dictionary<string, string> ItemDisplayNames = new(StringComparer.Ordinal)
    {
        ["upgrade_blitz_bullets"] = "Swift Striker",
        ["upgrade_high_velocity_mag"] = "High-Velocity Mag",
        ["upgrade_pristine_emblem"] = "Opening Rounds",
        ["upgrade_fleetfoot_boots"] = "Fleetfoot",
        ["upgrade_titan_round"] = "Titanic Magazine",
        ["upgrade_cardio_calibrator"] = "Enduring Speed",
        ["upgrade_burst_fire"] = "Burst Fire",
        ["upgrade_chonky"] = "Fortitude",
        ["upgrade_health"] = "Extra Health",
        ["upgrade_vampire"] = "Bullet Lifesteal",
    };

    // In-game name where known, otherwise the internal name tidied up
    // ("upgrade_close_range" -> "Close Range").
    private static string GetItemDisplayName(string item)
    {
        if (ItemDisplayNames.TryGetValue(item, out string? name))
            return name;

        string trimmed = item.StartsWith("upgrade_", StringComparison.Ordinal) ? item[8..] : item;
        var words = trimmed.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(' ', words);
    }

    public void ClearInjectedSoulsFromConnectedPlayers()
    {
        foreach (var controller in Players.GetAll().Where(player => !player.IsBot).ToArray())
            ClearInjectedSouls(controller, SoulsCleanupRetries);
    }

    private void ClearInjectedSouls(
        CCitadelPlayerController controller,
        int retriesRemaining
    )
    {
        if (!controller.IsValid)
            return;

        var pawn = controller.GetHeroPawn();
        if (pawn is { IsValid: true })
        {
            pawn.OnceHeroInitialized(() =>
            {
                if (!controller.IsValid)
                    return;

                var initializedPawn = controller.GetHeroPawn();
                if (initializedPawn is not { IsValid: true })
                    return;

                TopUpSouls(initializedPawn);
            });
            return;
        }

        if (retriesRemaining <= 0)
        {
            Console.WriteLine(
                $"[aimLOCK] Could not check injected souls for {controller.PlayerName}: hero did not load."
            );
            return;
        }

        server.ScheduleOnce(0.5.Seconds(), () =>
            ClearInjectedSouls(controller, retriesRemaining - 1));
    }

    public HookResult OnPlayerDeath(PlayerDeathEvent args)
    {
        var victim = args.UseridController;

        if (victim == null)
            return HookResult.Continue;

        if (!TryGetActiveMatch(victim, out var match))
        {
            return HookResult.Continue;
        }

        if (match.Finished || !match.ArenaInitialized)
            return HookResult.Continue;

        // Fixation resets every kill, in bot matches and player duels alike. Next tick,
        // so it doesn't add to the death frame.
        server.ScheduleNextTick(() => ResetMatchFixationStacks(match));

        bool scoreRound = match.CombatActive;
        if (match.CountdownActive)
        {
            match.CountdownActive = false;
            match.CountdownGeneration++;
        }

        SetMatchDisarmed(match, true);

        if (scoreRound)
        {
            string victimName = GetMatchParticipantName(victim);
            if (string.Equals(
                victimName,
                match.Player1,
                StringComparison.OrdinalIgnoreCase
            ))
                match.Score2++;
            else
                match.Score1++;
        }

        if (scoreRound)
        {
            server.LeaderboardManager.RefreshActiveMatches();
            server.UIManager.UpdateMatchHud(match);
            UpdateArenaScoreboard(match);
            ReportRoundAccuracy(match);
        }

        if (
            scoreRound &&
            (
                match.Score1 >= aimLOCK.WIN_SCORE ||
                match.Score2 >= aimLOCK.WIN_SCORE
            )
        )
        {
            match.Finished = true;
            match.CountdownActive = false;
            match.CountdownGeneration++;
            server.ScheduleOnce(3.Seconds(), () =>
            {
                FinishMatch(match);
            });

            return HookResult.Continue;
        }

        server.ScheduleOnce(0.5.Seconds(), () =>
        {
            if (match.Finished)
                return;

            ResetDuelCurrentHealth(match);
        });

        string participantName = GetMatchParticipantName(victim);
        BeginMatchRespawnRecovery(match, participantName);

        return HookResult.Continue;
    }

    public void ResetMatchFixationStacks(DuelMatch match)
    {
        foreach (var participant in GetMatchParticipants(match))
            ResetFixationStacks(participant);
    }

    private void ResetFixationStacks(CCitadelPlayerController player)
    {
        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true })
            return;

        // The stacks live on the person who was shot (a modifier from the shooter's
        // Fixation), not on the shooter's passive: remove those too. The player who
        // died gets a fresh passive with the respawn loadout.
        RemoveFixationModifiers(pawn);
        if (!pawn.IsAlive)
            return;

        var fixation = pawn.AbilityComponent?.FindAbilityByName(
            FixationAbilityName
        );
        if (fixation is not { IsValid: true })
        {
            ApplyFixation(pawn);
            return;
        }

        pawn.RemoveModifier(FixationModifierName);
        var fixationModifier = pawn.AddModifier(
            FixationModifierName,
            abilityValues: new()
            {
                ["MaxStacks"] = FixationMaxStacks,
            }
        );
        if (fixationModifier is not { IsValid: true })
        {
            Console.WriteLine(
                $"[aimLOCK] Could not reset Fixation stacks for {player.PlayerName ?? "player"}."
            );
            return;
        }

        if (HideFixationPassive)
            HideFromClients(fixationModifier);
        var modifierSchema = fixationModifier.AsSchema<Schema.CBaseModifier>();
        modifierSchema.m_iMaxStackCount = FixationMaxStacks;
        modifierSchema.m_iStackCount = 0;
        if (
            modifierSchema.m_iMaxStackCount != FixationMaxStacks ||
            modifierSchema.m_iStackCount != 0
        )
        {
            Console.WriteLine(
                $"[aimLOCK] Could not reset Fixation stacks for {player.PlayerName ?? "player"}."
            );
        }
    }

    public HookResult OnPlayerRespawned(PlayerRespawnedEvent args)
    {
        var controller = args.Userid?.Controller as CBasePlayerController;

        if (controller == null)
            return HookResult.Continue;

        if (
            controller is CCitadelPlayerController spectatingPlayer &&
            IsSpectating(spectatingPlayer)
        )
        {
            var spectatorPawn = spectatingPlayer.GetHeroPawn();
            if (spectatorPawn is { IsValid: true })
                spectatorPawn.AddModifier("modifier_citadel_disarmed");
        }

        if (controller is CCitadelPlayerController player && !player.IsBot)
            server.PlayerTagManager.RefreshPlayerTag(player);

        if (
            controller is CCitadelPlayerController connectingPlayer &&
            initialHeroSelectionPending.Contains(connectingPlayer.EntityHandle) &&
            initialHeroSelectionIssued.Contains(connectingPlayer.EntityHandle)
        )
        {
            server.ScheduleOnce(0.25.Seconds(), () =>
                WaitForInitialHazeSpawn(connectingPlayer, 4));
        }

        if (controller is CCitadelPlayerController fixationPlayer)
        {
            server.ScheduleOnce(0.25.Seconds(), () =>
            {
                if (!fixationPlayer.IsValid)
                    return;

                var pawn = fixationPlayer.GetHeroPawn();
                if (pawn is { IsValid: true })
                    ApplyHazeLoadout(pawn);
            });
        }

        string participantName = GetMatchParticipantName(controller);
        if (
            TryGetActiveMatch(controller, out var activeMatch) &&
            !activeMatch.ArenaInitialized
        )
        {
            server.PendingRespawns.Remove(participantName);
            return HookResult.Continue;
        }

        if (!server.PendingRespawns.TryGetValue(
            participantName,
            out var match))
        {
            return HookResult.Continue;
        }

        if (match.Finished)
            return HookResult.Continue;

        BeginMatchRespawnRecovery(match, participantName);

        return HookResult.Continue;
    }

    private void BeginMatchRespawnRecovery(
        DuelMatch match,
        string participantName
    )
    {
        if (match.Finished || !match.ArenaInitialized)
            return;

        server.PendingRespawns[participantName] = match;
        if (!respawnRecoveryInProgress.Add(participantName))
            return;

        RespawnIntoArena(match, participantName);
    }

    private void RespawnIntoArena(
        DuelMatch match,
        string playerName,
        int retriesRemaining = 20,
        CCitadelPlayerController? cachedPlayer = null
    )
    {
        if (match.Finished)
        {
            respawnRecoveryInProgress.Remove(playerName);
            return;
        }

        var player = cachedPlayer is { IsValid: true }
            ? cachedPlayer
            : FindMatchParticipant(playerName);

        var pawn = player?.GetHeroPawn();

        if (
            player is not { IsValid: true } ||
            pawn is not { IsValid: true, IsAlive: true }
        )
        {
            if (retriesRemaining <= 0)
            {
                respawnRecoveryInProgress.Remove(playerName);
                Console.WriteLine(
                    $"[aimLOCK] Could not restore {playerName} to the arena after respawn."
                );
                server.Queue.StartCountdown(match);
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                RespawnIntoArena(
                    match,
                    playerName,
                    retriesRemaining - 1,
                    player
                ));
            return;
        }

        SetMatchDisarmed(match, true);

        Vector3 arenaCenter = GetDuelArenaSphereCenter(match.ArenaPosition);

        if (string.Equals(
            playerName,
            match.Player1,
            StringComparison.OrdinalIgnoreCase
        ))
        {
            pawn.Teleport(
                arenaCenter + new Vector3(-aimLOCK.DUEL_SPAWN_OFFSET, 0f, 0f),
                new Vector3(0f, 45f, 0f),
                null
            );
            PlayMatchTeleportEffect(pawn);
        }
        else if (string.Equals(
            playerName,
            match.Player2,
            StringComparison.OrdinalIgnoreCase
        ))
        {
            pawn.Teleport(
                arenaCenter + new Vector3(aimLOCK.DUEL_SPAWN_OFFSET, 0f, 0f),
                new Vector3(0f, 180f, 0f),
                null
            );
            PlayMatchTeleportEffect(pawn);
        }

        server.PendingRespawns.Remove(playerName);
        respawnRecoveryInProgress.Remove(playerName);

        // The respawn frame only teleports and disarms. The modifier and item work
        // (lifesteal, the item stack, max health) is spread over the next few ticks: the player is disarmed until the
        // countdown anyway, and RestoreDuelRespawnHealth re-checks health afterwards.
        CCitadelPlayerPawn respawnedPawn = pawn;
        server.ScheduleNextTick(() =>
        {
            if (match.Finished || !respawnedPawn.IsValid)
                return;

            ApplyDuelBulletLifesteal(respawnedPawn);
            server.ScheduleNextTick(() =>
            {
                if (match.Finished || !respawnedPawn.IsValid)
                    return;

                ApplyDuelItemStack(respawnedPawn);
                respawnedPawn.RemoveAbility("upgrade_infinite_rounds");
                server.ScheduleNextTick(() =>
                {
                    if (match.Finished || !respawnedPawn.IsValid)
                        return;

                    ApplyMaxBoons(respawnedPawn);
                    if (!HasDuelMaxHealth(respawnedPawn))
                        ApplyDuelMaxHealth(respawnedPawn);
                    SetDuelStartingHealth(respawnedPawn);
                });
            });
        });

        server.ScheduleOnce(0.5.Seconds(), () =>
        {
            RestoreDuelRespawnHealth(match, playerName);
        });
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

    /// <summary>
    /// Start of every duel and every round (the countdown): both players' Fixation
    /// stacks are cleared and both are set to full health.
    /// </summary>
    public void ResetRoundState(DuelMatch match)
    {
        ResetMatchFixationStacks(match);
        FillMatchHealth(match);
        ResetRoundAccuracy(match);
    }

    /* Round accuracy */

    // Per player, for the current duel round only: shots fired and bullet hits on the
    // opponent, split into headshots and body shots. Counted only while the round is
    // live (after FIGHT, before the kill), read out to each player when a round is
    // scored, and cleared at the start of every round.
    private sealed class RoundAccuracy
    {
        public int Shots;
        public int HeadHits;
        public int BodyHits;
    }

    private readonly Dictionary<uint, RoundAccuracy> roundAccuracy = new();
    private bool loggedGunFiredFields;

    private static readonly SchemaAccessor<float> DamageCritDamage =
        new("CTakeDamageInfo"u8, "m_flCritDamage"u8);
    private static readonly SchemaAccessor<int> DamageHitGroup =
        new("CTakeDamageInfo"u8, "m_iHitGroupId"u8);
    private const int HeadHitGroup = 1;

    private RoundAccuracy? GetLiveRoundAccuracy(CCitadelPlayerPawn? pawn)
    {
        if (
            server.ActiveMatches.Count == 0 ||
            pawn is not { IsValid: true } ||
            pawn.Controller is not { } controller ||
            !TryGetActiveMatch(controller, out var match) ||
            match.Finished ||
            !match.CombatActive
        )
            return null;

        uint handle = controller.EntityHandle;
        if (!roundAccuracy.TryGetValue(handle, out var stats))
            roundAccuracy[handle] = stats = new RoundAccuracy();
        return stats;
    }

    /// <summary>A shot fired (the GunFired modifier event).</summary>
    public void OnGunFired(ModifierEvent args)
    {
        if (server.ActiveMatches.Count == 0)
            return;

        if (!loggedGunFiredFields)
        {
            loggedGunFiredFields = true;
            Console.WriteLine(
                "[aimLOCK] Accuracy: first shot seen. Caster: " +
                (args.Caster?.DesignerName ?? "none") + ", target: " +
                (args.Target?.DesignerName ?? "none") + "."
            );
        }

        var shooter = args.Caster?.As<CCitadelPlayerPawn>();
        if (shooter is not { IsValid: true, Controller: not null })
            shooter = args.Target?.As<CCitadelPlayerPawn>();

        var stats = GetLiveRoundAccuracy(shooter);
        if (stats != null)
            stats.Shots++;
    }

    /// <summary>A bullet hit (from DamageTaken), counted as a headshot or a body shot.</summary>
    private void CountAccuracyHit(
        CCitadelPlayerPawn attacker,
        CCitadelPlayerPawn victim,
        CTakeDamageInfo info
    )
    {
        var stats = GetLiveRoundAccuracy(attacker);
        if (stats == null)
            return;

        bool headshot =
            DamageCritDamage.Get(info.Handle) > 0f ||
            DamageHitGroup.Get(info.Handle) == HeadHitGroup;
        if (headshot)
            stats.HeadHits++;
        else
            stats.BodyHits++;
    }

    private void ResetRoundAccuracy(DuelMatch match)
    {
        foreach (var participant in GetMatchParticipants(match))
            roundAccuracy.Remove(participant.EntityHandle);
    }

    private void ReportRoundAccuracy(DuelMatch match)
    {
        foreach (var participant in GetMatchParticipants(match))
        {
            if (!roundAccuracy.Remove(participant.EntityHandle, out var stats))
                stats = new RoundAccuracy();
            if (participant.IsBot)
                continue;

            int shots = Math.Max(stats.Shots, stats.HeadHits + stats.BodyHits);
            if (shots == 0)
            {
                Chat.PrintToChat(participant, "Round accuracy: no shots fired.");
                continue;
            }

            Chat.PrintToChat(
                participant,
                $"Headshot accuracy: {Percent(stats.HeadHits, shots)} | " +
                $"Overall accuracy: {Percent(stats.HeadHits + stats.BodyHits, shots)} | " +
                $"{shots} shots"
            );
        }
    }

    private static string Percent(int hits, int shots) =>
        $"{Math.Round(hits * 100.0 / shots):0}% ({hits}/{shots})";

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

    private void FinishMatch(
        DuelMatch match
    )
    {
        if (!match.Finished)
            match.Finished = true;
        match.CountdownActive = false;
        match.CountdownGeneration++;

        var onlinePlayers = Players.GetAll().ToArray();
        var player1 = FindMatchParticipant(match.Player1, onlinePlayers);
        var player2 = FindMatchParticipant(match.Player2, onlinePlayers);
        SetMatchDisarmed(match, true, player1, player2);

        string winner;
        if (match.ForfeitingPlayer is { } forfeitingPlayer)
        {
            winner = StringComparer.OrdinalIgnoreCase.Equals(
                forfeitingPlayer,
                match.Player1
            )
                ? match.Player2
                : match.Player1;
        }
        else
        {
            winner =
                match.Score1 > match.Score2
                    ? match.Player1
                    : match.Player2;
        }

        bool player1Won = StringComparer.OrdinalIgnoreCase.Equals(
            winner,
            match.Player1
        );
        var winnerController = player1Won ? player1 : player2;
        var loserController = player1Won ? player2 : player1;
        if (
            winnerController != null &&
            loserController != null &&
            !winnerController.IsBot &&
            !loserController.IsBot
        )
        {
            if (match.QueueType == MatchQueueType.Ranked)
            {
                RecordRankedResult(
                    match,
                    winnerController,
                    loserController
                );
            }
            else
            {
                var winnerRecord = GetRecord(winnerController);
                var loserRecord = GetRecord(loserController);

                winnerRecord.Wins++;
                loserRecord.Losses++;
                SaveRecords();

                Chat.PrintToChatAll(
                    $"{winnerController.PlayerName} wins! " +
                    $"Unranked record: {winnerRecord.Wins}-{winnerRecord.Losses}"
                );
            }

            server.LeaderboardManager.RequestRefresh();
        }

        Console.WriteLine(
            $"Match Over! Winner: {GetMatchParticipantDisplayName(winner)}"
        );

        SetMatchDisarmed(match, false, player1, player2);

        server.ActiveMatches.Remove(match.Player1);
        server.ActiveMatches.Remove(match.Player2);
        server.PendingRespawns.Remove(match.Player1);
        server.PendingRespawns.Remove(match.Player2);
        server.RestoreMatchInfiniteAmmoIfIdle();

        var pawn1 = player1?.GetHeroPawn();
        var pawn2 = player2?.GetHeroPawn();
        if (pawn1 != null)
        {
            server.PlayerManager.RestoreDuelBulletLifesteal(
                pawn1,
                match.Player1HadBulletLifestealItem
            );
        }
        if (pawn2 != null)
        {
            server.PlayerManager.RestoreDuelBulletLifesteal(
                pawn2,
                match.Player2HadBulletLifestealItem
            );
        }
        if (pawn1 != null)
        {
            server.PlayerManager.RestoreDuelBlitzBullets(
                pawn1,
                match.Player1HadBlitzBulletsItem
            );
        }
        if (pawn2 != null)
        {
            server.PlayerManager.RestoreDuelBlitzBullets(
                pawn2,
                match.Player2HadBlitzBulletsItem
            );
        }
        if (pawn1 != null)
        {
            server.PlayerManager.RestoreDuelHighVelocityRounds(
                pawn1,
                match.Player1HadHighVelocityRoundsItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn1,
                "upgrade_pristine_emblem",
                match.Player1HadOpeningRoundsItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn1,
                "upgrade_fleetfoot_boots",
                match.Player1HadFleetfootItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn1,
                "upgrade_titan_round",
                match.Player1HadTitanicMagazineItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn1,
                "upgrade_cardio_calibrator",
                match.Player1HadCardioCalibratorItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn1,
                "upgrade_burst_fire",
                match.Player1HadBurstFireItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn1,
                "upgrade_health",
                match.Player1HadHealthItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_health",
                match.Player1HadHealthItem
            );
        }
        if (pawn2 != null)
        {
            server.PlayerManager.RestoreDuelHighVelocityRounds(
                pawn2,
                match.Player2HadHighVelocityRoundsItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn2,
                "upgrade_pristine_emblem",
                match.Player2HadOpeningRoundsItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn2,
                "upgrade_fleetfoot_boots",
                match.Player2HadFleetfootItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn2,
                "upgrade_titan_round",
                match.Player2HadTitanicMagazineItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn2,
                "upgrade_cardio_calibrator",
                match.Player2HadCardioCalibratorItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn2,
                "upgrade_burst_fire",
                match.Player2HadBurstFireItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn2,
                "upgrade_health",
                match.Player2HadHealthItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_health",
                match.Player2HadHealthItem
            );
        }
        ClampHealthToMax(pawn1);
        ClampHealthToMax(pawn2);
        server.ScheduleOnce(0.1.Seconds(), () =>
        {
            ClampHealthToMax(pawn1);
            ClampHealthToMax(pawn2);
        });
        pawn1?.RemoveModifier("modifier_speed_boost");
        pawn2?.RemoveModifier("modifier_speed_boost");
        if (pawn1 != null)
            RemovePermanentBurstFire(pawn1);
        if (pawn2 != null)
            RemovePermanentBurstFire(pawn2);
        // The practice bot is parked or removed right after this, so it skips the trip
        // to spawn, the teleport particles and the team change.
        bool player1IsBot = IsPracticeBotController(player1);
        bool player2IsBot = IsPracticeBotController(player2);
        if (player1IsBot)
            pawn1 = null;
        if (player2IsBot)
            pawn2 = null;

        pawn1?.Teleport(
            aimLOCK.MidtownSpawnPosition,
            null,
            null
        );
        pawn2?.Teleport(
            aimLOCK.MidtownSpawnPosition,
            null,
            null
        );
        if (pawn1 != null)
            server.PlayerManager.PlayMatchTeleportEffect(pawn1);
        if (pawn2 != null)
            server.PlayerManager.PlayMatchTeleportEffect(pawn2);

        if (!player1IsBot)
            EnsureTeam(player1, 2);
        if (!player2IsBot)
            EnsureTeam(player2, 2);

        server.UIManager.HideMatchHud(player1, player2);
        server.PlayerManager.OnDuelMatchFinished(match);
        server.UIManager.RestoreHudAfterMatch(match);
        ReturnMatchSpectators(match);
        server.Queue.TryStartMatches();
    }

    public bool ForfeitMatch(string playerName)
    {
        if (
            !server.ActiveMatches.TryGetValue(playerName, out var match) ||
            match.Finished
        )
        {
            return false;
        }

        if (
            !StringComparer.OrdinalIgnoreCase.Equals(playerName, match.Player1) &&
            !StringComparer.OrdinalIgnoreCase.Equals(playerName, match.Player2)
        )
        {
            return false;
        }

        match.ForfeitingPlayer = playerName;
        match.Finished = true;
        FinishMatch(match);
        return true;
    }

    public void SetMatchDisarmed(DuelMatch match, bool disarmed)
    {
        var onlinePlayers = Players.GetAll().ToArray();
        SetMatchDisarmed(
            match,
            disarmed,
            FindMatchParticipant(match.Player1, onlinePlayers),
            FindMatchParticipant(match.Player2, onlinePlayers)
        );
    }

    private static void SetMatchDisarmed(
        DuelMatch match,
        bool disarmed,
        CCitadelPlayerController? player1,
        CCitadelPlayerController? player2
    )
    {
        match.CombatActive = !disarmed && !match.Finished;

        if (player1 is { IsValid: true })
        {
            var pawn = player1.GetHeroPawn();
            if (pawn != null)
            {
                if (disarmed)
                    pawn.AddModifier(DisarmedModifierName);
                else
                    RemoveAllDisarms(pawn);
            }
        }

        if (
            player2 is { IsValid: true } &&
            (player1 == null || player2.EntityHandle != player1.EntityHandle)
        )
        {
            var pawn = player2.GetHeroPawn();
            if (pawn != null)
            {
                // Re-arming removes every copy, so repeated disarms (death, respawn,
                // countdown) can't pile up and leave someone disarmed.
                if (disarmed)
                    pawn.AddModifier(DisarmedModifierName);
                else
                    RemoveAllDisarms(pawn);
            }
        }
    }

    private void RecordRankedResult(
        DuelMatch match,
        CCitadelPlayerController winner,
        CCitadelPlayerController loser)
    {
        bool player1Won = StringComparer.OrdinalIgnoreCase.Equals(
            GetMatchParticipantName(winner),
            match.Player1
        );
        CCitadelPlayerController player1 = player1Won ? winner : loser;
        CCitadelPlayerController player2 = player1Won ? loser : winner;

        try
        {
            EloMatchResult result = server.RankedElo.RecordMatchResult(
                GetRankedEloId(player1),
                player1.PlayerName,
                GetRankedEloId(player2),
                player2.PlayerName,
                player1Won
            );

            RankedEloStats player1Stats = server.RankedElo.GetStats(
                GetRankedEloId(player1),
                player1.PlayerName
            );
            RankedEloStats player2Stats = server.RankedElo.GetStats(
                GetRankedEloId(player2),
                player2.PlayerName
            );

            int winnerChange = player1Won
                ? result.Player1RatingAfter - result.Player1RatingBefore
                : result.Player2RatingAfter - result.Player2RatingBefore;
            int loserChange = player1Won
                ? result.Player2RatingAfter - result.Player2RatingBefore
                : result.Player1RatingAfter - result.Player1RatingBefore;
            RankedEloStats winnerStats = player1Won ? player1Stats : player2Stats;
            RankedEloStats loserStats = player1Won ? player2Stats : player1Stats;
            Chat.PrintToChatAll(
                $"[RANKED] {winner.PlayerName} wins!\n" +
                $"{winner.PlayerName}: {winnerStats.Wins}-{winnerStats.Losses} | " +
                $"Elo {winnerStats.Rating} ({FormatEloChange(winnerChange)})\n" +
                $"{loser.PlayerName}: {loserStats.Wins}-{loserStats.Losses} | " +
                $"Elo {loserStats.Rating} ({FormatEloChange(loserChange)})"
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Failed to record ranked result for {match.Player1} vs " +
                $"{match.Player2}: {ex}"
            );
            Chat.PrintToChatAll(
                $"[RANKED] {winner.PlayerName} wins, but the Elo result " +
                "could not be saved."
            );
        }
    }

    private static string FormatEloChange(int change) =>
        change > 0 ? $"+{change}" : change.ToString(CultureInfo.InvariantCulture);

    public PlayerRecord GetRecord(CCitadelPlayerController player)
    {
        ulong steamId = player.PlayerSteamId;

        if (!server.PlayerRecords.TryGetValue(steamId, out var record))
        {
            record = new PlayerRecord
            {
                SteamId = steamId,
                LastKnownName = player.PlayerName
            };

            server.PlayerRecords[steamId] = record;
        }

        record.LastKnownName = player.PlayerName;
        return record;
    }

    public void ResetPracticeBotRecord(CCitadelPlayerController bot)
    {
        ulong steamId = bot.PlayerSteamId;
        if (!server.PlayerRecords.Remove(steamId, out var record))
            return;

        if (!SaveRecords())
            server.PlayerRecords[steamId] = record;
    }

    public bool IsPingBasedMatchmakingEnabled(CCitadelPlayerController player)
    {
        return !server.PlayerRecords.TryGetValue(
                player.PlayerSteamId,
                out var record
            )
            || record.PingBasedMatchmakingEnabled;
    }

    public void SetPingBasedMatchmakingEnabled(
        CCitadelPlayerController player,
        bool enabled
    )
    {
        ulong steamId = player.PlayerSteamId;
        if (steamId == 0 || steamId == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(player));

        bool hadRecord = server.PlayerRecords.TryGetValue(
            steamId,
            out var record
        );
        if (!hadRecord && enabled)
            return;

        record ??= new PlayerRecord
        {
            SteamId = steamId,
            LastKnownName = player.PlayerName
        };
        bool previousValue = record.PingBasedMatchmakingEnabled;
        if (hadRecord && previousValue == enabled)
            return;

        record.LastKnownName = player.PlayerName;
        record.PingBasedMatchmakingEnabled = enabled;
        server.PlayerRecords[steamId] = record;

        if (SaveRecords())
            return;

        if (hadRecord)
        {
            record.PingBasedMatchmakingEnabled = previousValue;
        }
        else
        {
            server.PlayerRecords.Remove(steamId);
        }

        throw new IOException(
            "Could not save your ping-based matchmaking setting."
        );
    }

    /// <summary>The player's saved /handicap lifesteal percent, or null when off.</summary>
    public float? GetHandicapLifestealPercent(CCitadelPlayerController player)
    {
        if (
            !server.PlayerRecords.TryGetValue(player.PlayerSteamId, out var record) ||
            !record.HandicapEnabled
        )
            return null;

        return record.HandicapLifestealPercent
            ?? DuelLifestealPercent - LegacyHandicapLifestealReduction;
    }

    /// <summary>Saves the player's /handicap lifesteal percent; null turns it off.</summary>
    public void SetHandicapLifestealPercent(
        CCitadelPlayerController player,
        float? percent
    )
    {
        ulong steamId = player.PlayerSteamId;
        if (steamId == 0 || steamId == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(player));

        bool hadRecord = server.PlayerRecords.TryGetValue(
            steamId,
            out var record
        );
        if (!hadRecord && percent == null)
            return;

        record ??= new PlayerRecord
        {
            SteamId = steamId,
            LastKnownName = player.PlayerName
        };
        bool previousEnabled = record.HandicapEnabled;
        float? previousPercent = record.HandicapLifestealPercent;
        string previousName = record.LastKnownName;

        record.LastKnownName = player.PlayerName;
        record.HandicapEnabled = percent != null;
        record.HandicapLifestealPercent = percent;
        server.PlayerRecords[steamId] = record;

        if (!SaveRecords())
        {
            if (hadRecord)
            {
                record.HandicapEnabled = previousEnabled;
                record.HandicapLifestealPercent = previousPercent;
                record.LastKnownName = previousName;
            }
            else
                server.PlayerRecords.Remove(steamId);

            throw new IOException("Could not save your handicap setting.");
        }
    }

    public void ResetUnrankedRecord(ulong steamId, string playerName)
    {
        if (steamId == 0 || steamId == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(steamId));
        if (string.IsNullOrWhiteSpace(playerName))
            throw new ArgumentException("A player name is required.", nameof(playerName));

        bool hadRecord = server.PlayerRecords.TryGetValue(
            steamId,
            out var record
        );
        record ??= new PlayerRecord
        {
            SteamId = steamId,
            LastKnownName = playerName
        };

        string previousName = record.LastKnownName;
        int previousWins = record.Wins;
        int previousLosses = record.Losses;
        record.LastKnownName = playerName;
        record.Wins = 0;
        record.Losses = 0;
        server.PlayerRecords[steamId] = record;

        if (SaveRecords())
            return;

        if (hadRecord)
        {
            record.LastKnownName = previousName;
            record.Wins = previousWins;
            record.Losses = previousLosses;
        }
        else
        {
            server.PlayerRecords.Remove(steamId);
        }

        throw new IOException("Could not save the unranked record reset.");
    }

    public string GetRankedEloId(CCitadelPlayerController player) =>
        (player.IsBot ? BotRankedEloId : player.PlayerSteamId)
            .ToString(CultureInfo.InvariantCulture);

    public string GetTimeZoneId(ulong steamId)
    {
        if (
            !server.PlayerRecords.TryGetValue(steamId, out var record) ||
            string.IsNullOrWhiteSpace(record.TimeZoneId)
        )
        {
            return "UTC";
        }

        return record.TimeZoneId;
    }

    public TimeZoneInfo GetTimeZone(ulong steamId)
    {
        string timeZoneId = GetTimeZoneId(steamId);
        if (resolvedTimeZones.TryGetValue(timeZoneId, out var timeZone))
            return timeZone;

        timeZone = ResolveIanaTimeZone(timeZoneId);
        resolvedTimeZones.Add(timeZoneId, timeZone);
        return timeZone;
    }

    public void SetTimeZone(CCitadelPlayerController player, string ianaTimeZoneId)
    {
        _ = ResolveIanaTimeZone(ianaTimeZoneId);

        var record = GetRecord(player);
        string previousTimeZoneId = record.TimeZoneId;
        record.TimeZoneId = ianaTimeZoneId;

        if (!SaveRecords())
        {
            record.TimeZoneId = previousTimeZoneId;
            throw new IOException("Could not save your timezone setting.");
        }
    }

    private static TimeZoneInfo ResolveIanaTimeZone(string ianaTimeZoneId)
    {
        string normalizedId = ianaTimeZoneId.Trim();
        if (string.Equals(normalizedId, "UTC", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        if (string.IsNullOrWhiteSpace(normalizedId))
            throw new TimeZoneNotFoundException("An IANA timezone ID is required.");

        if (
            !TimeZoneInfo.TryConvertIanaIdToWindowsId(
                normalizedId,
                out string? windowsTimeZoneId
            )
        )
        {
            string canonicalId = NormalizeIanaTimeZoneId(normalizedId);
            if (
                !TimeZoneInfo.TryConvertIanaIdToWindowsId(
                    canonicalId,
                    out windowsTimeZoneId
                )
            )
            {
                throw new TimeZoneNotFoundException(
                    $"Unknown IANA timezone: {ianaTimeZoneId}"
                );
            }
        }

        if (string.IsNullOrWhiteSpace(windowsTimeZoneId))
        {
            throw new TimeZoneNotFoundException(
                $"Unknown IANA timezone: {ianaTimeZoneId}"
            );
        }

        return TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZoneId);
    }

    private static string NormalizeIanaTimeZoneId(string ianaTimeZoneId)
    {
        string[] segments = ianaTimeZoneId.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            char[] chars = segments[i].ToLowerInvariant().ToCharArray();
            bool capitalizeNext = true;
            for (int j = 0; j < chars.Length; j++)
            {
                if (char.IsLetter(chars[j]))
                {
                    if (capitalizeNext)
                        chars[j] = char.ToUpperInvariant(chars[j]);
                    capitalizeNext = false;
                }
                else
                {
                    capitalizeNext = chars[j] is '_' or '-';
                }
            }

            string normalizedSegment = new string(chars);
            segments[i] = normalizedSegment.ToLowerInvariant() switch
            {
                "us" => "US",
                "nz" => "NZ",
                "gb" => "GB",
                "etc" => "Etc",
                "systemv" => "SystemV",
                "utc" => "UTC",
                "uct" => "UCT",
                "cet" => "CET",
                "cst6cdt" => "CST6CDT",
                "eet" => "EET",
                "est" => "EST",
                "est5edt" => "EST5EDT",
                "gmt" => "GMT",
                "hst" => "HST",
                "met" => "MET",
                "mst" => "MST",
                "mst7mdt" => "MST7MDT",
                "pst8pdt" => "PST8PDT",
                "wet" => "WET",
                "w-su" => "W-SU",
                "prc" => "PRC",
                "roc" => "ROC",
                "rok" => "ROK",
                _ when normalizedSegment.StartsWith(
                    "Gmt+",
                    StringComparison.OrdinalIgnoreCase
                ) || normalizedSegment.StartsWith(
                    "Gmt-",
                    StringComparison.OrdinalIgnoreCase
                ) => "GMT" + normalizedSegment.Substring(3),
                _ when normalizedSegment.StartsWith(
                    "Gmt",
                    StringComparison.OrdinalIgnoreCase
                ) && normalizedSegment.Length > 3 && char.IsDigit(normalizedSegment[3])
                    => "GMT" + normalizedSegment.Substring(3),
                _ => normalizedSegment
            };
        }

        return string.Join("/", segments);
    }

    public void StatsCommand(Caller caller, string? targetName = null)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            var player = caller.Player;

            if (player == null)
                return;

            var record = GetRecord(player);
            int games = record.Wins + record.Losses;
            float winRate = games > 0
                ? (float)record.Wins / games * 100f
                : 0f;

            caller.Reply(
                $"[{record.LastKnownName}] " +
                $"Unranked | Record: {record.Wins}-{record.Losses} | " +
                $"WR: {winRate:F1}%"
            );
            return;
        }

        var recordFound = server.PlayerRecords.Values.FirstOrDefault(record =>
            string.Equals(
                record.LastKnownName,
                targetName,
                StringComparison.OrdinalIgnoreCase
            ));

        if (recordFound == null)
        {
            caller.Reply($"No stats found for '{targetName}'.");
            return;
        }

        int totalGames = recordFound.Wins + recordFound.Losses;
        float targetWinRate = totalGames > 0
            ? (float)recordFound.Wins / totalGames * 100f
            : 0f;

        caller.Reply(
            $"[{recordFound.LastKnownName}] " +
            $"Unranked | Record: {recordFound.Wins}-{recordFound.Losses} | " +
            $"WR: {targetWinRate:F1}%"
        );
    }

    public void LoadRecords()
    {
        try
        {
            var loaded = JsonStore.Load(
                RecordsFilePath,
                () => new Dictionary<ulong, PlayerRecord>(),
                PlayerRecordJsonOptions
            );

            server.PlayerRecords.Clear();
            foreach (var pair in loaded)
                server.PlayerRecords[pair.Key] = pair.Value;

            Console.WriteLine($"Loaded {server.PlayerRecords.Count} player records.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load records: {ex}");
        }
    }

    private bool SaveRecords()
    {
        try
        {
            JsonStore.Save(
                RecordsFilePath,
                server.PlayerRecords,
                PlayerRecordJsonOptions
            );
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save records: {ex}");
            return false;
        }
    }

    public void SetPlayersInvulnerable(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        bool enabled)
    {
        if (enabled)
        {
            player1.GetHeroPawn()?.AddModifier("modifier_invulnerable");
            player2.GetHeroPawn()?.AddModifier("modifier_invulnerable");
        }
        else
        {
            player1.GetHeroPawn()?.RemoveModifier("modifier_invulnerable");
            player2.GetHeroPawn()?.RemoveModifier("modifier_invulnerable");
        }
    }
}
