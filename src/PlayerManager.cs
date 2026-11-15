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
    private static readonly Vector3 ArenaScoreboardAngles =
        new(180f, 90f, 270f);

    private const string FixationAbilityName = "ability_stacking_damage";
    private const string FixationModifierName =
        "ability_stacking_damage/modifier_passive_haze_stacking_damage";
    private const short FixationMaxStacks = 999;
    private const int FullyUpgradedAbilityBits = 0xF;
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
    private const float DuelArenaCourtHalfSize = 250f;
    private const float DuelArenaCourtHeight = 200f;
    private const float DuelArenaPlayerMargin = 48f;
    private const float DuelArenaBarrierExpansion = 200f;
    private const float DuelArenaRadiusMarkerWidth = 2f;
    private const int DuelArenaCircleSegments = 48;
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
    private static readonly Color DuelArenaCircleColor =
        Color.FromArgb(128, 255, 255, 255);
    private const float DuelBulletLifestealPercent = 50f;
    private const float DuelHandicapLifestealReduction = 10f;
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
    private const double DuelHealthCapCheckIntervalSeconds = 0.05;
    private static readonly string[] DuelItemStack =
    {
        "upgrade_blitz_bullets",
        "upgrade_pristine_emblem",
        "upgrade_fleetfoot_boots",
        "upgrade_titan_round",
        "upgrade_cardio_calibrator",
        "upgrade_burst_fire",
        "upgrade_chonky",
        "upgrade_health",
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
    private const string MatchTeleportEffect =
        "particles/items/gold_received_coins_player.vpcf";
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
    private sealed class PracticeBotState
    {
        public uint ControllerHandle { get; init; }
        public string MatchKey { get; init; } = "";
        public string DisplayName { get; set; } = "";
        public uint OwnerControllerHandle { get; init; }
        public string OwnerName { get; init; } = "";
        public CCitadelPlayerController? TargetController { get; set; }
        public string DifficultyName { get; set; } = "Hard";
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
    private readonly HashSet<(uint PlayerHandle, int ZoneIndex)>
        playersInsidePracticeBotZones = new();
    private readonly List<CPointWorldText> practiceBotZoneLabelEntities = new();
    private readonly List<CBeam> practiceBotZoneBoundaryBeams = new();
    private readonly List<CBeam> duelArenaRadiusBeams = new();
    private readonly Vector3[] duelArenaSphereCenters;
    private readonly Dictionary<string, (int ArenaIndex, bool IsInside)>
        duelArenaBoundaryStates = new(StringComparer.OrdinalIgnoreCase);
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
        else
            ProcessPracticeBotDoorwayZone(player, pawn);
    }

    private void ProcessPracticeBotDoorwayZone(
        CCitadelPlayerController player,
        CCitadelPlayerPawn pawn
    )
    {
        for (int zoneIndex = 0; zoneIndex < PracticeBotZones.Length; zoneIndex++)
        {
            PracticeBotZone zone = PracticeBotZones[zoneIndex];
            var entryKey = (player.EntityHandle, zoneIndex);
            bool isInsideZone =
                Vector3.DistanceSquared(pawn.Position, zone.Center) <=
                zone.RadiusSquared;

            if (!isInsideZone)
            {
                playersInsidePracticeBotZones.Remove(entryKey);
                continue;
            }

            if (!playersInsidePracticeBotZones.Add(entryKey))
                continue;

            TryStartPracticeBotMatch(
                player,
                zone.Difficulty,
                message => Chat.PrintToChat(player, message)
            );
        }
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

        if (server.ActiveMatches.ContainsKey(player.PlayerName))
        {
            reply("You are already in a match.");
            return;
        }

        bool alreadyQueued = server.RankedQueue
            .Concat(server.UnrankedQueue)
            .Any(entry => StringComparer.OrdinalIgnoreCase.Equals(
                entry.PlayerName,
                player.PlayerName));
        if (alreadyQueued)
        {
            reply("Leave your current queue with /leave before starting a bot match.");
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

        if (!server.Queue.TryGetAvailableArena(out int arenaIndex))
        {
            reply("All duel arenas are currently in use.");
            return;
        }

        Vector3 arena = server.DuelLocations[arenaIndex];

        var botController = CreatePracticeBot(
            player,
            playerPawn,
            arena,
            selectedProfile,
            reply
        );
        if (botController == null)
            return;

        SetPracticeBotDifficultyName(botController, difficulty);

        if (!server.Queue.StartBotMatch(player, botController, arena, reply))
            RemovePracticeBot(botController.EntityHandle);
    }

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

        var bot = Bots.Add(
            Heroes.Haze,
            botTeam,
            spawnPosition,
            BotDifficulty.Medium
        );
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
            MatchKey = $"__aimlock_bot_{botController.EntityHandle}",
            DisplayName = botController.PlayerName,
            OwnerControllerHandle = owner.EntityHandle,
            OwnerName = owner.PlayerName,
            TargetController = owner,
            Difficulty = difficulty,
            LookAt = playerPawn.EyePosition,
        };
        practiceBots.Add(state.ControllerHandle, state);
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
        state.DisplayName = $"Bot ({difficultyName})";
        botController.PlayerName = state.DisplayName;
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
                ? participantName
                : server.GuildManager.FormatPlayerName(participant);
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

    public bool IsPracticeBot(CBaseEntity entity) =>
        entity is CCitadelPlayerPawn pawn &&
        pawn.Controller is { } controller &&
        practiceBots.ContainsKey(controller.EntityHandle);

    public bool TryGetActiveMatch(
        CBasePlayerController player,
        out DuelMatch match
    )
    {
        if (
            practiceBots.TryGetValue(player.EntityHandle, out var bot) &&
            server.ActiveMatches.TryGetValue(bot.MatchKey, out match!)
        )
            return true;

        if (server.ActiveMatches.TryGetValue(player.PlayerName, out match!))
            return true;

        match = null!;
        return false;
    }

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
                StringComparer.OrdinalIgnoreCase.Equals(
                    entry.PlayerName,
                    player.PlayerName
                )
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
        pawn.AddModifier("modifier_citadel_disarmed");
        caller.Reply(
            $"Spectating {GetMatchParticipantDisplayName(matchToSpectate.Player1)} " +
            $"vs {GetMatchParticipantDisplayName(matchToSpectate.Player2)}."
        );
    }

    public void StopSpectating(CCitadelPlayerController player)
    {
        spectatingMatches.Remove(player.PlayerSteamId);
        player.GetHeroPawn()?.RemoveModifier("modifier_citadel_disarmed");
    }

    private string GetMatchParticipantDisplayName(string participantName)
    {
        var participant = FindMatchParticipant(participantName);
        if (participant == null)
            return participantName;

        return GetPracticeBotDisplayName(participant) ??
            server.GuildManager.FormatPlayerName(participant);
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

        player.GetHeroPawn()?.RemoveModifier("modifier_citadel_disarmed");
        player.ChangeTeam(2);
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

    private CCitadelPlayerController? FindMatchParticipant(
        string participantName,
        IEnumerable<CCitadelPlayerController> players
    )
    {
        var bot = practiceBots.Values.FirstOrDefault(state =>
            string.Equals(
                participantName,
                state.MatchKey,
                StringComparison.OrdinalIgnoreCase
            )
        );
        if (bot != null)
        {
            var botController = players.FirstOrDefault(player =>
                player.EntityHandle == bot.ControllerHandle
            );
            if (botController != null)
                return botController;
        }

        return players.FirstOrDefault(player =>
            string.Equals(
                player.PlayerName,
                participantName,
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

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

    public HookResult ApplyDuelBulletDamage(TakeDamageEvent args)
    {
        if (
            args.Entity is CCitadelPlayerPawn victimPawn &&
            victimPawn.Controller is { } victim &&
            TryGetActiveMatch(victim, out var match) &&
            !match.Finished &&
            match.CombatActive
        )
        {
            if (args.Info.CitadelDamageType == ECitadelDamageType.Bullet)
            {
                args.Info.Damage *=
                    aimLOCK.DUEL_BULLET_DAMAGE / aimLOCK.BASE_DUEL_BULLET_DAMAGE;
            }
        }

        return HookResult.Continue;
    }

    public void ApplyDuelBulletLifesteal(
        CCitadelPlayerPawn pawn
    )
    {
        if (pawn.IsBot)
        {
            return;
        }

        pawn.RemoveModifier("upgrade_vampire/modifier_intrinsic_base");
        float lifestealPercent = DuelBulletLifestealPercent;
        if (
            pawn.Controller is CCitadelPlayerController player &&
            server.PlayerRecords.TryGetValue(
                player.PlayerSteamId,
                out var record
            ) &&
            record.HandicapEnabled
        )
        {
            lifestealPercent = Math.Max(
                0f,
                lifestealPercent - DuelHandicapLifestealReduction
            );
        }

        pawn.AddModifier(
            "upgrade_vampire/modifier_intrinsic_base",
            abilityValues: new()
            {
                ["BulletLifestealPercent"] = lifestealPercent,
            }
        );
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

    public void ApplyDuelBlitzBullets(CCitadelPlayerPawn pawn)
    {
        if (
            pawn.AbilityComponent?.FindAbilityByName("upgrade_blitz_bullets")
            == null
        )
        {
            pawn.AddItem("upgrade_blitz_bullets");
        }

        pawn.RemoveModifier(
            "upgrade_blitz_bullets/modifier_intrinsic_base"
        );
        pawn.AddModifier(
            "upgrade_blitz_bullets/modifier_intrinsic_base",
            abilityValues: new()
            {
                ["BonusFireRate"] = DuelBlitzBulletsFireRatePercent,
            }
        );
    }

    public void RestoreDuelBlitzBullets(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch
    )
    {
        pawn.RemoveModifier(
            "upgrade_blitz_bullets/modifier_intrinsic_base"
        );
        if (hadItemBeforeMatch)
        {
            pawn.AddModifier(
                "upgrade_blitz_bullets/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BonusFireRate"] = DefaultBlitzBulletsFireRatePercent,
                }
            );
            return;
        }

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

        ApplyDuelBlitzBullets(pawn);
        foreach (string itemName in DuelItemStack)
            OverrideDuelItemHealthRegen(pawn, itemName);
    }

    private static void OverrideDuelItemHealthRegen(
        CCitadelPlayerPawn pawn,
        string itemName
    )
    {
        if (pawn.AbilityComponent?.FindAbilityByName(itemName) == null)
            return;

        string modifierName = $"{itemName}/modifier_intrinsic_base";
        pawn.RemoveModifier(modifierName);
        var abilityValues = DuelHealthRegenerationProperties.ToDictionary(
            propertyName => propertyName,
            _ => 0f
        );
        pawn.AddModifier(
            modifierName,
            abilityValues: abilityValues
        );
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

        Vector3 position = pawn.Position;
        if (!duelArenaBoundaryStates.TryGetValue(
            controller.PlayerName,
            out var boundaryState
        ))
        {
            int nearestArenaIndex = -1;
            float nearestDistanceSquared = float.MaxValue;
            for (int i = 0; i < server.DuelLocations.Length; i++)
            {
                Vector3 offset = position - duelArenaSphereCenters[i];
                if (
                    MathF.Abs(offset.X) > DuelArenaOuterRadius ||
                    MathF.Abs(offset.Y) > DuelArenaOuterRadius ||
                    MathF.Abs(offset.Z) > DuelArenaOuterRadius
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
                return null;

            float interiorRadius =
                DuelArenaSphereRadius - DuelArenaPlayerMargin;
            boundaryState = (
                nearestArenaIndex,
                nearestDistanceSquared <= interiorRadius * interiorRadius
            );
            duelArenaBoundaryStates[controller.PlayerName] = boundaryState;
        }
        else if (
            Vector3.DistanceSquared(
                position,
                duelArenaSphereCenters[boundaryState.ArenaIndex]
            ) > DuelArenaOuterRadiusSquared
        )
        {
            duelArenaBoundaryStates.Remove(controller.PlayerName);
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
            duelArenaBoundaryStates.Remove(player.PlayerName);
            return;
        }

        float interiorRadius = arenaSphereRadius - DuelArenaPlayerMargin;
        duelArenaBoundaryStates[player.PlayerName] = (
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

        Vector3 outward = distanceSquared > 0f
            ? Vector3.Normalize(offset)
            : Vector3.UnitX;
        Vector3 clampedPosition = sphereCenter + outward * boundaryRadius;

        Vector3 velocity = pawn.AbsVelocity;
        float outwardSpeed = Vector3.Dot(velocity, outward);
        if ((isInside && outwardSpeed > 0f) || (!isInside && outwardSpeed < 0f))
            velocity -= outward * outwardSpeed;

        pawn.Teleport(clampedPosition, null, velocity);
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

    public void RemovePracticeBotZoneBoundaries() =>
        CBeam.RemoveAll(practiceBotZoneBoundaryBeams);

    public void CreatePracticeBotDoorwayLabel()
    {
        foreach (var zone in PracticeBotZones)
        {
            Vector3 labelPosition = zone.LabelPosition;

            foreach (var entity in Entities.All)
            {
                if (
                    entity.Is<CPointWorldText>() &&
                    Vector3.DistanceSquared(entity.Position, labelPosition) <=
                        PracticeBotDoorwayLabelNearbyDistanceSquared
                )
                    entity.Remove();
            }

            CreatePracticeBotZoneLabel(zone, labelPosition);
        }
    }

    private void CreatePracticeBotZoneLabel(
        PracticeBotZone zone,
        Vector3 labelPosition
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
                    zone.Label.Length * characterWidth +
                    PracticeBotDoorwayLabelHorizontalPadding
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
                $"[aimLOCK] Failed to create the {zone.Label} doorway label background."
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
        practiceBotZoneLabelEntities.Add(backgroundText);

        var labelText = CPointWorldText.Create(
            zone.Label,
            labelPosition,
            fontSize: PracticeBotDoorwayLabelFontSize,
            worldUnitsPerPx: PracticeBotDoorwayLabelWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1
        );
        if (labelText == null)
        {
            backgroundText.Remove();
            practiceBotZoneLabelEntities.Remove(backgroundText);
            Console.WriteLine(
                $"[aimLOCK] Failed to create the {zone.Label} doorway label."
            );
            return;
        }

        labelText.ColorABGR = zone.LabelColorABGR;
        labelText.Fullbright = true;
        labelText.JustifyHorizontal = HorizontalJustify.Center;
        labelText.Teleport(angles: PracticeBotDoorwayLabelAngles);
        practiceBotZoneLabelEntities.Add(labelText);
    }

    public void RemovePracticeBotDoorwayLabel()
    {
        foreach (var labelEntity in practiceBotZoneLabelEntities)
        {
            if (labelEntity is { IsValid: true })
                labelEntity.Remove();
        }

        practiceBotZoneLabelEntities.Clear();
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
        if (!practiceBots.Remove(controllerHandle))
            return;

        var bot = Players.GetAll().FirstOrDefault(player =>
            player.IsBot &&
            player.EntityHandle == controllerHandle
        );
        bot?.Kick(
            "Practice bot removed.",
            ENetworkDisconnectionReason.NetworkDisconnectKicked
        );

        RestorePracticeBotAiIfIdle();
    }

    public void ApplyDuelMatchModifiers(
        CCitadelPlayerPawn pawn,
        bool hadGlassCannonItemBeforeMatch
    )
    {
        ApplyFixation(pawn);
        ApplyDuelGlassCannon(pawn, hadGlassCannonItemBeforeMatch);
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

        return true;
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

    public void ApplyDuelGlassCannon(
        CCitadelPlayerPawn pawn,
        bool hadGlassCannonItemBeforeMatch
    )
    {
        pawn.RemoveModifier(
            "upgrade_glass_cannon/modifier_glass_cannon_intrinstic"
        );
        AddDuelGlassCannonModifier(
            pawn,
            aimLOCK.DUEL_GLASS_CANNON_HEALTH_LOSS,
            hadGlassCannonItemBeforeMatch
        );
    }

    private static void AddDuelGlassCannonModifier(
        CCitadelPlayerPawn pawn,
        float maxHealthLossPercent,
        bool hadGlassCannonItemBeforeMatch
    )
    {
        var abilityValues = new System.Collections.Generic.Dictionary<
            string,
            float
        >
        {
            ["MaxHealthLossPercent"] = maxHealthLossPercent,
        };
        if (!hadGlassCannonItemBeforeMatch)
            abilityValues["BaseAttackDamagePercent"] =
                aimLOCK.DUEL_GLASS_CANNON_DAMAGE;

        pawn.AddModifier(
            "upgrade_glass_cannon/modifier_glass_cannon_intrinstic",
            abilityValues
        );
    }

    private static void RemoveDuelGlassCannon(
        CCitadelPlayerPawn pawn,
        bool hadGlassCannonItemBeforeMatch
    )
    {
        pawn.RemoveModifier(
            "upgrade_glass_cannon/modifier_glass_cannon_intrinstic"
        );
        if (hadGlassCannonItemBeforeMatch)
            pawn.AddModifier(
                "upgrade_glass_cannon/modifier_glass_cannon_intrinstic"
            );
    }

    private static void ClampHealthToMax(CCitadelPlayerPawn? pawn)
    {
        if (pawn is { IsValid: true, IsAlive: true })
            pawn.Health = Math.Min(pawn.Health, pawn.GetMaxHealth());
    }

    private static void ApplyDuelMaxHealth(CCitadelPlayerPawn pawn)
    {
        const string modifierName =
            "upgrade_health/modifier_intrinsic_base";

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

        var abilityValues = DuelHealthRegenerationProperties.ToDictionary(
            propertyName => propertyName,
            _ => 0f
        );
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
        pawn.Health = Math.Min(DuelMaxHealth, pawn.GetMaxHealth());
    }

    public void StartDuelHealthCapMonitor(DuelMatch match)
    {
        void MonitorHealthCap()
        {
            if (match.Finished)
                return;

            foreach (var player in GetMatchParticipants(match))
            {
                var pawn = player.GetHeroPawn();
                if (pawn is { IsValid: true, IsAlive: true })
                {
                    ClampHealthToMax(pawn);
                }
            }

            server.ScheduleOnce(
                DuelHealthCapCheckIntervalSeconds.Seconds(),
                MonitorHealthCap
            );
        }

        MonitorHealthCap();
    }

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

    private static CParticleSystem? CreateAttachedEffect(
        CCitadelPlayerPawn pawn,
        string effectName
    )
    {
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
                StringComparer.OrdinalIgnoreCase.Equals(
                    entry.PlayerName,
                    playerName
                )
            ) > 0;
        queueChanged |=
            server.UnrankedQueue.RemoveAll(entry =>
                StringComparer.OrdinalIgnoreCase.Equals(
                    entry.PlayerName,
                    playerName
                )
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
        playersInsidePracticeBotZones.RemoveWhere(
            entry => entry.PlayerHandle == controller.EntityHandle
        );
        duelArenaBoundaryStates.Remove(controller.PlayerName);

        if (
            TryGetActiveMatch(controller, out var match) &&
            !match.Finished
        )
        {
            ForfeitMatch(playerName);
        }

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
                ApplyFixation(pawn);
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
        if (!HeroTypeExtensions.TryParse("hero_inferno", out var temporaryHero))
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
        ApplyFixation(pawn);
        GrantInitialAbilityCurrencies(controller, pawn);
        ClearInjectedSouls(controller, SoulsCleanupRetries);
        server.PlayerTagManager.RefreshPlayerTag(controller);
        server.UIManager.SetServerInfoVisibility(controller, true);
        Console.WriteLine(
            $"[aimLOCK] Spawned {controller.PlayerName} as Haze at Midtown."
        );
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

                if (
                    initializedPawn.GetCurrency(ECurrencyType.EGold)
                    == InjectedSoulsBalance
                )
                {
                    initializedPawn.SetCurrency(ECurrencyType.EGold, 0);
                }
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
        var killer = args.AttackerController;

        if (victim == null)
            return HookResult.Continue;

        if (!TryGetActiveMatch(victim, out var match))
        {
            return HookResult.Continue;
        }

        if (match.Finished || !match.ArenaInitialized)
            return HookResult.Continue;

        ResetMatchFixationStacks(match);

        bool scoreRound = match.CombatActive;
        if (match.CountdownActive)
        {
            match.CountdownActive = false;
            match.CountdownGeneration++;
        }

        SetMatchDisarmed(match, true);

        if (scoreRound && killer != null)
        {
            string killerName = GetMatchParticipantName(killer);
            if (string.Equals(
                killerName,
                match.Player1,
                StringComparison.OrdinalIgnoreCase
            ))
                match.Score1++;

            if (string.Equals(
                killerName,
                match.Player2,
                StringComparison.OrdinalIgnoreCase
            ))
                match.Score2++;
        }

        if (scoreRound)
        {
            server.UIManager.UpdateMatchHud(match);
            UpdateArenaScoreboard(match);
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

    private static void ResetFixationStacks(CCitadelPlayerController player)
    {
        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true })
            return;

        var fixation = pawn.AbilityComponent?.FindAbilityByName(
            FixationAbilityName
        );
        if (fixation is not { IsValid: true })
            return;

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
                    ApplyFixation(pawn);
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
        bool hadGlassCannonItemBeforeMatch = string.Equals(
            playerName,
            match.Player1,
            StringComparison.OrdinalIgnoreCase
        )
            ? match.Player1HadGlassCannonItem
            : match.Player2HadGlassCannonItem;
        ApplyDuelGlassCannon(pawn, hadGlassCannonItemBeforeMatch);
        ApplyDuelBulletLifesteal(pawn);
        ApplyDuelItemStack(pawn);
        if (pawn.GetMaxHealth() != DuelMaxHealth)
            ApplyDuelMaxHealth(pawn);
        pawn.RemoveAbility("upgrade_infinite_rounds");

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

        SetDuelStartingHealth(pawn);
        server.PendingRespawns.Remove(playerName);
        respawnRecoveryInProgress.Remove(playerName);

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

        if (pawn.AbilityComponent?.FindAbilityByName("upgrade_health") == null)
            pawn.AddItem("upgrade_health");
        if (pawn.GetMaxHealth() != DuelMaxHealth)
            ApplyDuelMaxHealth(pawn);
        if (pawn.GetMaxHealth() == DuelMaxHealth)
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

            server.LeaderboardManager.Refresh();
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
                "upgrade_chonky",
                match.Player1HadFortitudeItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn1,
                "upgrade_health",
                match.Player1HadHealthItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_blitz_bullets",
                match.Player1HadBlitzBulletsItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_pristine_emblem",
                match.Player1HadOpeningRoundsItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_fleetfoot_boots",
                match.Player1HadFleetfootItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_titan_round",
                match.Player1HadTitanicMagazineItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_cardio_calibrator",
                match.Player1HadCardioCalibratorItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_burst_fire",
                match.Player1HadBurstFireItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_chonky",
                match.Player1HadFortitudeItem
            );
            RestoreDuelItemHealthRegen(
                pawn1,
                "upgrade_health",
                match.Player1HadHealthItem
            );
        }
        if (pawn2 != null)
        {
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
                "upgrade_chonky",
                match.Player2HadFortitudeItem
            );
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn2,
                "upgrade_health",
                match.Player2HadHealthItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_blitz_bullets",
                match.Player2HadBlitzBulletsItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_pristine_emblem",
                match.Player2HadOpeningRoundsItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_fleetfoot_boots",
                match.Player2HadFleetfootItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_titan_round",
                match.Player2HadTitanicMagazineItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_cardio_calibrator",
                match.Player2HadCardioCalibratorItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_burst_fire",
                match.Player2HadBurstFireItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_chonky",
                match.Player2HadFortitudeItem
            );
            RestoreDuelItemHealthRegen(
                pawn2,
                "upgrade_health",
                match.Player2HadHealthItem
            );
        }
        if (pawn1 != null)
        {
            RemoveDuelGlassCannon(pawn1, match.Player1HadGlassCannonItem);
            ClampHealthToMax(pawn1);
        }
        if (pawn2 != null)
        {
            RemoveDuelGlassCannon(pawn2, match.Player2HadGlassCannonItem);
            ClampHealthToMax(pawn2);
        }
        server.ScheduleOnce(0.1.Seconds(), () =>
        {
            ClampHealthToMax(pawn1);
            ClampHealthToMax(pawn2);
        });
        pawn1?.RemoveModifier("modifier_speed_boost");
        pawn2?.RemoveModifier("modifier_speed_boost");
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

        player1?.ChangeTeam(2);
        player2?.ChangeTeam(2);

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
                    pawn.AddModifier("modifier_citadel_disarmed");
                else
                    pawn.RemoveModifier("modifier_citadel_disarmed");
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
                if (disarmed)
                    pawn.AddModifier("modifier_citadel_disarmed");
                else
                    pawn.RemoveModifier("modifier_citadel_disarmed");
            }
        }
    }

    private void RecordRankedResult(
        DuelMatch match,
        CCitadelPlayerController winner,
        CCitadelPlayerController loser)
    {
        bool player1Won = StringComparer.OrdinalIgnoreCase.Equals(
            winner.PlayerName,
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

    public bool IsHandicapEnabled(CCitadelPlayerController player) =>
        server.PlayerRecords.TryGetValue(
            player.PlayerSteamId,
            out var record
        ) && record.HandicapEnabled;

    public void SetHandicapEnabled(
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
        if (!hadRecord && !enabled)
            return;

        record ??= new PlayerRecord
        {
            SteamId = steamId,
            LastKnownName = player.PlayerName
        };
        bool previousValue = record.HandicapEnabled;
        string previousName = record.LastKnownName;
        if (!hadRecord || previousValue != enabled)
        {
            record.LastKnownName = player.PlayerName;
            record.HandicapEnabled = enabled;
            server.PlayerRecords[steamId] = record;

            if (!SaveRecords())
            {
                if (hadRecord)
                {
                    record.HandicapEnabled = previousValue;
                    record.LastKnownName = previousName;
                }
                else
                    server.PlayerRecords.Remove(steamId);

                throw new IOException("Could not save your handicap setting.");
            }
        }

        if (
            TryGetActiveMatch(player, out var match) &&
            !match.Finished &&
            player.GetHeroPawn() is { IsValid: true } pawn
        )
        {
            ApplyDuelBulletLifesteal(pawn);
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

    public const string RecordsFileName = "records.json";
    private static readonly string RecordsFilePath = JsonStore.PathFor(RecordsFileName);

    public void LoadRecords()
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

    private bool SaveRecords()
    {
        try
        {
            JsonStore.Save(RecordsFilePath, server.PlayerRecords, PlayerRecordJsonOptions);
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
