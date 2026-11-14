/*
this file is for the queue manager
it parses the various commands, starting matches, finding other players, and setting up the matches
*/

using System;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;
using DeadworksManaged.Api.Sounds;

namespace aimLOCK;

public class QueueManager
{
    private readonly aimLOCK server;

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

        bool alreadyQueued = server.RankedQueue
            .Concat(server.UnrankedQueue)
            .Any(x => StringComparer.OrdinalIgnoreCase.Equals(
                x.PlayerName,
                controller.PlayerName));

        if (alreadyQueued)
        {
            caller.Reply("You are already in a queue. Use /leave first.");
            return;
        }

        var queue = GetQueue(queueType);
        queue.Add(new QueueEntry(controller.PlayerName));

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
            StringComparer.OrdinalIgnoreCase.Equals(
                x.PlayerName, controller.PlayerName));

        if (player != null)
        {
            server.RankedQueue.Remove(player);
        }
        else
        {
            player = server.UnrankedQueue.FirstOrDefault(x =>
                StringComparer.OrdinalIgnoreCase.Equals(
                    x.PlayerName, controller.PlayerName));
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
        if (server.ActiveMatches.ContainsKey(player.PlayerName))
        {
            reply("You are already in a match.");
            return false;
        }

        if (server.ActiveMatches.ContainsKey(bot.PlayerName))
        {
            reply("The practice bot is already in a match.");
            return false;
        }

        bool alreadyQueued = server.RankedQueue
            .Concat(server.UnrankedQueue)
            .Any(entry => StringComparer.OrdinalIgnoreCase.Equals(
                entry.PlayerName,
                player.PlayerName));
        if (alreadyQueued)
        {
            reply("Leave your current queue with /leave before starting a bot match.");
            return false;
        }

        StartMatch(player, bot, MatchQueueType.Unranked, arena);
        reply(
            $"Starting Bot ({server.PlayerManager.GetPracticeBotDifficultyName(bot)}) match..."
        );
        return true;
    }

    public bool StartDuelMatch(
        CCitadelPlayerController initiator,
        string targetName,
        Action<string> reply
    )
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            reply("Use /duel <player name>.");
            return false;
        }

        if (server.PlayerManager.TryGetActiveMatch(initiator, out _))
        {
            reply("You are already in a match.");
            return false;
        }

        if (server.PlayerManager.IsSpectating(initiator))
        {
            reply("Stop spectating before starting a duel.");
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
                StringComparer.OrdinalIgnoreCase.Equals(
                    entry.PlayerName,
                    initiator.PlayerName
                ) ||
                StringComparer.OrdinalIgnoreCase.Equals(
                    entry.PlayerName,
                    target.PlayerName
                )
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

        StartMatch(initiator, target, MatchQueueType.Unranked);
        return true;
    }

    public void TryStartMatches()
    {
        TryStartMatch(MatchQueueType.Ranked);
        TryStartMatch(MatchQueueType.Unranked);
    }

    public void TryStartMatch(MatchQueueType queueType)
    {
        var queue = GetQueue(queueType);

        if (queue.Count < 2)
            return;

        var playersByName = Players.GetAll()
            .GroupBy(player => player.PlayerName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase
            );
        var queuedPlayers = queue
            .Select((entry, index) => new QueuedPlayer(
                entry,
                index,
                playersByName.TryGetValue(entry.PlayerName, out var player)
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
                    if (!server.PingManager.TryGetPing(firstPlayer, out int firstPing)
                        || !server.PingManager.TryGetPing(secondPlayer, out int secondPing))
                    {
                        continue;
                    }

                    pingDifference = Math.Abs((long)firstPing - secondPing);
                    DateTimeOffset oldestEntry = firstQueued.Entry.EnqueuedAt
                        <= secondQueued.Entry.EnqueuedAt
                            ? firstQueued.Entry.EnqueuedAt
                            : secondQueued.Entry.EnqueuedAt;
                    int allowedDifference = GetAllowedPingDifference(
                        Math.Max(0d, (now - oldestEntry).TotalSeconds)
                    );
                    if (pingDifference > allowedDifference)
                        continue;
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

        StartMatch(player1, player2, queueType);

        queue.RemoveAt(selectedSecondIndex);
        queue.RemoveAt(selectedFirstIndex);

        server.UIManager.RefreshHud();
    }

    private void StartMatch(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        MatchQueueType queueType,
        Vector3? selectedArena = null
    )
    {
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

        Vector3 arena = selectedArena ?? server.DuelLocations[
            server.random.Next(server.DuelLocations.Length)
        ];

        var match = new DuelMatch
        {
            Player1 = player1.PlayerName,
            Player2 = player2.PlayerName,
            Player1HadBulletLifestealItem =
                player1.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_vampire"
                ) != null,
            Player2HadBulletLifestealItem =
                player2.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_vampire"
                ) != null,
            Player1HadBlitzBulletsItem =
                player1.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_blitz_bullets"
                ) != null,
            Player2HadBlitzBulletsItem =
                player2.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_blitz_bullets"
                ) != null,
            Player1HadOpeningRoundsItem =
                player1.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_pristine_emblem"
                ) != null,
            Player2HadOpeningRoundsItem =
                player2.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_pristine_emblem"
                ) != null,
            Player1HadFleetfootItem =
                player1.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_fleetfoot_boots"
                ) != null,
            Player2HadFleetfootItem =
                player2.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_fleetfoot_boots"
                ) != null,
            Player1HadTitanicMagazineItem =
                player1.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_titan_round"
                ) != null,
            Player2HadTitanicMagazineItem =
                player2.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_titan_round"
                ) != null,
            Player1HadCardioCalibratorItem =
                player1.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_cardio_calibrator"
                ) != null,
            Player2HadCardioCalibratorItem =
                player2.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_cardio_calibrator"
                ) != null,
            Player1HadBurstFireItem =
                player1.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_burst_fire"
                ) != null,
            Player2HadBurstFireItem =
                player2.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_burst_fire"
                ) != null,
            Player1HadHealthItem =
                player1.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_health"
                ) != null,
            Player2HadHealthItem =
                player2.GetHeroPawn()?.AbilityComponent?.FindAbilityByName(
                    "upgrade_health"
                ) != null,
            QueueType = queueType,
            ArenaPosition = arena,
            Score1 = 0,
            Score2 = 0
        };

        server.PendingRespawns.Remove(player1.PlayerName);
        server.PendingRespawns.Remove(player2.PlayerName);
        server.ActiveMatches[player1.PlayerName] = match;
        server.ActiveMatches[player2.PlayerName] = match;
        server.UIManager.ShowMatchHud(match);

        var preTeleportPawn1 = player1.GetHeroPawn();
        var preTeleportPawn2 = player2.GetHeroPawn();
        var respawnEffect1 = preTeleportPawn1 is { IsValid: true }
            ? server.PlayerManager.BeginRespawnEffect(preTeleportPawn1)
            : null;
        var respawnEffect2 = preTeleportPawn2 is { IsValid: true }
            ? server.PlayerManager.BeginRespawnEffect(preTeleportPawn2)
            : null;

        void InitializeMatchInArena()
        {
            player1.ChangeTeam(2);
            player2.ChangeTeam(3);

            var pawn1 = player1.GetHeroPawn();
            var pawn2 = player2.GetHeroPawn();

            if (pawn1 == null || pawn2 == null)
            {
                Console.WriteLine("A player pawn is null.");
                server.PlayerManager.EndRespawnEffect(respawnEffect1);
                server.PlayerManager.EndRespawnEffect(respawnEffect2);
                server.UIManager.RestoreHudAfterMatch(match);
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
            server.ScheduleOnce(1.5.Seconds(), () =>
            {
                server.PlayerManager.EndRespawnEffect(respawnEffect1);
                server.PlayerManager.EndRespawnEffect(respawnEffect2);
            });

            server.PlayerManager.ApplyDuelHealthModifier(pawn1);
            server.PlayerManager.ApplyDuelHealthModifier(pawn2);
            match.SharedMaxHealth =
                Math.Max(pawn1.GetMaxHealth(), pawn2.GetMaxHealth())
                + (int)aimLOCK.DUEL_BONUS_HEALTH;
            server.PlayerManager.ApplyDuelMatchModifiers(
                pawn1,
                match.SharedMaxHealth
            );
            server.PlayerManager.ApplyDuelMatchModifiers(
                pawn2,
                match.SharedMaxHealth
            );
            server.PlayerManager.ApplyDuelBulletLifesteal(pawn1);
            server.PlayerManager.ApplyDuelBulletLifesteal(pawn2);
            foreach (var pawn in new[] { pawn1, pawn2 })
            {
                server.PlayerManager.ApplyDuelItemStack(pawn);
                pawn.RemoveAbility("upgrade_infinite_rounds");
            }

            match.ArenaInitialized = true;
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