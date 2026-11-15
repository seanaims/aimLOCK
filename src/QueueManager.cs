/*
this file is for the queue manager
it parses the various commands, starting matches, finding other players, and setting up the matches
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;
using DeadworksManaged.Api.Sounds;

namespace aimLOCK;

public class QueueManager
{
    private static readonly TimeSpan DuelRequestLifetime =
        TimeSpan.FromSeconds(60);

    private readonly aimLOCK server;
    private readonly Dictionary<uint, DuelRequest> pendingDuelRequests = new();

    private sealed record DuelRequest(
        CCitadelPlayerController Initiator,
        CCitadelPlayerController Target,
        int? ArenaIndex,
        DateTimeOffset ExpiresAt
    );

    public QueueManager(aimLOCK server)
    {
        this.server = server;
    }

    public void JoinQueue(Caller caller, MatchQueueType queueType)
    {
        var controller = caller.Player;

        if (controller == null)
        {
            caller.Reply("You must be a player to join the queue.");
            return;
        }

        if (server.PlayerManager.IsSpectating(controller))
        {
            caller.Reply("Leave spectating before joining a queue.");
            return;
        }

        if (server.PlayerManager.TryGetActiveMatch(controller, out _))
        {
            caller.Reply("You are already in a match.");
            return;
        }

        bool alreadyQueued = server.RankedQueue
            .Concat(server.UnrankedQueue)
            .Any(x => x.SteamId == controller.PlayerSteamId);

        if (alreadyQueued)
        {
            caller.Reply("You are already in a queue. Use /leave first.");
            return;
        }

        var queue = GetQueue(queueType);
        queue.Add(new QueueEntry(controller.PlayerSteamId));

        server.UIManager.RefreshHud();

        TryStartMatch(queueType);

        caller.Reply($"You have joined the {queueType.ToString().ToLowerInvariant()} queue.");

        Sounds.Play(
            "Damage.Send.Crit",
            RecipientFilter.Single(controller.Slot)
        );

        Console.WriteLine(
            $"{controller.PlayerName} has joined the {queueType} queue."
        );
    }

    public void LeaveQueue(Caller caller)
    {
        var controller = caller.Player;

        if (controller == null)
            return;

        var player = server.RankedQueue.FirstOrDefault(x =>
            x.SteamId == controller.PlayerSteamId);

        if (player != null)
        {
            server.RankedQueue.Remove(player);
        }
        else
        {
            player = server.UnrankedQueue.FirstOrDefault(x =>
                x.SteamId == controller.PlayerSteamId);
            if (player == null)
            {
                caller.Reply("You are not currently in a queue.");
                return;
            }

            server.UnrankedQueue.Remove(player);
        }

        server.UIManager.RefreshHud();

        caller.Reply("You have left the queue.");

        Console.WriteLine(
            $"{controller.PlayerName} has left the queue."
        );
    }

    public bool StartBotMatch(
        Caller caller,
        CCitadelPlayerController bot,
        Vector3 arena
    )
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to start a bot match.");
            return false;
        }

        return StartBotMatch(
            player,
            bot,
            arena,
            message => caller.Reply(message)
        );
    }

    public bool StartBotMatch(
        CCitadelPlayerController player,
        CCitadelPlayerController bot,
        Vector3 arena,
        Action<string> reply
    )
    {
        if (server.PlayerManager.TryGetActiveMatch(player, out _))
        {
            reply("You are already in a match.");
            return false;
        }

        if (
            server.ActiveMatches.ContainsKey(
                server.PlayerManager.GetMatchParticipantName(bot)
            )
        )
        {
            reply("The practice bot is already in a match.");
            return false;
        }

        bool alreadyQueued = server.RankedQueue
            .Concat(server.UnrankedQueue)
            .Any(entry => entry.SteamId == player.PlayerSteamId);
        if (alreadyQueued)
        {
            reply("Leave your current queue with /leave before starting a bot match.");
            return false;
        }

        int arenaIndex = Array.IndexOf(server.DuelLocations, arena);
        if (!IsArenaAvailable(arenaIndex))
        {
            reply("All duel arenas are currently in use.");
            return false;
        }

        if (!StartMatch(player, bot, MatchQueueType.Unranked, arenaIndex))
            return false;

        reply(
            $"Starting Bot ({server.PlayerManager.GetPracticeBotDifficultyName(bot)}) match..."
        );
        return true;
    }

    public bool RequestDuelMatch(
        CCitadelPlayerController initiator,
        string targetName,
        int? arenaIndex,
        Action<string> reply
    )
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            reply("Use /duel <player name> [arena name].");
            return false;
        }

        if (string.Equals(
            initiator.PlayerName,
            targetName,
            StringComparison.OrdinalIgnoreCase
        ))
        {
            reply("You cannot duel yourself.");
            return false;
        }

        var onlinePlayers = Players.GetAll()
            .Where(player =>
                !player.IsBot &&
                player.EntityHandle != initiator.EntityHandle
            )
            .ToArray();
        var exactMatches = onlinePlayers
            .Where(player => string.Equals(
                player.PlayerName,
                targetName,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
        var matches = exactMatches.Length > 0
            ? exactMatches
            : onlinePlayers
                .Where(player => player.PlayerName.StartsWith(
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                ))
                .ToArray();

        if (matches.Length == 0)
        {
            reply($"No online player matches \"{targetName}\".");
            return false;
        }

        if (matches.Length > 1)
        {
            reply("That player name is ambiguous; use the full player name.");
            return false;
        }

        var target = matches[0];
        if (!ValidateDuelParticipants(initiator, target, reply))
            return false;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        PruneExpiredDuelRequests(now);
        if (pendingDuelRequests.Values.Any(request =>
            request.Initiator.EntityHandle == initiator.EntityHandle))
        {
            reply("You already have a pending duel request.");
            return false;
        }

        if (pendingDuelRequests.TryGetValue(target.EntityHandle, out var existing))
        {
            reply(
                $"{target.PlayerName} already has a pending duel request from {existing.Initiator.PlayerName}."
            );
            return false;
        }

        pendingDuelRequests[target.EntityHandle] = new DuelRequest(
            initiator,
            target,
            arenaIndex,
            now + DuelRequestLifetime
        );
        string arenaMessage = arenaIndex is int selectedArena
            ? $" on arena {selectedArena + 1} ({aimLOCK.DuelArenaNames[selectedArena]})"
            : "";
        reply($"Duel request sent to {target.PlayerName}{arenaMessage}.");
        Chat.PrintToChat(
            target,
            $"{initiator.PlayerName} challenged you to a duel{arenaMessage}. Use /accept within 60 seconds."
        );
        return true;
    }

    public bool AcceptDuelRequest(
        CCitadelPlayerController target,
        Action<string> reply
    )
    {
        if (!pendingDuelRequests.TryGetValue(target.EntityHandle, out var request))
        {
            reply("You do not have a pending duel request.");
            return false;
        }

        if (request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            pendingDuelRequests.Remove(target.EntityHandle);
            reply("That duel request has expired. Ask them to send another.");
            return false;
        }

        if (
            !request.Initiator.IsValid ||
            !request.Target.IsValid ||
            request.Target.EntityHandle != target.EntityHandle
        )
        {
            pendingDuelRequests.Remove(target.EntityHandle);
            reply("That duel request is no longer valid.");
            return false;
        }

        if (!ValidateDuelParticipants(request.Initiator, target, reply))
        {
            pendingDuelRequests.Remove(target.EntityHandle);
            return false;
        }

        int arenaIndex;
        if (request.ArenaIndex is int requestedArena)
        {
            if (!IsArenaAvailable(requestedArena))
            {
                reply(
                    $"Arena {requestedArena + 1} ({aimLOCK.DuelArenaNames[requestedArena]}) is currently in use. Try again when it is free."
                );
                return false;
            }

            arenaIndex = requestedArena;
        }
        else if (!TryGetAvailableArena(out arenaIndex))
        {
            reply("All duel arenas are currently in use. Try again when one is free.");
            return false;
        }

        if (!StartMatch(
            request.Initiator,
            target,
            MatchQueueType.Unranked,
            arenaIndex
        ))
            return false;

        pendingDuelRequests.Remove(target.EntityHandle);
        string arenaMessage =
            $" on arena {arenaIndex + 1} ({aimLOCK.DuelArenaNames[arenaIndex]})";
        reply(
            $"Accepted {request.Initiator.PlayerName}'s duel request{arenaMessage}. Starting match..."
        );
        Chat.PrintToChat(
            request.Initiator,
            $"{target.PlayerName} accepted your duel request{arenaMessage}. Starting match..."
        );
        return true;
    }

    public void OnPlayerDisconnect(CCitadelPlayerController player)
    {
        uint playerHandle = player.EntityHandle;
        var requestsToRemove = pendingDuelRequests
            .Where(request =>
                request.Key == playerHandle ||
                request.Value.Initiator.EntityHandle == playerHandle
            )
            .Select(request => request.Key)
            .ToArray();

        foreach (uint targetHandle in requestsToRemove)
            pendingDuelRequests.Remove(targetHandle);
    }

    private bool ValidateDuelParticipants(
        CCitadelPlayerController initiator,
        CCitadelPlayerController target,
        Action<string> reply
    )
    {
        if (server.PlayerManager.TryGetActiveMatch(initiator, out _))
        {
            reply($"{initiator.PlayerName} is already in a match.");
            return false;
        }

        if (server.PlayerManager.IsSpectating(initiator))
        {
            reply($"{initiator.PlayerName} must stop spectating before starting a duel.");
            return false;
        }

        if (server.PlayerManager.TryGetActiveMatch(target, out _))
        {
            reply($"{target.PlayerName} is already in a match.");
            return false;
        }

        if (server.PlayerManager.IsSpectating(target))
        {
            reply($"{target.PlayerName} must stop spectating before starting a duel.");
            return false;
        }

        bool eitherQueued = server.RankedQueue
            .Concat(server.UnrankedQueue)
            .Any(entry =>
                entry.SteamId == initiator.PlayerSteamId ||
                entry.SteamId == target.PlayerSteamId
            );
        if (eitherQueued)
        {
            reply("Both players must leave their queues before starting a duel.");
            return false;
        }

        if (
            initiator.GetHeroPawn() is not { IsValid: true, IsAlive: true } ||
            target.GetHeroPawn() is not { IsValid: true, IsAlive: true }
        )
        {
            reply("Both players must have a living hero to start a duel.");
            return false;
        }

        return true;
    }

    private void PruneExpiredDuelRequests(DateTimeOffset now)
    {
        var requestsToRemove = pendingDuelRequests
            .Where(request =>
                request.Value.ExpiresAt <= now ||
                !request.Value.Initiator.IsValid ||
                !request.Value.Target.IsValid
            )
            .Select(request => request.Key)
            .ToArray();

        foreach (uint targetHandle in requestsToRemove)
            pendingDuelRequests.Remove(targetHandle);
    }

    public void TryStartMatches()
    {
        TryStartMatch(MatchQueueType.Ranked);
        TryStartMatch(MatchQueueType.Unranked);
    }

    public void TryStartMatch(MatchQueueType queueType)
    {
        var queue = GetQueue(queueType);

        if (queue.Count < 2 || !TryGetAvailableArena(out int arenaIndex))
            return;

        var playersBySteamId = Players.GetAll()
            .Where(player => !player.IsBot)
            .GroupBy(player => player.PlayerSteamId)
            .ToDictionary(
                group => group.Key,
                group => group.First()
            );
        var queuedPlayers = queue
            .Select((entry, index) => new QueuedPlayer(
                entry,
                index,
                playersBySteamId.TryGetValue(entry.SteamId, out var player)
                    ? player
                    : null
            ))
            .Where(item => item.Player != null)
            .ToArray();

        int selectedFirstIndex = -1;
        int selectedSecondIndex = -1;
        CCitadelPlayerController? player1 = null;
        CCitadelPlayerController? player2 = null;
        DateTimeOffset selectedOldestEntry = DateTimeOffset.MaxValue;
        long selectedPingDifference = long.MaxValue;
        int selectedIndexSum = int.MaxValue;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int first = 0; first < queuedPlayers.Length; first++)
        {
            var firstQueued = queuedPlayers[first];
            for (int second = first + 1; second < queuedPlayers.Length; second++)
            {
                var secondQueued = queuedPlayers[second];
                var firstPlayer = firstQueued.Player!;
                var secondPlayer = secondQueued.Player!;
                long pingDifference = 0;
                bool usePingMatching =
                    server.PlayerManager.IsPingBasedMatchmakingEnabled(firstPlayer) &&
                    server.PlayerManager.IsPingBasedMatchmakingEnabled(secondPlayer);

                if (
                    !firstPlayer.IsBot &&
                    !secondPlayer.IsBot &&
                    usePingMatching
                )
                {
                    DateTimeOffset oldestEntry = firstQueued.Entry.EnqueuedAt
                        <= secondQueued.Entry.EnqueuedAt
                            ? firstQueued.Entry.EnqueuedAt
                            : secondQueued.Entry.EnqueuedAt;
                    int allowedDifference = GetAllowedPingDifference(
                        Math.Max(0d, (now - oldestEntry).TotalSeconds)
                    );

                    int firstPing = 0;
                    int secondPing = 0;
                    bool hasPings =
                        server.PingManager.TryGetPing(firstPlayer, out firstPing) &&
                        server.PingManager.TryGetPing(secondPlayer, out secondPing);
                    if (hasPings)
                    {
                        pingDifference = Math.Abs((long)firstPing - secondPing);
                        if (pingDifference > allowedDifference)
                            continue;
                    }
                    else if (allowedDifference != int.MaxValue)
                    {
                        continue;
                    }
                }

                DateTimeOffset pairOldestEntry = firstQueued.Entry.EnqueuedAt
                    <= secondQueued.Entry.EnqueuedAt
                        ? firstQueued.Entry.EnqueuedAt
                        : secondQueued.Entry.EnqueuedAt;
                int indexSum = firstQueued.QueueIndex + secondQueued.QueueIndex;
                bool isBetter = pairOldestEntry < selectedOldestEntry
                    || (pairOldestEntry == selectedOldestEntry
                        && pingDifference < selectedPingDifference)
                    || (pairOldestEntry == selectedOldestEntry
                        && pingDifference == selectedPingDifference
                        && indexSum < selectedIndexSum);
                if (!isBetter)
                    continue;

                selectedFirstIndex = firstQueued.QueueIndex;
                selectedSecondIndex = secondQueued.QueueIndex;
                player1 = firstPlayer;
                player2 = secondPlayer;
                selectedOldestEntry = pairOldestEntry;
                selectedPingDifference = pingDifference;
                selectedIndexSum = indexSum;
            }
        }

        if (selectedFirstIndex < 0 || player1 == null || player2 == null)
            return;

        if (!StartMatch(player1, player2, queueType, arenaIndex))
            return;

        queue.RemoveAt(selectedSecondIndex);
        queue.RemoveAt(selectedFirstIndex);

        server.UIManager.RefreshHud();
        TryStartMatch(queueType);
    }

    public bool TryGetAvailableArena(out int arenaIndex)
    {
        var occupiedArenas = server.ActiveMatches.Values
            .Select(match => match.ArenaIndex)
            .ToHashSet();
        int[] availableArenas = Enumerable
            .Range(0, server.DuelLocations.Length)
            .Where(index => !occupiedArenas.Contains(index))
            .ToArray();
        if (availableArenas.Length == 0)
        {
            arenaIndex = -1;
            return false;
        }

        arenaIndex = availableArenas[
            server.random.Next(availableArenas.Length)
        ];
        return true;
    }

    private bool IsArenaAvailable(int arenaIndex) =>
        arenaIndex >= 0 &&
        arenaIndex < server.DuelLocations.Length &&
        !server.ActiveMatches.Values.Any(match =>
            match.ArenaIndex == arenaIndex
        );

    private bool StartMatch(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        MatchQueueType queueType,
        int? selectedArenaIndex = null
    )
    {
        int arenaIndex;
        if (selectedArenaIndex is int selectedIndex)
        {
            if (!IsArenaAvailable(selectedIndex))
                return false;

            arenaIndex = selectedIndex;
        }
        else if (!TryGetAvailableArena(out arenaIndex))
        {
            return false;
        }

        if (player1.IsBot)
            server.PlayerManager.ResetPracticeBotRecord(player1);
        if (player2.IsBot)
            server.PlayerManager.ResetPracticeBotRecord(player2);

        if (!player1.IsBot && !player2.IsBot)
        {
            string[] matchFoundLines =
            {
                "  [MATCH FOUND]",
                GetMatchFoundPlayerLine(player1, queueType),
                GetMatchFoundPingLine(player1),
                "                 VS",
                GetMatchFoundPlayerLine(player2, queueType),
                GetMatchFoundPingLine(player2)
            };
            int matchFoundWidth = matchFoundLines.Max(line => line.Length);
            string matchFoundMessage = string.Join(
                "\n",
                matchFoundLines.Select(line =>
                    line.PadLeft((matchFoundWidth + line.Length) / 2))
            );
            Chat.PrintToChat(player1, matchFoundMessage);
            Chat.PrintToChat(player2, matchFoundMessage);
        }

        Console.WriteLine(
            $"Starting match: {player1.PlayerName} vs {player2.PlayerName}"
        );

        Vector3 arena = server.DuelLocations[arenaIndex];
        string player1MatchKey =
            server.PlayerManager.GetMatchParticipantName(player1);
        string player2MatchKey =
            server.PlayerManager.GetMatchParticipantName(player2);
        var player1Pawn = player1.GetHeroPawn();
        var player2Pawn = player2.GetHeroPawn();
        bool HasItem(CCitadelPlayerPawn? pawn, string itemName) =>
            pawn?.AbilityComponent?.FindAbilityByName(itemName) != null;

        var match = new DuelMatch
        {
            Player1 = player1MatchKey,
            Player2 = player2MatchKey,
            Player1HadBulletLifestealItem = HasItem(player1Pawn, "upgrade_vampire"),
            Player2HadBulletLifestealItem = HasItem(player2Pawn, "upgrade_vampire"),
            Player1HadBlitzBulletsItem = HasItem(player1Pawn, "upgrade_blitz_bullets"),
            Player2HadBlitzBulletsItem = HasItem(player2Pawn, "upgrade_blitz_bullets"),
            Player1HadOpeningRoundsItem = HasItem(player1Pawn, "upgrade_pristine_emblem"),
            Player2HadOpeningRoundsItem = HasItem(player2Pawn, "upgrade_pristine_emblem"),
            Player1HadFleetfootItem = HasItem(player1Pawn, "upgrade_fleetfoot_boots"),
            Player2HadFleetfootItem = HasItem(player2Pawn, "upgrade_fleetfoot_boots"),
            Player1HadTitanicMagazineItem = HasItem(player1Pawn, "upgrade_titan_round"),
            Player2HadTitanicMagazineItem = HasItem(player2Pawn, "upgrade_titan_round"),
            Player1HadCardioCalibratorItem = HasItem(player1Pawn, "upgrade_cardio_calibrator"),
            Player2HadCardioCalibratorItem = HasItem(player2Pawn, "upgrade_cardio_calibrator"),
            Player1HadBurstFireItem = HasItem(player1Pawn, "upgrade_burst_fire"),
            Player2HadBurstFireItem = HasItem(player2Pawn, "upgrade_burst_fire"),
            Player1HadFortitudeItem = HasItem(player1Pawn, "upgrade_chonky"),
            Player2HadFortitudeItem = HasItem(player2Pawn, "upgrade_chonky"),
            Player1HadHealthItem = HasItem(player1Pawn, "upgrade_health"),
            Player2HadHealthItem = HasItem(player2Pawn, "upgrade_health"),
            Player1HadGlassCannonItem = HasItem(player1Pawn, "upgrade_glass_cannon"),
            Player2HadGlassCannonItem = HasItem(player2Pawn, "upgrade_glass_cannon"),
            QueueType = queueType,
            ArenaPosition = arena,
            ArenaIndex = arenaIndex,
            Score1 = 0,
            Score2 = 0
        };

        server.PendingRespawns.Remove(player1MatchKey);
        server.PendingRespawns.Remove(player2MatchKey);
        server.ActiveMatches[player1MatchKey] = match;
        server.ActiveMatches[player2MatchKey] = match;
        server.UIManager.ShowMatchHud(match);

        void InitializeMatchInArena()
        {
            if (match.Finished)
                return;

            if (player1.IsValid)
                player1.ChangeTeam(2);
            if (player2.IsValid)
                player2.ChangeTeam(3);

            var pawn1 = player1.IsValid ? player1.GetHeroPawn() : null;
            var pawn2 = player2.IsValid ? player2.GetHeroPawn() : null;

            if (pawn1 == null || pawn2 == null)
            {
                Console.WriteLine("A player pawn is null.");
                match.Finished = true;
                server.ActiveMatches.Remove(match.Player1);
                server.ActiveMatches.Remove(match.Player2);
                server.PendingRespawns.Remove(match.Player1);
                server.PendingRespawns.Remove(match.Player2);
                if (player1.IsValid)
                    player1.ChangeTeam(2);
                if (player2.IsValid)
                    player2.ChangeTeam(2);
                server.UIManager.RestoreHudAfterMatch(match);
                server.PlayerManager.OnDuelMatchFinished(match);
                server.Queue.TryStartMatches();
                return;
            }

            pawn1.RemoveModifier("modifier_speed_boost");
            pawn2.RemoveModifier("modifier_speed_boost");

            Vector3 arenaCenter =
                server.PlayerManager.GetDuelArenaCenter(arena);
            pawn1.Teleport(
                arenaCenter + new Vector3(-aimLOCK.DUEL_SPAWN_OFFSET, 0f, 0f),
                new Vector3(0f, 45f, 0f),
                null
            );

            pawn2.Teleport(
                arenaCenter + new Vector3(aimLOCK.DUEL_SPAWN_OFFSET, 0f, 0f),
                new Vector3(0f, 180f, 0f),
                null
            );
            server.PlayerManager.ConfigurePracticeBotStrafe(pawn1, pawn2);
            server.UIManager.ShowMatchHud(match);

            server.PlayerManager.ApplyDuelMatchModifiers(
                pawn1,
                match.Player1HadGlassCannonItem
            );
            server.PlayerManager.ApplyDuelMatchModifiers(
                pawn2,
                match.Player2HadGlassCannonItem
            );
            server.PlayerManager.ApplyDuelBulletLifesteal(pawn1);
            server.PlayerManager.ApplyDuelBulletLifesteal(pawn2);
            foreach (var pawn in new[] { pawn1, pawn2 })
            {
                server.PlayerManager.ApplyDuelItemStack(pawn);
                pawn.RemoveAbility("upgrade_infinite_rounds");
            }

            match.ArenaInitialized = true;
            server.PlayerManager.ShowArenaScoreboard(match);
            server.PlayerManager.StartDuelHealthCapMonitor(match);
            server.PlayerManager.SetMatchDisarmed(match, true);
            server.ScheduleOnce(0.25.Seconds(), () =>
            {
                if (match.Finished)
                    return;

                server.PlayerManager.PrepareDuelMatchHealth(match);
                server.EnableMatchInfiniteAmmo();
                StartCountdown(match);
            });

            Console.WriteLine(
                $"Match Found! {player1.PlayerName} vs {player2.PlayerName}"
            );
        }

        server.ScheduleOnce(2.Seconds(), InitializeMatchInArena);
        return true;
    }

    private static int GetAllowedPingDifference(double oldestWaitSeconds)
    {
        if (oldestWaitSeconds >= 120)
            return int.MaxValue;

        int wideningSteps = (int)(oldestWaitSeconds / 15);
        return Math.Min(60, 6 + wideningSteps * 3);
    }

    private sealed record QueuedPlayer(
        QueueEntry Entry,
        int QueueIndex,
        CCitadelPlayerController? Player
    );

    private List<QueueEntry> GetQueue(MatchQueueType queueType) =>
        queueType switch
        {
            MatchQueueType.Ranked => server.RankedQueue,
            MatchQueueType.Unranked => server.UnrankedQueue,
            _ => throw new ArgumentOutOfRangeException(nameof(queueType))
        };

    private string GetMatchFoundPlayerLine(
        CCitadelPlayerController player,
        MatchQueueType queueType)
    {
        string displayName = server.GuildManager.FormatPlayerName(player);
        if (player.IsBot)
            return displayName;

        if (queueType == MatchQueueType.Ranked)
        {
            RankedEloStats stats = server.RankedElo.GetStats(
                server.PlayerManager.GetRankedEloId(player),
                player.PlayerName
            );
            int games = stats.Wins + stats.Losses;
            float winRate = games > 0
                ? (float)stats.Wins / games * 100f
                : 0f;

            return $"{displayName} | {stats.Rating} Elo | " +
                $"{stats.Wins}-{stats.Losses} | {winRate:F1}% WR";
        }

        PlayerRecord record = server.PlayerManager.GetRecord(player);
        int unrankedGames = record.Wins + record.Losses;
        float unrankedWinRate = unrankedGames > 0
            ? (float)record.Wins / unrankedGames * 100f
            : 0f;

        return $"{displayName} | {record.Wins}-{record.Losses} | " +
            $"{unrankedWinRate:F1}% WR";
    }

    private string GetMatchFoundPingLine(CCitadelPlayerController player) =>
        server.PingManager.TryGetPing(player, out int pingMs)
            ? $"      Ping: {pingMs} ms"
            : "           Ping: N/A";

    public void StartCountdown(DuelMatch match)
    {
        if (
            match.Finished ||
            match.CountdownActive ||
            !match.ArenaInitialized
        )
            return;

        match.CountdownActive = true;
        match.CombatActive = false;
        int countdownGeneration = ++match.CountdownGeneration;

        server.PlayerManager.ResetMatchFixationStacks(match);
        server.PlayerManager.SetMatchDisarmed(match, true);

        PrintCountdownMessage(match, "[DUEL] 3...");

        server.ScheduleOnce(1.Seconds(), () =>
        {
            if (IsCurrentCountdown(match, countdownGeneration))
                PrintCountdownMessage(match, "[DUEL] 2...");
        });

        server.ScheduleOnce(2.Seconds(), () =>
        {
            if (IsCurrentCountdown(match, countdownGeneration))
                PrintCountdownMessage(match, "[DUEL] 1...");
        });

        server.ScheduleOnce(3.Seconds(), () =>
        {
            if (!IsCurrentCountdown(match, countdownGeneration))
                return;

            var player1 = server.PlayerManager.FindMatchParticipant(
                match.Player1
            );
            var player2 = server.PlayerManager.FindMatchParticipant(
                match.Player2
            );
            var pawn1 = player1?.GetHeroPawn();
            var pawn2 = player2?.GetHeroPawn();
            if (
                pawn1 is not { IsValid: true, IsAlive: true } ||
                pawn2 is not { IsValid: true, IsAlive: true }
            )
            {
                match.CountdownActive = false;
                server.PlayerManager.SetMatchDisarmed(match, true);
                return;
            }

            pawn1.RemoveModifier("modifier_citadel_disarmed");
            pawn2.RemoveModifier("modifier_citadel_disarmed");
            PrintCountdownMessage(match, "[DUEL] FIGHT!");
            match.CountdownActive = false;
            match.CombatActive = true;
        });
    }

    private void PrintCountdownMessage(DuelMatch match, string message)
    {
        foreach (string participantName in new[] { match.Player1, match.Player2 })
        {
            var participant = server.PlayerManager.FindMatchParticipant(
                participantName
            );
            if (participant is { IsValid: true, IsBot: false })
                Chat.PrintToChat(participant, message);
        }
    }

    private static bool IsCurrentCountdown(
        DuelMatch match,
        int countdownGeneration
    ) =>
        !match.Finished &&
        match.CountdownActive &&
        match.CountdownGeneration == countdownGeneration;
}