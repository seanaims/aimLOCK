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

        var bot = Players.GetAll().FirstOrDefault(player => player.IsBot);
        if (bot != null)
            MoveBotToQueue(bot.PlayerName, queueType);

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

    public void MoveBotToQueue(string botName, MatchQueueType queueType)
    {
        server.BotQueuePreference = queueType;

        if (server.ActiveMatches.ContainsKey(botName))
            return;

        server.RankedQueue.RemoveAll(entry =>
            StringComparer.OrdinalIgnoreCase.Equals(entry.PlayerName, botName));
        server.UnrankedQueue.RemoveAll(entry =>
            StringComparer.OrdinalIgnoreCase.Equals(entry.PlayerName, botName));

        GetQueue(queueType).Add(new QueueEntry(botName));
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

                if (!firstPlayer.IsBot && !secondPlayer.IsBot)
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
        Chat.PrintToChatAll(matchFoundMessage);

        Console.WriteLine(
            $"Starting match: {player1.PlayerName} vs {player2.PlayerName}"
        );

        server.ScheduleOnce(2.Seconds(), () =>
        {
            player1.ChangeTeam(2);
            player2.ChangeTeam(3);

            var pawn1 = player1.GetHeroPawn();
            var pawn2 = player2.GetHeroPawn();

            if (pawn1 == null || pawn2 == null)
            {
                Console.WriteLine("A player pawn is null.");
                return;
            }

            Vector3 arena =
                server.DuelLocations[
                    server.random.Next(server.DuelLocations.Length)
                ];

            pawn1.Teleport(
                arena + new Vector3(-200f, 0f, 0f),
                new Vector3(0f, 45f, 0f),
                null
            );

            pawn2.Teleport(
                arena + new Vector3(200f, 0f, 0f),
                new Vector3(0f, 180f, 0f),
                null
            );

            // Lifesteal
            pawn1.AddModifier(
                "upgrade_vampire/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BulletLifestealPercent"] = aimLOCK.DUEL_LIFESTEAL
                }
            );

            pawn2.AddModifier(
                "upgrade_vampire/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BulletLifestealPercent"] = aimLOCK.DUEL_LIFESTEAL
                }
            );

            // Bonus Health
            pawn1.AddModifier(
                "upgrade_health/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BonusHealth"] = aimLOCK.DUEL_BONUS_HEALTH
                }
            );

            pawn2.AddModifier(
                "upgrade_health/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BonusHealth"] = aimLOCK.DUEL_BONUS_HEALTH
                }
            );

            // Glass Cannon
            pawn1.AddModifier(
                "upgrade_glass_cannon/modifier_glass_cannon_intrinstic",
                new System.Collections.Generic.Dictionary<string, float>
                {
                    ["MaxHealthLossPercent"] = aimLOCK.DUEL_GLASS_CANNON_HEALTH_LOSS,
                    ["BaseAttackDamagePercent"] = aimLOCK.DUEL_GLASS_CANNON_DAMAGE
                }
            );

            pawn2.AddModifier(
                "upgrade_glass_cannon/modifier_glass_cannon_intrinstic",
                new System.Collections.Generic.Dictionary<string, float>
                {
                    ["MaxHealthLossPercent"] = aimLOCK.DUEL_GLASS_CANNON_HEALTH_LOSS,
                    ["BaseAttackDamagePercent"] = aimLOCK.DUEL_GLASS_CANNON_DAMAGE
                }
            );

            pawn1.AddItem("upgrade_infinite_rounds");
            pawn2.AddItem("upgrade_infinite_rounds");

            var match = new DuelMatch
            {
                Player1 = player1.PlayerName,
                Player2 = player2.PlayerName,
                QueueType = queueType,
                ArenaPosition = arena,
                Score1 = 0,
                Score2 = 0
            };

            server.ActiveMatches[player1.PlayerName] = match;
            server.ActiveMatches[player2.PlayerName] = match;

            server.UIManager.ShowMatchHud(match);
            StartCountdown(match);

            server.UIManager.RefreshHud();

            Console.WriteLine(
                $"Match Found! {player1.PlayerName} vs {player2.PlayerName}"
            );
        });

        queue.RemoveAt(selectedSecondIndex);
        queue.RemoveAt(selectedFirstIndex);

        server.UIManager.RefreshHud();
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

            return $"{player.PlayerName} | {stats.Rating} Elo | " +
                $"{stats.Wins}-{stats.Losses} | {winRate:F1}% WR";
        }

        PlayerRecord record = server.PlayerManager.GetRecord(player);
        int unrankedGames = record.Wins + record.Losses;
        float unrankedWinRate = unrankedGames > 0
            ? (float)record.Wins / unrankedGames * 100f
            : 0f;

        return $"{player.PlayerName} | {record.Wins}-{record.Losses} | " +
            $"{unrankedWinRate:F1}% WR";
    }

    private string GetMatchFoundPingLine(CCitadelPlayerController player) =>
        server.PingManager.TryGetPing(player, out int pingMs)
            ? $"      Ping: {pingMs} ms"
            : "           Ping: N/A";

    public void StartCountdown(DuelMatch match)
    {
        match.CountdownActive = true;

        var player1 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player1);
        var player2 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player2);

        var pawn1 = player1?.GetHeroPawn();
        var pawn2 = player2?.GetHeroPawn();

        pawn1?.AddModifier("modifier_citadel_disarmed");
        pawn2?.AddModifier("modifier_citadel_disarmed");

        Chat.PrintToChatAll("[DUEL] 3...");

        server.ScheduleOnce(1.Seconds(), () =>
            Chat.PrintToChatAll("[DUEL] 2..."));

        server.ScheduleOnce(2.Seconds(), () =>
            Chat.PrintToChatAll("[DUEL] 1..."));

        server.ScheduleOnce(3.Seconds(), () =>
        {
            pawn1?.RemoveModifier("modifier_citadel_disarmed");
            pawn2?.RemoveModifier("modifier_citadel_disarmed");

            Chat.PrintToChatAll("[DUEL] FIGHT!");
            match.CountdownActive = false;
        });
    }
}