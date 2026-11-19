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
    // Keep the test bot's Elo entry separate from real Steam accounts.
    private const ulong BotRankedEloId = ulong.MaxValue;

    private const float MinPracticeBotStrafeSpeed = 100f;
    private const float MaxPracticeBotStrafeSpeed = 2500f;
    private const float PracticeBotDuelMinRange = 160f;
    private const float PracticeBotDuelMaxRange = 200f;
    private const float PracticeBotArenaBoundaryMargin = 64f;

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

    private readonly Dictionary<uint, PracticeBotState> practiceBots = new();
    private readonly Dictionary<uint, float> practiceBotStrafeSpeedOverrides =
        new();

    private bool practiceBotsPreviousAiEnabled;

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

        var request = new BotMatchRequest(
            player,
            player.EntityHandle,
            arenaIndex,
            difficulty,
            selectedProfile,
            reply
        );
        server.ScheduleOnce(
            PracticeBotSpawnDelaySeconds.Seconds(),
            () => SpawnBotForMatch(request)
        );
    }

    private sealed record BotMatchRequest(
        CCitadelPlayerController Player,
        uint PlayerHandle,
        int ArenaIndex,
        string? Difficulty,
        PracticeBotDifficultyProfile Profile,
        Action<string> Reply
    );

    private bool IsInMatchOrSpectating(CCitadelPlayerController player) =>
        server.ActiveMatches.ContainsKey(GetMatchParticipantName(player)) ||
        IsSpectating(player);

    /* Step 2: the player is in the arena, create the bot next to them */
    private void SpawnBotForMatch(BotMatchRequest request)
    {
        var player = request.Player;
        var currentPawn = player.IsValid ? player.GetHeroPawn() : null;
        if (
            currentPawn is not { IsValid: true, IsAlive: true } ||
            IsInMatchOrSpectating(player)
        )
        {
            ReleasePendingBotMatch(request.PlayerHandle, request.ArenaIndex);
            return;
        }

        var botController = CreatePracticeBot(
            player,
            currentPawn,
            server.DuelLocations[request.ArenaIndex],
            request.Profile,
            request.Reply
        );
        if (botController == null)
        {
            ReleasePendingBotMatch(request.PlayerHandle, request.ArenaIndex);
            return;
        }

        // Creating the bot is the heaviest frame of a bot match: the game builds the
        // bot, then loads its hero again a few frames later. The match setup (items,
        // health, Fixation for both players) waits until that hero load is done, so
        // the work lands on separate frames and is done once instead of twice.
        uint botHandle = botController.EntityHandle;
        pendingBotReservations[botHandle] = (request.PlayerHandle, request.ArenaIndex);
        pendingBotMatchStarts[botHandle] =
            () => StartMatchWithBot(request, botController, botHandle);

        // Fallback in case the hero-loaded hook never fires for the bot.
        server.ScheduleOnce(
            PendingBotMatchTimeoutSeconds.Seconds(),
            () => RunPendingBotMatchStart(botHandle)
        );
    }

    /* Step 3: the bot's hero has loaded, start the match */
    private void StartMatchWithBot(
        BotMatchRequest request,
        CCitadelPlayerController botController,
        uint botHandle
    )
    {
        var player = request.Player;
        ReleasePendingBotMatch(request.PlayerHandle, request.ArenaIndex);
        if (
            !player.IsValid ||
            !botController.IsValid ||
            IsInMatchOrSpectating(player)
        )
        {
            RemovePracticeBot(botHandle);
            return;
        }

        // Named only now: the bot's hero reload resets its name.
        SetPracticeBotDifficultyName(botController, request.Difficulty);
        if (!server.Queue.StartBotMatch(
            player,
            botController,
            server.DuelLocations[request.ArenaIndex],
            request.Reply,
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

    public bool IsPracticeBotController(CBasePlayerController? controller) =>
        controller != null &&
        practiceBots.Count != 0 &&
        practiceBots.ContainsKey(controller.EntityHandle);

    public bool IsPracticeBot(CBaseEntity entity) =>
        entity is CCitadelPlayerPawn pawn &&
        pawn.Controller is { } controller &&
        practiceBots.ContainsKey(controller.EntityHandle);

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

    public bool BlockPracticeBotDamage(TakeDamageEvent args)
    {
        if (IsPracticeBot(args.Entity))
            return true;

        var attackerPawn = args.Info.Attacker as CCitadelPlayerPawn;
        return attackerPawn?.Controller is { } attacker &&
            IsSpectating(attacker);
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

    public void ResetPracticeBotRecord(CCitadelPlayerController bot)
    {
        ulong steamId = bot.PlayerSteamId;
        if (!server.PlayerRecords.Remove(steamId, out var record))
            return;

        if (!SaveRecords())
            server.PlayerRecords[steamId] = record;
    }
}
