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

namespace aimLOCK;

public class PlayerManager
{
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
    private const float DuelArenaBarrierExpansion = 100f;
    private const float DuelArenaRadiusMarkerWidth = 2f;
    private const int DuelArenaCircleSegments = 48;
    private static readonly float DuelArenaSphereRadius = MathF.Sqrt(
        2f * DuelArenaCourtHalfSize * DuelArenaCourtHalfSize +
        DuelArenaCourtHeight * DuelArenaCourtHeight / 4f
    ) + DuelArenaPlayerMargin * 2f + DuelArenaBarrierExpansion;
    private static readonly Color DuelArenaCircleColor =
        Color.FromArgb(128, 255, 255, 255);
    private const float DuelBulletLifestealPercent = 100f;
    private const float DefaultBulletLifestealPercent = 28f;
    private const float DefaultBlitzBulletsFireRatePercent = 22f;
    private const float DuelBlitzBulletsFireRatePercent =
        DefaultBlitzBulletsFireRatePercent / 2f;
    private const float DefaultUpgradeHealthBonus = 160f;
    private const float DefaultUpgradeHealthAttackDamagePercent = 6f;
    private const string MatchTeleportEffect =
        "particles/items/gold_received_coins_player.vpcf";
    private const string RespawnTeleportEffect =
        "particles/generic/player_respawn_deploy.vpcf";
    public const float DefaultPracticeBotStrafeSpeed = 2500f;
    private static readonly PracticeBotDifficultyProfile EasyBotProfile = new(
        1200f, 50f, 0.45d, 0.45d, 0.85d, 0.4d, 0.7d, 90f, 300f,
        85f, 28f, 0.25d
    );
    private static readonly PracticeBotDifficultyProfile MediumBotProfile = new(
        1800f, 50f, 0.65d, 0.18d, 0.38d, 0.55d, 0.85d, 75f, 260f,
        35f, 11f, 0.12d
    );
    private static readonly PracticeBotDifficultyProfile HardBotProfile = new(
        DefaultPracticeBotStrafeSpeed, 40f, 0.8d, 0.1d, 0.25d, 0.65d, 0.95d,
        80f, 300f, 25f, 8f, 0.1d
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
    );

    private readonly aimLOCK server;
    private readonly HashSet<string> respawnRecoveryInProgress =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, DuelMatch> spectatingMatches = new();
    private readonly HashSet<uint> initialHeroSelectionPending = new();
    private readonly HashSet<uint> initialHeroSelectionIssued = new();
    private readonly HashSet<uint> initialHeroSelectionCompleted = new();
    private readonly HashSet<uint> initialHeroHazeSelectionStarted = new();
    private readonly Dictionary<uint, uint> initialHeroPawnInitializationHandles =
        new();
    private bool initialSpawnRecoverySweepStarted;
    private string? practiceBotName;
    private string? practiceBotBaseName;
    private string? practiceBotTargetName;
    private uint? practiceBotControllerHandle;
    private bool practiceBotPreviousAiEnabled;
    private PracticeBotDifficultyProfile practiceBotDifficulty = HardBotProfile;
    private Vector3 practiceBotLookAt;
    private Vector3 practiceBotAimError;
    private Vector3 practiceBotStrafeTarget;
    private int practiceBotStrafeDirection = 1;
    private DateTimeOffset practiceBotNextMovementAdjustment;
    private DateTimeOffset practiceBotNextAimUpdate;
    private readonly HashSet<string> playersInsidePracticeBotZones =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CPointWorldText> practiceBotZoneLabelEntities = new();
    private readonly List<CBeam> practiceBotZoneBoundaryBeams = new();
    private readonly List<CBeam> duelArenaRadiusBeams = new();
    private readonly Dictionary<string, (int ArenaIndex, bool IsInside)>
        duelArenaBoundaryStates = new(StringComparer.OrdinalIgnoreCase);

    public PlayerManager(aimLOCK server)
    {
        this.server = server;
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

    public void ProcessPracticeBotDoorwayZone(ProcessUsercmdsEvent args)
    {
        if (args.Controller is not { IsBot: false } player)
            return;

        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true })
            return;

