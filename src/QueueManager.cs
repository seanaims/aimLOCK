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
        DateTimeOffset ExpiresAt,
        float? LifestealPercent = null
    );

    // A lifesteal change proposed during a duel (/duel ls N), waiting for the opponent's
    // /accept. Keyed by the match.
    private sealed record LifestealProposal(
        uint ProposerHandle,
        string ProposerName,
        float Percent,
        DateTimeOffset ExpiresAt
    );
    private readonly Dictionary<DuelMatch, LifestealProposal> lifestealProposals = new();

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

        if (
            server.PlayerManager.TryGetActiveMatch(controller, out var activeMatch) &&
            !server.PlayerManager.IsBotMatch(activeMatch)
        )
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
        // Fetch fresh pings now (it also retries matchmaking when it finishes) instead
        // of waiting for the next scheduled poll.
        server.PingManager.Refresh();

        caller.Reply($"You have joined the {queueType.ToString().ToLowerInvariant()} queue.");

        Sounds.Play(
            "Damage.Send.Crit",
            RecipientFilter.Single(controller.Slot)
        );

        Console.WriteLine(
            $"{controller.PlayerName} has joined the {queueType} queue."
        );
    }

    public void JoinQueueFromZone(
        CCitadelPlayerController controller,
        MatchQueueType queueType
    )
    {
        if (server.PlayerManager.IsSpectating(controller))
        {
            Chat.PrintToChat(controller, "Leave spectating before joining a queue.");
            return;
        }

        ulong steamId = controller.PlayerSteamId;
        bool alreadyInTargetQueue = GetQueue(queueType).Any(entry =>
            entry.SteamId == steamId
        );
        if (alreadyInTargetQueue)
            return;

        bool wasQueued = server.RankedQueue
            .Concat(server.UnrankedQueue)
            .Any(entry => entry.SteamId == steamId);
        if (wasQueued)
        {
            server.RankedQueue.RemoveAll(entry => entry.SteamId == steamId);
            server.UnrankedQueue.RemoveAll(entry => entry.SteamId == steamId);
            server.UIManager.RefreshHud();
        }

        JoinQueue(Caller.Of(controller), queueType);
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
        Action<string> reply,
        bool alreadyInArena = false
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


        int arenaIndex = Array.IndexOf(server.DuelLocations, arena);
        if (!IsArenaAvailable(arenaIndex))
        {
            reply("All duel arenas are currently in use.");
            return false;
        }

        if (!StartMatch(
            player,
            bot,
            MatchQueueType.Unranked,
            arenaIndex,
            alreadyInArena
        ))
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
        Action<string> reply,
        float? lifestealPercent = null
    )
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            reply("Use /duel <player name> [arena name] [ls <0-100>].");
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
            now + DuelRequestLifetime,
            lifestealPercent
        );
        string arenaMessage = (arenaIndex is int selectedArena
            ? $" on arena {selectedArena + 1} ({aimLOCK.DuelArenaNames[selectedArena]})"
            : "") +
            (lifestealPercent is { } requestedLifesteal
                ? $" with {requestedLifesteal:0.#}% lifesteal"
                : "");
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
        // In a duel: /accept takes the opponent's lifesteal proposal.
        if (
            lifestealProposals.Count != 0 &&
            server.PlayerManager.TryGetActiveMatch(target, out var currentMatch) &&
            lifestealProposals.TryGetValue(currentMatch, out var proposal)
        )
        {
            if (proposal.ProposerHandle == target.EntityHandle)
            {
                reply("Your opponent has to accept your lifesteal proposal.");
                return false;
            }

            lifestealProposals.Remove(currentMatch);
            if (proposal.ExpiresAt <= DateTimeOffset.UtcNow || currentMatch.Finished)
            {
                reply("That lifesteal proposal has expired.");
                return false;
            }

            SetMatchLifesteal(currentMatch, proposal.Percent,
                $"{target.PlayerName} accepted: this duel's lifesteal is now {proposal.Percent:0.#}%.");
            return true;
        }

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
        if (
            request.LifestealPercent is { } agreedLifesteal &&
            server.PlayerManager.TryGetActiveMatch(target, out var startedMatch)
        )
            startedMatch.LifestealPercent = agreedLifesteal;

        string arenaMessage =
            $" on arena {arenaIndex + 1} ({aimLOCK.DuelArenaNames[arenaIndex]})" +
            (request.LifestealPercent is { } shownLifesteal
                ? $" with {shownLifesteal:0.#}% lifesteal"
                : "");
        reply(
            $"Accepted {request.Initiator.PlayerName}'s duel request{arenaMessage}. Starting match..."
        );
        Chat.PrintToChat(
            request.Initiator,
            $"{target.PlayerName} accepted your duel request{arenaMessage}. Starting match..."
        );
        return true;
    }

    /// <summary>
    /// /duel ls N during a duel: against a bot it applies right away; against a player
    /// it waits for the opponent's /accept (60 seconds).
    /// </summary>
    public void ProposeMatchLifesteal(
        CCitadelPlayerController proposer,
        float percent,
        Action<string> reply
    )
    {
        if (
            !server.PlayerManager.TryGetActiveMatch(proposer, out var match) ||
            match.Finished
        )
        {
            reply("You are not in a duel. To set it when challenging: /duel <player name> ls " +
                $"{percent:0.#}.");
            return;
        }

        var opponent = server.PlayerManager.FindMatchParticipant(
            string.Equals(
                match.Player1,
                server.PlayerManager.GetMatchParticipantName(proposer),
                StringComparison.Ordinal
            )
                ? match.Player2
                : match.Player1
        );
        if (opponent == null || opponent.IsBot)
        {
            SetMatchLifesteal(match, percent, $"This duel's lifesteal is now {percent:0.#}%.");
            return;
        }

        lifestealProposals[match] = new LifestealProposal(
            proposer.EntityHandle,
            proposer.PlayerName,
            percent,
            DateTimeOffset.UtcNow + DuelRequestLifetime
        );
        reply($"Proposed {percent:0.#}% lifesteal to {opponent.PlayerName}.");
        Chat.PrintToChat(
            opponent,
            $"{proposer.PlayerName} wants this duel's lifesteal at {percent:0.#}%. Use /accept within 60 seconds."
        );
    }

    private void SetMatchLifesteal(DuelMatch match, float percent, string message)
    {
        match.LifestealPercent = percent;
        foreach (string name in new[] { match.Player1, match.Player2 })
        {
            var player = server.PlayerManager.FindMatchParticipant(name);
            if (player is { IsBot: false })
                Chat.PrintToChat(player, message);
        }
    }

    public void OnMatchFinished(DuelMatch match) => lifestealProposals.Remove(match);

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

        if (queue.Count < 2)
            return;

        bool hasArena = TryGetAvailableArena(out int arenaIndex);

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
            .Where(item =>
                item.Player != null &&
                (
                    !server.PlayerManager.TryGetActiveMatch(item.Player, out var match) ||
                    server.PlayerManager.IsBotMatch(match)
                )
            )
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

        /* End practice bot matches; finishing a match runs the queue again */
        foreach (var player in new[] { player1, player2 })
        {
            if (!server.PlayerManager.TryGetActiveMatch(player, out _))
                continue;

            Chat.PrintToChat(player, "Match found! Ending your bot match.");
            server.PlayerManager.ForfeitMatch(
                server.PlayerManager.GetMatchParticipantName(player)
            );
            return;
        }

        if (!hasArena)
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
        // Runs on every queue change; plain loops instead of building a HashSet and
        // two arrays each time. An arena held for a bot match being set up counts as
        // taken.
        int arenaCount = server.DuelLocations.Length;
        Span<bool> taken = arenaCount <= 64 ? stackalloc bool[arenaCount] : new bool[arenaCount];
        foreach (var match in server.ActiveMatches.Values)
        {
            if ((uint)match.ArenaIndex < (uint)arenaCount)
                taken[match.ArenaIndex] = true;
        }

        int availableCount = 0;
        for (int i = 0; i < arenaCount; i++)
        {
            if (taken[i] || server.PlayerManager.IsArenaReserved(i))
                taken[i] = true;
            else
                availableCount++;
        }

        if (availableCount == 0)
        {
            arenaIndex = -1;
            return false;
        }

        int pick = server.random.Next(availableCount);
        for (int i = 0; i < arenaCount; i++)
        {
            if (taken[i])
                continue;
            if (pick-- == 0)
            {
                arenaIndex = i;
                return true;
            }
        }

        arenaIndex = -1;
        return false;
    }

    private bool IsArenaAvailable(int arenaIndex)
    {
        if (
            arenaIndex < 0 ||
            arenaIndex >= server.DuelLocations.Length ||
            server.PlayerManager.IsArenaReserved(arenaIndex)
        )
            return false;

        foreach (var match in server.ActiveMatches.Values)
        {
            if (match.ArenaIndex == arenaIndex)
                return false;
        }

        return true;
    }

    private bool StartMatch(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        MatchQueueType queueType,
        int? selectedArenaIndex = null,
        bool alreadyInArena = false
    )
    {
        if (!TryPickArena(selectedArenaIndex, out int arenaIndex))
            return false;

        if (player1.IsBot)
            server.PlayerManager.ResetPracticeBotRecord(player1);
        if (player2.IsBot)
            server.PlayerManager.ResetPracticeBotRecord(player2);

        if (!player1.IsBot && !player2.IsBot)
            AnnounceMatchFound(player1, player2, queueType);

        Console.WriteLine(
            $"Starting match: {player1.PlayerName} vs {player2.PlayerName}"
        );

        DuelMatch match = CreateMatch(player1, player2, queueType, arenaIndex);
        server.PendingRespawns.Remove(match.Player1);
        server.PendingRespawns.Remove(match.Player2);
        server.ActiveMatches[match.Player1] = match;
        server.ActiveMatches[match.Player2] = match;
        server.LeaderboardManager.RefreshActiveMatches();

        // A bot match whose player is already standing in the arena skips the 2 s
        // wait: there is nowhere to teleport to. InitializeMatchInArena shows its HUD;
        // building it here too sent every panel twice within two frames.
        if (alreadyInArena)
        {
            server.ScheduleNextTick(
                () => InitializeMatchInArena(match, player1, player2)
            );
        }
        else
        {
            server.UIManager.ShowMatchHud(match);
            server.ScheduleOnce(
                2.Seconds(),
                () => InitializeMatchInArena(match, player1, player2)
            );
        }

        return true;
    }

    private bool TryPickArena(int? selectedArenaIndex, out int arenaIndex)
    {
        if (selectedArenaIndex is int selectedIndex)
        {
            arenaIndex = selectedIndex;
            return IsArenaAvailable(selectedIndex);
        }

        return TryGetAvailableArena(out arenaIndex);
    }

    private void AnnounceMatchFound(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        MatchQueueType queueType
    )
    {
        string[] lines =
        {
            "  [MATCH FOUND]",
            GetMatchFoundPlayerLine(player1, queueType),
            GetMatchFoundPingLine(player1),
            "                 VS",
            GetMatchFoundPlayerLine(player2, queueType),
            GetMatchFoundPingLine(player2)
        };
        // Centre every line on the widest one.
        int width = lines.Max(line => line.Length);
        string message = string.Join(
            "\n",
            lines.Select(line => line.PadLeft((width + line.Length) / 2))
        );
        Chat.PrintToChat(player1, message);
        Chat.PrintToChat(player2, message);
    }

    private DuelMatch CreateMatch(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        MatchQueueType queueType,
        int arenaIndex
    )
    {
        var playerManager = server.PlayerManager;
        return new DuelMatch
        {
            Player1 = playerManager.GetMatchParticipantName(player1),
            Player2 = playerManager.GetMatchParticipantName(player2),
            Player1ItemsBeforeMatch =
                PlayerManager.GetOwnedDuelItems(player1.GetHeroPawn()),
            Player2ItemsBeforeMatch =
                PlayerManager.GetOwnedDuelItems(player2.GetHeroPawn()),
            QueueType = queueType,
            ArenaPosition = server.DuelLocations[arenaIndex],
            ArenaIndex = arenaIndex,
            BuyingAllowed =
                player1.IsBot ||
                player2.IsBot ||
                (
                    playerManager.AllowsDuelBuying(player1) &&
                    playerManager.AllowsDuelBuying(player2)
                )
        };
    }

    private void InitializeMatchInArena(
        DuelMatch match,
        CCitadelPlayerController player1,
        CCitadelPlayerController player2
    )
    {
        if (match.Finished)
            return;

        PlayerManager.EnsureTeam(player1, 2);
        PlayerManager.EnsureTeam(player2, 3);

        var pawn1 = player1.IsValid ? player1.GetHeroPawn() : null;
        var pawn2 = player2.IsValid ? player2.GetHeroPawn() : null;
        if (pawn1 == null || pawn2 == null)
        {
            Console.WriteLine("A player pawn is null.");
            CancelMatch(match, player1, player2);
            return;
        }

        var playerManager = server.PlayerManager;
        pawn1.RemoveModifier("modifier_speed_boost");
        pawn2.RemoveModifier("modifier_speed_boost");

        Vector3 arenaCenter = playerManager.GetDuelArenaCenter(match.ArenaPosition);
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
        playerManager.ConfigurePracticeBotStrafe(pawn1, pawn2);
        server.UIManager.ShowMatchHud(match);

        playerManager.ApplyDuelMatchModifiers(pawn1);
        playerManager.ApplyDuelMatchModifiers(pawn2);
        playerManager.ApplyDuelBulletLifesteal(pawn1);
        playerManager.ApplyDuelBulletLifesteal(pawn2);
        // The item stack is the biggest batch of changes per pawn; do the second
        // pawn on the next tick so both don't land in the same server frame.
        playerManager.ApplyDuelItemStack(pawn1);
        pawn1.RemoveAbility("upgrade_infinite_rounds");
        server.ScheduleNextTick(() =>
        {
            if (match.Finished || !pawn2.IsValid)
                return;

            server.PlayerManager.ApplyDuelItemStack(pawn2);
            pawn2.RemoveAbility("upgrade_infinite_rounds");
        });

        match.ArenaInitialized = true;
        playerManager.ShowArenaScoreboard(match);
        playerManager.StartDuelHealthCapMonitor(match);
        playerManager.SetMatchDisarmed(match, true);
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

    /* A player lost their pawn before the match could start */
    private void CancelMatch(
        DuelMatch match,
        CCitadelPlayerController player1,
        CCitadelPlayerController player2
    )
    {
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
        string displayName = server.GuildManager.FormatPlayerNameWithSelectedTag(player);
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

        // Every duel and every round starts from zero Fixation stacks and full health.
        server.PlayerManager.ResetRoundState(match);
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

            // Fixation cleared and full health again right as the round goes live: a
            // bullet still in the air from the last round can land during the countdown
            // and put stacks back, and late respawn health or max-health updates can
            // change health.
            server.PlayerManager.ResetRoundState(match);
            PlayerManager.RemoveAllDisarms(pawn1);
            PlayerManager.RemoveAllDisarms(pawn2);
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