        foreach (var zone in PracticeBotZones)
        {
            string entryKey = $"{zone.Difficulty}:{player.PlayerName}";
            bool isInsideZone =
                Vector3.DistanceSquared(pawn.Position, zone.Center) <=
                zone.Radius * zone.Radius;

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

        practiceBotDifficulty = selectedProfile;
        Vector3 arena = server.DuelLocations[
            server.random.Next(server.DuelLocations.Length)
        ];

        var existingBot = practiceBotName == null
            ? null
            : Players.GetAll().FirstOrDefault(controller =>
                controller.IsBot &&
                string.Equals(
                    controller.PlayerName,
                    practiceBotName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        bool createdBot = false;
        CCitadelPlayerController? botController = existingBot;

        if (botController == null)
        {
            if (practiceBotName != null)
                RemovePracticeBot();

            botController = CreatePracticeBot(playerPawn, arena, reply);
            if (botController == null)
                return;

            createdBot = true;
        }

        SetPracticeBotDifficultyName(botController, difficulty);

        if (!server.Queue.StartBotMatch(player, botController, arena, reply))
        {
            if (createdBot)
                RemovePracticeBot();
        }
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
        CCitadelPlayerPawn playerPawn,
        Vector3 arena,
        Action<string> reply
    )
    {
        var spawnPosition = GetDuelArenaSphereCenter(arena) +
            new Vector3(aimLOCK.DUEL_SPAWN_OFFSET, 0f, 0f);
        int botTeam = 3;

        practiceBotPreviousAiEnabled = Bots.AiEnabled;
        if (
            practiceBotPreviousAiEnabled &&
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
            if (practiceBotPreviousAiEnabled)
                Bots.SetAiEnabled(true);

            reply("Could not spawn a practice bot here.");
            return null;
        }

        var botController = bot.Controller;
        if (botController == null)
        {
            if (practiceBotPreviousAiEnabled)
                Bots.SetAiEnabled(true);

            reply("The practice bot did not finish loading.");
            return null;
        }

        practiceBotName = botController.PlayerName;
        practiceBotBaseName = botController.PlayerName;
        practiceBotControllerHandle = botController.EntityHandle;
        practiceBotTargetName = playerPawn.Controller?.PlayerName;
        practiceBotLookAt = playerPawn.EyePosition;
        practiceBotAimError = Vector3.Zero;
        ResetPracticeBotMovement(
            spawnPosition,
            DateTimeOffset.UtcNow
        );
        practiceBotNextAimUpdate = DateTimeOffset.MinValue;

        return botController;
    }

    private void SetPracticeBotDifficultyName(
        CCitadelPlayerController botController,
        string? difficulty
    )
    {
        string difficultyName = difficulty?.Trim().ToLowerInvariant() switch
        {
            "easy" => "Easy",
            "medium" => "Medium",
            _ => "Hard"
        };
        practiceBotBaseName ??= botController.PlayerName;
        botController.PlayerName =
            $"{practiceBotBaseName} ({difficultyName})";
        practiceBotName = botController.PlayerName;
        practiceBotControllerHandle = botController.EntityHandle;
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

        practiceBotDifficulty = practiceBotDifficulty with
        {
            StrafeSpeed = speed,
        };
        caller.Reply($"Bot strafe speed set to {speed:F0} units per second.");
    }

    public void ConfigurePracticeBotStrafe(
        CCitadelPlayerPawn playerPawn,
        CCitadelPlayerPawn botPawn
    )
    {
        if (!IsPracticeBot(botPawn))
            return;

        practiceBotTargetName = playerPawn.Controller?.PlayerName;
        practiceBotLookAt = playerPawn.EyePosition;
        practiceBotAimError = Vector3.Zero;
        ResetPracticeBotMovement(
            botPawn.Position,
            DateTimeOffset.UtcNow
        );
        practiceBotNextAimUpdate = DateTimeOffset.MinValue;
    }

    private void ResetPracticeBotMovement(
        Vector3 botPosition,
        DateTimeOffset now
    )
    {
        practiceBotStrafeTarget = botPosition;
        if (
            server.random.NextDouble() <
            practiceBotDifficulty.DirectionChangeChance
        )
            practiceBotStrafeDirection *= -1;
        practiceBotNextMovementAdjustment = now;
    }

    public void OnDuelMatchFinished(DuelMatch match)
    {
        if (
            practiceBotName != null &&
            (string.Equals(
                match.Player1,
                practiceBotName,
                StringComparison.OrdinalIgnoreCase
            ) ||
            string.Equals(
                match.Player2,
                practiceBotName,
                StringComparison.OrdinalIgnoreCase
            ))
        )
        {
            RemovePracticeBot();
        }
    }

    public bool IsPracticeBot(CBaseEntity entity) =>
        entity is CCitadelPlayerPawn pawn &&
        practiceBotControllerHandle is uint botHandle &&
        pawn.Controller?.EntityHandle == botHandle;

    public bool TryGetActiveMatch(
        CBasePlayerController player,
        out DuelMatch match
    )
    {
        if (server.ActiveMatches.TryGetValue(player.PlayerName, out match!))
            return true;

        if (
            practiceBotControllerHandle == player.EntityHandle &&
            practiceBotName != null &&
            server.ActiveMatches.TryGetValue(practiceBotName, out match!)
        )
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

        var exactMatches = matches
            .Where(match =>
                string.Equals(
                    match.Player1,
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                string.Equals(
                    match.Player2,
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        var matchingMatches = exactMatches.Length > 0
            ? exactMatches
            : matches
                .Where(match =>
                    match.Player1.StartsWith(
                        targetName,
                        StringComparison.OrdinalIgnoreCase
                    ) ||
                    match.Player2.StartsWith(
                        targetName,
                        StringComparison.OrdinalIgnoreCase
                    )
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
        float radius = GetArenaSphereRadius(
            FindArenaIndex(match.ArenaPosition)
        );
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
        int retriesRemaining = 20
    )
    {
        var player = Players.GetAll().FirstOrDefault(controller =>
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
                ReturnSpectatorToSpawn(steamId, retriesRemaining - 1));
            return;
        }

        pawn.Teleport(aimLOCK.MidtownSpawnPosition, null, null);
        PlayMatchTeleportEffect(pawn);
        PlayRespawnTeleportEffect(pawn);
        server.UIManager.RestoreServerInfoAfterSpawn(player);
        Chat.PrintToChat(player, "The match ended; you have been returned to spawn.");
    }

    public string GetMatchParticipantName(CBasePlayerController player) =>
        practiceBotControllerHandle == player.EntityHandle &&
        practiceBotName != null
            ? practiceBotName
            : player.PlayerName;

    public string? GetPracticeBotDisplayName(
        CBasePlayerController player
    ) =>
        practiceBotControllerHandle == player.EntityHandle
            ? practiceBotName
            : null;

    public CCitadelPlayerController? FindMatchParticipant(
        string participantName
    )
    {
        var players = Players.GetAll();
        if (
            practiceBotControllerHandle is uint botHandle &&
            string.Equals(
                participantName,
                practiceBotName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            var bot = players.FirstOrDefault(player =>
                player.EntityHandle == botHandle
            );
            if (bot != null)
                return bot;
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
        var player1 = FindMatchParticipant(match.Player1);
        var player2 = FindMatchParticipant(match.Player2);

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

    public void ApplyDuelBulletLifesteal(
        CCitadelPlayerPawn pawn
    )
    {
        if (pawn.IsBot)
        {
            return;
        }

        if (pawn.AbilityComponent?.FindAbilityByName("upgrade_vampire") == null)
        {
            pawn.AddItem("upgrade_vampire");
        }

        pawn.RemoveModifier("upgrade_vampire/modifier_intrinsic_base");
        pawn.AddModifier(
            "upgrade_vampire/modifier_intrinsic_base",
            abilityValues: new()
            {
                ["BulletLifestealPercent"] = DuelBulletLifestealPercent,
            }
        );
    }

    public void RestoreDuelBulletLifesteal(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch
    )
    {
        pawn.RemoveModifier("upgrade_vampire/modifier_intrinsic_base");
        if (hadItemBeforeMatch)
        {
            pawn.AddModifier(
                "upgrade_vampire/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BulletLifestealPercent"] = DefaultBulletLifestealPercent,
                }
            );
            return;
        }

        pawn.RemoveItem("upgrade_vampire");
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

    public void ApplyDuelHealthItem(CCitadelPlayerPawn pawn)
    {
        if (
            pawn.AbilityComponent?.FindAbilityByName("upgrade_health") == null
        )
        {
            pawn.AddItem("upgrade_health");
        }
    }

    public void RestoreDuelHealthItem(
        CCitadelPlayerPawn pawn,
        bool hadItemBeforeMatch
    )
    {
        pawn.RemoveModifier("upgrade_health/modifier_intrinsic_base");
        if (hadItemBeforeMatch)
        {
            pawn.AddModifier(
                "upgrade_health/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BonusHealth"] = DefaultUpgradeHealthBonus,
                    ["BaseAttackDamagePercent"] =
                        DefaultUpgradeHealthAttackDamagePercent,
                }
            );
            return;
        }

        pawn.RemoveItem("upgrade_health");
    }

    public void ProcessPracticeBotUsercmds(ProcessUsercmdsEvent args)
    {
        if (
            practiceBotName == null ||
            practiceBotControllerHandle is not uint botHandle ||
            args.Paused ||
            args.Controller is not { IsBot: true } controller ||
            controller.EntityHandle != botHandle
        )
        {
            return;
        }

        var pawn = controller.GetHeroPawn();
        if (pawn is not { IsValid: true })
            return;

        var target = Players.GetAll().FirstOrDefault(player =>
            !player.IsBot &&
            string.Equals(
                player.PlayerName,
                practiceBotTargetName,
                StringComparison.OrdinalIgnoreCase
            )
        );
        var targetPawn = target?.GetHeroPawn();
        if (targetPawn is not { IsValid: true })
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        UpdatePracticeBotAim(now, targetPawn);
        var moveTo = UpdatePracticeBotDuelMovement(
            pawn,
            targetPawn,
            now
        );
        bool canAttack =
            server.ActiveMatches.TryGetValue(practiceBotName, out var match) &&
            !match.Finished &&
            match.ArenaInitialized &&
            !match.CountdownActive &&
            match.CombatActive;

        foreach (var usercmd in args.Usercmds)
        {
            Bots.SetMoveInput(
                usercmd,
                pawn,
                moveTo,
                practiceBotLookAt,
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

    public void EnforceDuelArenaBounds(ProcessUsercmdsEvent args)
    {
        if (
            args.Paused ||
            args.Controller is not { IsValid: true } controller
        )
        {
            return;
        }

        var pawn = controller.GetHeroPawn();
        if (pawn is not { IsValid: true })
            return;

        if (TryGetActiveMatch(controller, out var match))
        {
            duelArenaBoundaryStates.Remove(GetMatchParticipantName(controller));
            if (match.Finished || !match.ArenaInitialized)
                return;

            EnforceSphereBoundary(
                pawn,
                match.ArenaPosition,
                isInside: true,
                GetArenaSphereRadius(FindArenaIndex(match.ArenaPosition))
            );
            return;
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

            float arenaSphereRadius = GetArenaSphereRadius(nearestArenaIndex);
            float detectionRadius = arenaSphereRadius + DuelArenaPlayerMargin;
            if (
                nearestArenaIndex < 0 ||
                nearestDistanceSquared > detectionRadius * detectionRadius
            )
                return;

            float interiorRadius = arenaSphereRadius - DuelArenaPlayerMargin;
            boundaryState = (
                nearestArenaIndex,
                nearestDistanceSquared <= interiorRadius * interiorRadius
            );
            duelArenaBoundaryStates[controller.PlayerName] = boundaryState;
        }
        else if (
            Vector3.DistanceSquared(
                position,
                GetDuelArenaSphereCenter(
                    server.DuelLocations[boundaryState.ArenaIndex]
                )
            ) > MathF.Pow(
                GetArenaSphereRadius(boundaryState.ArenaIndex) +
                    DuelArenaPlayerMargin,
                2f
            )
        )
        {
            duelArenaBoundaryStates.Remove(controller.PlayerName);
            return;
        }

        EnforceSphereBoundary(
            pawn,
            server.DuelLocations[boundaryState.ArenaIndex],
            boundaryState.IsInside,
            GetArenaSphereRadius(boundaryState.ArenaIndex)
        );
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

        float arenaSphereRadius = GetArenaSphereRadius(nearestArenaIndex);
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
            float radius =
                GetArenaSphereRadius(arenaIndex) - DuelArenaPlayerMargin;
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

    private float GetArenaSphereRadius(int arenaIndex) =>
        DuelArenaSphereRadius - (arenaIndex == 2 ? 40f : 0f);

    private int FindArenaIndex(Vector3 arenaPosition)
    {
        for (int i = 0; i < server.DuelLocations.Length; i++)
        {
            if (Vector3.DistanceSquared(server.DuelLocations[i], arenaPosition) < 0.01f)
                return i;
        }

        return -1;
    }

    private Vector3 UpdatePracticeBotDuelMovement(
        CCitadelPlayerPawn botPawn,
        CCitadelPlayerPawn opponentPawn,
        DateTimeOffset now
    )
    {
        var botPosition = botPawn.Position;
        var opponentPosition = opponentPawn.Position;
        float playerRadius = DuelArenaSphereRadius - DuelArenaPlayerMargin;
        Vector3? arenaPosition = null;
        if (
            botPawn.Controller is { } controller &&
            TryGetActiveMatch(controller, out var match)
        )
        {
            arenaPosition = match.ArenaPosition;
            playerRadius =
                GetArenaSphereRadius(FindArenaIndex(match.ArenaPosition)) -
                DuelArenaPlayerMargin;
        }

        Vector2 target = CorrectPracticeBotStrafeRange(
            new Vector2(practiceBotStrafeTarget.X, practiceBotStrafeTarget.Y),
            new Vector2(botPosition.X, botPosition.Y),
            new Vector2(opponentPosition.X, opponentPosition.Y),
            playerRadius
        );
        if (arenaPosition is Vector3 nextArena)
        {
            target = KeepPracticeBotTargetInsideArena(
                target,
                botPosition,
                nextArena,
                playerRadius
            );
        }

        if (
            now >= practiceBotNextMovementAdjustment ||
            Vector2.DistanceSquared(
                new Vector2(botPosition.X, botPosition.Y),
                target
            ) <= practiceBotDifficulty.ArrivalDistance *
                practiceBotDifficulty.ArrivalDistance
        )
        {
            ChooseNextPracticeBotMovement(
                botPosition,
                opponentPosition,
                now,
                playerRadius,
                arenaPosition
            );
            target = CorrectPracticeBotStrafeRange(
                new Vector2(
                    practiceBotStrafeTarget.X,
                    practiceBotStrafeTarget.Y
                ),
                new Vector2(botPosition.X, botPosition.Y),
                new Vector2(opponentPosition.X, opponentPosition.Y),
                playerRadius
            );
            if (arenaPosition is Vector3 activeArena)
            {
                target = KeepPracticeBotTargetInsideArena(
                    target,
                    botPosition,
                    activeArena,
                    playerRadius
                );
            }
        }

        return new Vector3(target.X, target.Y, botPosition.Z);
    }

    private void ChooseNextPracticeBotMovement(
        Vector3 botPosition,
        Vector3 opponentPosition,
        DateTimeOffset now,
        float playerRadius,
        Vector3? arenaPosition
    )
    {
        practiceBotStrafeDirection =
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
            practiceBotDifficulty.StrafeDurationMinimum,
            practiceBotDifficulty.StrafeDurationMaximum
        );
        float stepDistance = Math.Clamp(
            practiceBotDifficulty.StrafeSpeed *
                (float)RandomRange(
                    practiceBotDifficulty.StepFactorMinimum,
                    practiceBotDifficulty.StepFactorMaximum
                ) *
                duration,
            practiceBotDifficulty.StepDistanceMinimum,
            practiceBotDifficulty.StepDistanceMaximum
        );
        Vector2 target = new Vector2(botPosition.X, botPosition.Y)
            + right * practiceBotStrafeDirection * stepDistance;
        target = CorrectPracticeBotStrafeRange(
            target,
            new Vector2(botPosition.X, botPosition.Y),
            new Vector2(opponentPosition.X, opponentPosition.Y),
            playerRadius
        );
        if (arenaPosition is Vector3 activeArena)
        {
            target = KeepPracticeBotTargetInsideArena(
                target,
                botPosition,
                activeArena,
                playerRadius
            );
        }

        practiceBotStrafeTarget = new Vector3(
            target.X,
            target.Y,
            botPosition.Z
        );
        practiceBotNextMovementAdjustment = now.AddSeconds(duration);
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
        float horizontalRadius = MathF.Sqrt(MathF.Max(
            0f,
            safeRadius * safeRadius - verticalOffset * verticalOffset
        ));
        Vector2 center = new(arenaCenter.X, arenaCenter.Y);
        Vector2 offset = target - center;
        float distance = offset.Length();
        if (distance <= horizontalRadius)
            return target;

        Vector2 direction = distance > 0.001f
            ? offset / distance
            : Vector2.UnitX;
        return center + direction * horizontalRadius;
    }

    private static Vector2 CorrectPracticeBotStrafeRange(
        Vector2 moveTarget,
        Vector2 botPosition,
        Vector2 opponentPosition,
        float playerRadius
    )
    {
        float courtCenterOffset = DuelArenaCourtHeight / 2f;
        float maxHorizontalRadius = MathF.Sqrt(MathF.Max(
            0f,
            playerRadius * playerRadius - courtCenterOffset * courtCenterOffset
        ));
        float maximumPossibleRange = 2f * maxHorizontalRadius;
        float minimumRange = maximumPossibleRange >= PracticeBotDuelMinRange
            ? PracticeBotDuelMinRange
            : maximumPossibleRange * 0.7f;
        float maximumRange = Math.Min(PracticeBotDuelMaxRange, maximumPossibleRange);
        float currentRange = Vector2.Distance(botPosition, opponentPosition);
        if (
            currentRange >= minimumRange &&
            currentRange <= maximumRange
        )
        {
            return moveTarget;
        }

        float targetRange = currentRange < minimumRange
            ? minimumRange
            : maximumRange;
        Vector2 targetOffset = moveTarget - opponentPosition;
        if (targetOffset.LengthSquared() < 0.001f)
            targetOffset = botPosition - opponentPosition;
        if (targetOffset.LengthSquared() < 0.001f)
            targetOffset = Vector2.UnitX;

        return opponentPosition +
            Vector2.Normalize(targetOffset) * targetRange;
    }

    private void UpdatePracticeBotAim(
        DateTimeOffset now,
        CCitadelPlayerPawn targetPawn
    )
    {
        if (now >= practiceBotNextAimUpdate)
        {
            practiceBotNextAimUpdate =
                now.AddSeconds(practiceBotDifficulty.AimUpdateInterval);

            float errorX = (float)(server.random.NextDouble() * 2d - 1d)
                * practiceBotDifficulty.HorizontalAimError;
            float errorY = (float)(server.random.NextDouble() * 2d - 1d)
                * practiceBotDifficulty.HorizontalAimError;
            float errorZ = (float)(server.random.NextDouble() * 2d - 1d)
                * practiceBotDifficulty.VerticalAimError;
            practiceBotAimError = new Vector3(errorX, errorY, errorZ);
        }

        practiceBotLookAt = targetPawn.EyePosition + practiceBotAimError;
    }

    private double RandomRange(double minimum, double maximum) =>
        minimum + server.random.NextDouble() * (maximum - minimum);

    private void RemovePracticeBot()
    {
        if (practiceBotName == null)
            return;

        var bot = Players.GetAll().FirstOrDefault(player =>
            player.IsBot &&
            (
                player.EntityHandle == practiceBotControllerHandle ||
                string.Equals(
                    player.PlayerName,
                    practiceBotName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        );
        bot?.Kick(
            "Practice bot removed.",
            ENetworkDisconnectionReason.NetworkDisconnectKicked
        );

        if (practiceBotPreviousAiEnabled)
            Bots.SetAiEnabled(true);

        practiceBotName = null;
        practiceBotBaseName = null;
        practiceBotTargetName = null;
        practiceBotControllerHandle = null;
        practiceBotPreviousAiEnabled = false;
        practiceBotLookAt = Vector3.Zero;
        practiceBotAimError = Vector3.Zero;
        practiceBotStrafeTarget = Vector3.Zero;
        practiceBotStrafeDirection = 1;
        practiceBotNextMovementAdjustment = DateTimeOffset.MinValue;
        practiceBotNextAimUpdate = DateTimeOffset.MinValue;
    }

    public void ApplyDuelMatchModifiers(
        CCitadelPlayerPawn pawn,
        int sharedMaxHealth
    )
    {
        ApplyDuelHealthItem(pawn);
        ApplyDuelGlassCannon(pawn);
        NormalizeDuelPawnHealth(pawn, sharedMaxHealth, force: true);
    }

    private void NormalizeDuelPawnHealth(
        CCitadelPlayerPawn pawn,
        int sharedMaxHealth,
        bool force = false
    )
    {
        if (!force && pawn.GetMaxHealth() == sharedMaxHealth)
            return;

        pawn.RemoveModifier("upgrade_health/modifier_intrinsic_base");
        float bonusHealth = sharedMaxHealth - pawn.GetMaxHealth();
        pawn.AddModifier(
            "upgrade_health/modifier_intrinsic_base",
            abilityValues: new()
            {
                ["BonusHealth"] = bonusHealth,
                ["BaseAttackDamagePercent"] =
                    DefaultUpgradeHealthAttackDamagePercent,
                ["BonusHealthRegen"] = 0f
            }
        );
    }

    public void ApplyDuelGlassCannon(CCitadelPlayerPawn pawn)
    {
        pawn.RemoveModifier(
            "upgrade_glass_cannon/modifier_glass_cannon_intrinstic"
        );
        pawn.AddModifier(
            "upgrade_glass_cannon/modifier_glass_cannon_intrinstic",
            new System.Collections.Generic.Dictionary<string, float>
            {
                ["MaxHealthLossPercent"] =
                    aimLOCK.DUEL_GLASS_CANNON_HEALTH_LOSS,
                ["BaseAttackDamagePercent"] =
                    aimLOCK.DUEL_GLASS_CANNON_DAMAGE
            }
        );
    }

    private void NormalizeDuelMatchHealth(DuelMatch match)
    {
        if (match.Finished)
            return;

        var player1 = FindMatchParticipant(match.Player1);
        var player2 = FindMatchParticipant(match.Player2);

        var pawn1 = player1?.GetHeroPawn();
        var pawn2 = player2?.GetHeroPawn();
        if (pawn1 is { IsValid: true })
            NormalizeDuelPawnHealth(pawn1, match.SharedMaxHealth);
        if (pawn2 is { IsValid: true })
            NormalizeDuelPawnHealth(pawn2, match.SharedMaxHealth);
    }

    public void PlayRespawnTeleportEffect(CCitadelPlayerPawn pawn)
    {
        SpawnAttachedEffect(pawn, RespawnTeleportEffect);
    }

    public CParticleSystem? BeginRespawnEffect(CCitadelPlayerPawn pawn) =>
        CreateAttachedEffect(pawn, RespawnTeleportEffect);

    public void EndRespawnEffect(CParticleSystem? effect)
    {
        if (effect is not { IsValid: true })
            return;

        effect.Stop();
        server.ScheduleOnce(1.Seconds(), () =>
        {
            if (effect.IsValid)
                effect.Destroy();
        });
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
        initialHeroPawnInitializationHandles.Remove(controllerHandle);
        playersInsidePracticeBotZones.Remove(controller.PlayerName);
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
        RecoverInitialSpawns();
    }

    private void RecoverInitialSpawns()
    {
        foreach (var controller in Players.GetAll().Where(player => !player.IsBot).ToArray())
        {
            if (!controller.IsValid)
                continue;

            var pawn = controller.GetHeroPawn();
            if (pawn is { IsValid: true, IsAlive: true })
                continue;

            MarkInitialHeroSelectionPending(controller);
            BeginInitialHazeSpawn(controller);
        }

        server.ScheduleOnce(1.Seconds(), RecoverInitialSpawns);
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
            server.UIManager.UpdateMatchHud(match);

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

            NormalizeDuelMatchHealth(match);
            ResetDuelCurrentHealth(match);
        });

        string participantName = GetMatchParticipantName(victim);
        BeginMatchRespawnRecovery(match, participantName);

        return HookResult.Continue;
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
        int retriesRemaining = 20
    )
    {
        if (match.Finished)
        {
            respawnRecoveryInProgress.Remove(playerName);
            return;
        }

        var player = FindMatchParticipant(playerName);

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
                RespawnIntoArena(match, playerName, retriesRemaining - 1));
            return;
        }

        SetMatchDisarmed(match, true);

        Vector3 arenaCenter = GetDuelArenaSphereCenter(match.ArenaPosition);
        ApplyDuelGlassCannon(pawn);
        NormalizeDuelMatchHealth(match);
        ApplyDuelBulletLifesteal(pawn);
        ApplyDuelBlitzBullets(pawn);

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
            PlayRespawnTeleportEffect(pawn);
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
            PlayRespawnTeleportEffect(pawn);
        }

        pawn.Health = pawn.GetMaxHealth();
        server.PendingRespawns.Remove(playerName);
        respawnRecoveryInProgress.Remove(playerName);

        server.ScheduleOnce(0.5.Seconds(), () =>
        {
            server.Queue.StartCountdown(match);
        });
    }

    private void ResetDuelCurrentHealth(DuelMatch match)
    {
        foreach (var player in GetMatchParticipants(match))
        {
            var pawn = player.GetHeroPawn();
            if (pawn is { IsValid: true, IsAlive: true })
                pawn.Health = pawn.GetMaxHealth();
        }
    }

    private void FinishMatch(
        DuelMatch match,
        string? respawnEffectPlayer = null,
        CParticleSystem? startedRespawnEffect = null
    )
    {
        if (!match.Finished)
            match.Finished = true;
        match.CountdownActive = false;
        match.CountdownGeneration++;
        SetMatchDisarmed(match, true);

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

        var winnerController = FindMatchParticipant(winner);

        string loserName = StringComparer.OrdinalIgnoreCase.Equals(
            winner,
            match.Player1
        )
            ? match.Player2
            : match.Player1;
        var loserController = FindMatchParticipant(loserName);

        if (winnerController != null && loserController != null)
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

        Console.WriteLine($"Match Over! Winner: {winner}");

        var player1 = FindMatchParticipant(match.Player1);
        var player2 = FindMatchParticipant(match.Player2);

        SetMatchDisarmed(match, false);

        server.ActiveMatches.Remove(match.Player1);
        server.ActiveMatches.Remove(match.Player2);
        server.PendingRespawns.Remove(match.Player1);
        server.PendingRespawns.Remove(match.Player2);

        var pawn1 = player1?.GetHeroPawn();
        var pawn2 = player2?.GetHeroPawn();
        if (pawn1 != null)
        {
            server.PlayerManager.RestoreDuelHealthItem(
                pawn1,
                match.Player1HadHealthItem
            );
        }
        if (pawn2 != null)
        {
            server.PlayerManager.RestoreDuelHealthItem(
                pawn2,
                match.Player2HadHealthItem
            );
        }
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
                "upgrade_infinite_rounds",
                match.Player1HadInfiniteRoundsItem
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
        }
        if (pawn2 != null)
        {
            server.PlayerManager.RemoveDuelItemIfGranted(
                pawn2,
                "upgrade_infinite_rounds",
                match.Player2HadInfiniteRoundsItem
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
        }
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
        {
            server.PlayerManager.PlayMatchTeleportEffect(pawn1);
            if (
                respawnEffectPlayer == match.Player1 &&
                startedRespawnEffect is { IsValid: true }
            )
            {
                server.ScheduleOnce(1.Seconds(), () =>
                    server.PlayerManager.EndRespawnEffect(startedRespawnEffect));
            }
            else
                server.PlayerManager.PlayRespawnTeleportEffect(pawn1);
        }
        if (pawn2 != null)
        {
            server.PlayerManager.PlayMatchTeleportEffect(pawn2);
            if (
                respawnEffectPlayer == match.Player2 &&
                startedRespawnEffect is { IsValid: true }
            )
            {
                server.ScheduleOnce(1.Seconds(), () =>
                    server.PlayerManager.EndRespawnEffect(startedRespawnEffect));
            }
            else
                server.PlayerManager.PlayRespawnTeleportEffect(pawn2);
        }

        player1?.ChangeTeam(2);
        player2?.ChangeTeam(2);

        server.PlayerManager.OnDuelMatchFinished(match);
        server.UIManager.RestoreHudAfterMatch(match);
        ReturnMatchSpectators(match);
        server.Queue.TryStartMatches();
    }

    public bool ForfeitMatch(
        string playerName,
        CParticleSystem? startedRespawnEffect = null
    )
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
        FinishMatch(match, playerName, startedRespawnEffect);
        return true;
    }

    public void SetMatchDisarmed(DuelMatch match, bool disarmed)
    {
        match.CombatActive = !disarmed && !match.Finished;

        foreach (var player in GetMatchParticipants(match))
        {
            var pawn = player.GetHeroPawn();
            if (pawn == null)
                continue;

            if (disarmed)
                pawn.AddModifier("modifier_citadel_disarmed");
            else
                pawn.RemoveModifier("modifier_citadel_disarmed");
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

    public TimeZoneInfo GetTimeZone(ulong steamId) =>
        ResolveIanaTimeZone(GetTimeZoneId(steamId));

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
            const string recordFile = "aimlock_records.json";
            if (!File.Exists(recordFile))
                return;

            string json = File.ReadAllText(recordFile);
            var loaded = JsonSerializer.Deserialize<Dictionary<ulong, PlayerRecord>>(json);

            if (loaded == null)
                return;

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
            string json = JsonSerializer.Serialize(
                server.PlayerRecords,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText("aimlock_records.json", json);
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
