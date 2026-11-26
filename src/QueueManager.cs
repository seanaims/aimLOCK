/*
this file is for the queue manager
it parses the various commands, starting matches, finding other players, and setting up the matches
*/

namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeadworksManaged.Api;
using DeadworksManaged.Api.Sounds;

public class QueueManager
{
    private static readonly TimeSpan DuelRequestLifetime =
        TimeSpan.FromSeconds(60);

    private readonly aimLOCK server;
    private readonly Dictionary<uint, DuelRequest> pendingDuelRequests = new();

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

        if (this.server.PlayerManager.IsSpectating(controller))
        {
            caller.Reply("Leave spectating before joining a queue.");
            return;
        }

        if (
            this.server.PlayerManager.TryGetActiveMatch(controller, out var activeMatch) &&
            !this.server.PlayerManager.IsBotMatch(activeMatch))
        {
            caller.Reply("You are already in a match.");
            return;
        }

        bool alreadyQueued = this.server.RankedQueue
            .Concat(this.server.UnrankedQueue)
            .Any(x => x.SteamId == controller.PlayerSteamId);

        if (alreadyQueued)
        {
            caller.Reply("You are already in a queue. Use /leave first.");
            return;
        }

        var queue = this.GetQueue(queueType);
        queue.Add(new QueueEntry(controller.PlayerSteamId));

        this.server.UIManager.RefreshHud();

        this.TryStartMatch(queueType);

        // Fetch fresh pings now (it also retries matchmaking when it finishes) instead
        // of waiting for the next scheduled poll.
        this.server.PingManager.Refresh();

        caller.Reply($"You have joined the {queueType.ToString().ToLowerInvariant()} queue.");

        Sounds.Play(
            "Damage.Send.Crit",
            RecipientFilter.Single(controller.Slot));

        Console.WriteLine(
            $"{controller.PlayerName} has joined the {queueType} queue.");
    }

    public void JoinQueueFromZone(
        CCitadelPlayerController controller,
        MatchQueueType queueType)
    {
        if (this.server.PlayerManager.IsSpectating(controller))
        {
            Chat.PrintToChat(controller, "Leave spectating before joining a queue.");
            return;
        }

        ulong steamId = controller.PlayerSteamId;
        bool alreadyInTargetQueue = this.GetQueue(queueType).Any(entry =>
            entry.SteamId == steamId);
        if (alreadyInTargetQueue)
        {
            return;
        }

        bool wasQueued = this.server.RankedQueue
            .Concat(this.server.UnrankedQueue)
            .Any(entry => entry.SteamId == steamId);
        if (wasQueued)
        {
            this.server.RankedQueue.RemoveAll(entry => entry.SteamId == steamId);
            this.server.UnrankedQueue.RemoveAll(entry => entry.SteamId == steamId);
            this.server.UIManager.RefreshHud();
        }

        JoinQueue(Caller.Of(controller), queueType);
    }

    public void LeaveQueue(Caller caller)
    {
        var controller = caller.Player;

        if (controller == null)
        {
            return;
        }

        var player = this.server.RankedQueue.FirstOrDefault(x =>
            x.SteamId == controller.PlayerSteamId);

        if (player != null)
        {
            this.server.RankedQueue.Remove(player);
        }
        else
        {
            player = this.server.UnrankedQueue.FirstOrDefault(x =>
                x.SteamId == controller.PlayerSteamId);
            if (player == null)
            {
                caller.Reply("You are not currently in a queue.");
                return;
            }

            this.server.UnrankedQueue.Remove(player);
        }

        this.server.UIManager.RefreshHud();

        caller.Reply("You have left the queue.");

        Console.WriteLine(
            $"{controller.PlayerName} has left the queue.");
    }

    public bool StartBotMatch(
        Caller caller,
        CCitadelPlayerController bot,
        Vector3 arena)
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
            message => caller.Reply(message));
    }

    public bool StartBotMatch(
        CCitadelPlayerController player,
        CCitadelPlayerController bot,
        Vector3 arena,
        Action<string> reply,
        bool alreadyInArena = false)
    {
        if (this.server.PlayerManager.TryGetActiveMatch(player, out _))
        {
            reply("You are already in a match.");
            return false;
        }

        if (
            this.server.ActiveMatches.ContainsKey(
                this.server.PlayerManager.GetMatchParticipantName(bot)))
        {
            reply("The practice bot is already in a match.");
            return false;
        }

        int arenaIndex = Array.IndexOf(this.server.DuelLocations, arena);
        if (!this.IsArenaAvailable(arenaIndex))
        {
            reply("All duel arenas are currently in use.");
            return false;
        }

        if (!this.StartMatch(
            player,
            bot,
            MatchQueueType.Unranked,
            arenaIndex,
            alreadyInArena))
        {
            return false;
        }

        reply(
            $"Starting Bot ({this.server.PlayerManager.GetPracticeBotDifficultyName(bot)}) match...");
        return true;
    }

    public bool RequestDuelMatch(
        CCitadelPlayerController initiator,
        string targetName,
        int? arenaIndex,
        Action<string> reply,
        float? lifestealPercent = null)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            reply("Use /duel <player name> [arena name] [ls <0-100>].");
            return false;
        }

        if (string.Equals(
            initiator.PlayerName,
            targetName,
            StringComparison.OrdinalIgnoreCase))
        {
            reply("You cannot duel yourself.");
            return false;
        }

        var matches = PlayerLookup.FindByName(
            Players.GetAll().Where(player =>
                !player.IsBot &&
                player.EntityHandle != initiator.EntityHandle),
            targetName);

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
        if (!this.ValidateDuelParticipants(initiator, target, reply))
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        this.PruneExpiredDuelRequests(now);
        if (this.pendingDuelRequests.Values.Any(request =>
            request.Initiator.EntityHandle == initiator.EntityHandle))
        {
            reply("You already have a pending duel request.");
            return false;
        }

        if (this.pendingDuelRequests.TryGetValue(target.EntityHandle, out var existing))
        {
            reply(
                $"{target.PlayerName} already has a pending duel request from {existing.Initiator.PlayerName}.");
            return false;
        }

        this.pendingDuelRequests[target.EntityHandle] = new DuelRequest(
            initiator,
            target,
            arenaIndex,
            now + DuelRequestLifetime,
            lifestealPercent);
        string arenaMessage = (arenaIndex is int selectedArena
            ? $" on arena {selectedArena + 1} ({aimLOCK.DuelArenaNames[selectedArena]})"
            : string.Empty) +
            (lifestealPercent is { } requestedLifesteal
                ? $" with {requestedLifesteal:0.#}% lifesteal"
                : string.Empty);
        reply($"Duel request sent to {target.PlayerName}{arenaMessage}.");
        Chat.PrintToChat(
            target,
            $"{initiator.PlayerName} challenged you to a duel{arenaMessage}. Use /accept within 60 seconds.");
        return true;
    }

    public bool AcceptDuelRequest(
        CCitadelPlayerController target,
        Action<string> reply)
    {
        // In a duel: /accept takes the opponent's lifesteal proposal.
        if (
            this.lifestealProposals.Count != 0 &&
            this.server.PlayerManager.TryGetActiveMatch(target, out var currentMatch) &&
            this.lifestealProposals.TryGetValue(currentMatch, out var proposal))
        {
            return this.AcceptLifestealProposal(target, currentMatch, proposal, reply);
        }

        if (!this.pendingDuelRequests.TryGetValue(target.EntityHandle, out var request))
        {
            reply("You do not have a pending duel request.");
            return false;
        }

        if (request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            this.pendingDuelRequests.Remove(target.EntityHandle);
            reply("That duel request has expired. Ask them to send another.");
            return false;
        }

        if (
            !request.Initiator.IsValid ||
            !request.Target.IsValid ||
            request.Target.EntityHandle != target.EntityHandle)
        {
            this.pendingDuelRequests.Remove(target.EntityHandle);
            reply("That duel request is no longer valid.");
            return false;
        }

        if (!this.ValidateDuelParticipants(request.Initiator, target, reply))
        {
            this.pendingDuelRequests.Remove(target.EntityHandle);
            return false;
        }

        if (!this.TryPickArena(request.ArenaIndex, out int arenaIndex))
        {
            reply(
                request.ArenaIndex is int requestedArena
                    ? $"Arena {requestedArena + 1} ({aimLOCK.DuelArenaNames[requestedArena]}) is currently in use. Try again when it is free."
                    : "All duel arenas are currently in use. Try again when one is free.");
            return false;
        }

        if (!this.StartMatch(
            request.Initiator,
            target,
            MatchQueueType.Unranked,
            arenaIndex))
        {
            return false;
        }

        this.pendingDuelRequests.Remove(target.EntityHandle);
        if (
            request.LifestealPercent is { } agreedLifesteal &&
            this.server.PlayerManager.TryGetActiveMatch(target, out var startedMatch))
        {
            startedMatch.LifestealPercent = agreedLifesteal;
        }

        string arenaMessage =
            $" on arena {arenaIndex + 1} ({aimLOCK.DuelArenaNames[arenaIndex]})" +
            (request.LifestealPercent is { } shownLifesteal
                ? $" with {shownLifesteal:0.#}% lifesteal"
                : string.Empty);
        reply(
            $"Accepted {request.Initiator.PlayerName}'s duel request{arenaMessage}. Starting match...");
        Chat.PrintToChat(
            request.Initiator,
            $"{target.PlayerName} accepted your duel request{arenaMessage}. Starting match...");
        return true;
    }

    /// <summary>
    /// /duel ls N during a duel: against a bot it applies right away; against a player
    /// it waits for the opponent's /accept (60 seconds).
    /// </summary>
    public void ProposeMatchLifesteal(
        CCitadelPlayerController proposer,
        float percent,
        Action<string> reply)
    {
        if (
            !this.server.PlayerManager.TryGetActiveMatch(proposer, out var match) ||
            match.Finished)
        {
            reply("You are not in a duel. To set it when challenging: /duel <player name> ls " +
                $"{percent:0.#}.");
            return;
        }

        var opponent = this.server.PlayerManager.FindMatchParticipant(
            string.Equals( match.Player1, this.server.PlayerManager.GetMatchParticipantName(proposer), StringComparison.Ordinal) ? match.Player2 : match.Player1);
        if (opponent == null || opponent.IsBot)
        {
            this.SetMatchLifesteal(match, percent, $"This duel's lifesteal is now {percent:0.#}%.");
            return;
        }

        this.lifestealProposals[match] = new LifestealProposal(
            proposer.EntityHandle,
            proposer.PlayerName,
            percent,
            DateTimeOffset.UtcNow + DuelRequestLifetime);
        reply($"Proposed {percent:0.#}% lifesteal to {opponent.PlayerName}.");
        Chat.PrintToChat(
            opponent,
            $"{proposer.PlayerName} wants this duel's lifesteal at {percent:0.#}%. Use /accept within 60 seconds.");
    }

    public void OnMatchFinished(DuelMatch match) => this.lifestealProposals.Remove(match);

    public void OnPlayerDisconnect(CCitadelPlayerController player)
    {
        uint playerHandle = player.EntityHandle;
        var requestsToRemove = this.pendingDuelRequests
            .Where(request =>
                request.Key == playerHandle ||
                request.Value.Initiator.EntityHandle == playerHandle)
            .Select(request => request.Key)
            .ToArray();

        foreach (uint targetHandle in requestsToRemove)
        {
            this.pendingDuelRequests.Remove(targetHandle);
        }
    }

    public void TryStartMatches()
    {
        this.TryStartMatch(MatchQueueType.Ranked);
        this.TryStartMatch(MatchQueueType.Unranked);
    }

    public void TryStartMatch(MatchQueueType queueType)
    {
        var queue = this.GetQueue(queueType);

        if (queue.Count < 2)
        {
            return;
        }

        bool hasArena = this.TryGetAvailableArena(out int arenaIndex);

        var pair = this.SelectBestPair(this.GetMatchableQueuedPlayers(queue));
        if (pair is not var (first, second))
        {
            return;
        }

        var player1 = first.Player!;
        var player2 = second.Player!;

        /* End practice bot matches; finishing a match runs the queue again */
        foreach (var player in new[] { player1, player2 })
        {
            if (!this.server.PlayerManager.TryGetActiveMatch(player, out _))
            {
                continue;
            }

            Chat.PrintToChat(player, "Match found! Ending your bot match.");
            this.server.PlayerManager.ForfeitMatch(
                this.server.PlayerManager.GetMatchParticipantName(player));
            return;
        }

        if (!hasArena)
        {
            return;
        }

        if (!StartMatch(player1, player2, queueType, arenaIndex))
        {
            return;
        }

        // The later queue position first, so the earlier index stays valid.
        queue.RemoveAt(second.QueueIndex);
        queue.RemoveAt(first.QueueIndex);

        this.server.UIManager.RefreshHud();
        this.TryStartMatch(queueType);
    }

    public bool TryGetAvailableArena(out int arenaIndex)
    {
        // Runs on every queue change; plain loops instead of building a HashSet and
        // two arrays each time. An arena held for a bot match being set up counts as
        // taken.
        int arenaCount = this.server.DuelLocations.Length;
        Span<bool> taken = arenaCount <= 64 ? stackalloc bool[arenaCount] : new bool[arenaCount];
        foreach (var match in this.server.ActiveMatches.Values)
        {
            if ((uint)match.ArenaIndex < (uint)arenaCount)
            {
                taken[match.ArenaIndex] = true;
            }
        }

        int availableCount = 0;
        for (int i = 0; i < arenaCount; i++)
        {
            if (taken[i] || this.server.PlayerManager.IsArenaReserved(i))
            {
                taken[i] = true;
            }
            else
            {
                availableCount++;
            }
        }

        if (availableCount == 0)
        {
            arenaIndex = -1;
            return false;
        }

        int pick = this.server.Random.Next(availableCount);
        for (int i = 0; i < arenaCount; i++)
        {
            if (taken[i])
            {
                continue;
            }

            if (pick-- == 0)
            {
                arenaIndex = i;
                return true;
            }
        }

        arenaIndex = -1;
        return false;
    }

    public void StartCountdown(DuelMatch match)
    {
        if (
            match.Finished ||
            match.CountdownActive ||
            !match.ArenaInitialized)
        {
            return;
        }

        match.CountdownActive = true;
        match.CombatActive = false;
        int countdownGeneration = ++match.CountdownGeneration;

        // Every duel and every round starts from zero Fixation stacks and full health.
        this.server.PlayerManager.ResetRoundState(match);
        this.server.PlayerManager.SetMatchDisarmed(match, true);

        this.PrintCountdownMessage(match, "[DUEL] 3...");

        this.server.ScheduleOnce(1.Seconds(), () =>
        {
            if (IsCurrentCountdown(match, countdownGeneration))
            {
                this.PrintCountdownMessage(match, "[DUEL] 2...");
            }
        });

        this.server.ScheduleOnce(2.Seconds(), () =>
        {
            if (IsCurrentCountdown(match, countdownGeneration))
            {
                this.PrintCountdownMessage(match, "[DUEL] 1...");
            }
        });

        this.server.ScheduleOnce(3.Seconds(), () =>
        {
            if (!IsCurrentCountdown(match, countdownGeneration))
            {
                return;
            }

            var player1 = this.server.PlayerManager.FindMatchParticipant(
                match.Player1);
            var player2 = this.server.PlayerManager.FindMatchParticipant(
                match.Player2);
            var pawn1 = player1?.GetHeroPawn();
            var pawn2 = player2?.GetHeroPawn();
            if (
                pawn1 is not { IsValid: true, IsAlive: true } ||
                pawn2 is not { IsValid: true, IsAlive: true })
            {
                match.CountdownActive = false;
                this.server.PlayerManager.SetMatchDisarmed(match, true);
                return;
            }

            // Fixation cleared and full health again right as the round goes live: a
            // bullet still in the air from the last round can land during the countdown
            // and put stacks back, and late respawn health or max-health updates can
            // change health.
            this.server.PlayerManager.ResetRoundState(match);
            PlayerManager.RemoveAllDisarms(pawn1);
            PlayerManager.RemoveAllDisarms(pawn2);
            this.PrintCountdownMessage(match, "[DUEL] FIGHT!");
            match.CountdownActive = false;
            match.CombatActive = true;
        });
    }

    private static int GetAllowedPingDifference(double oldestWaitSeconds)
    {
        if (oldestWaitSeconds >= 120)
        {
            return int.MaxValue;
        }

        int wideningSteps = (int)(oldestWaitSeconds / 15);
        return Math.Min(60, 6 + (wideningSteps * 3));
    }

    private static bool IsCurrentCountdown(
        DuelMatch match,
        int countdownGeneration) =>
        !match.Finished &&
        match.CountdownActive &&
        match.CountdownGeneration == countdownGeneration;

    private bool AcceptLifestealProposal(
        CCitadelPlayerController target,
        DuelMatch match,
        LifestealProposal proposal,
        Action<string> reply)
    {
        if (proposal.ProposerHandle == target.EntityHandle)
        {
            reply("Your opponent has to accept your lifesteal proposal.");
            return false;
        }

        this.lifestealProposals.Remove(match);
        if (proposal.ExpiresAt <= DateTimeOffset.UtcNow || match.Finished)
        {
            reply("That lifesteal proposal has expired.");
            return false;
        }

        this.SetMatchLifesteal(
            match,
            proposal.Percent,
            $"{target.PlayerName} accepted: this duel's lifesteal is now {proposal.Percent:0.#}%.");
        return true;
    }

    private void SetMatchLifesteal(DuelMatch match, float percent, string message)
    {
        match.LifestealPercent = percent;
        foreach (string name in new[] { match.Player1, match.Player2 })
        {
            var player = this.server.PlayerManager.FindMatchParticipant(name);
            if (player is { IsBot: false })
            {
                Chat.PrintToChat(player, message);
            }
        }
    }

    private bool ValidateDuelParticipants(
        CCitadelPlayerController initiator,
        CCitadelPlayerController target,
        Action<string> reply)
    {
        if (this.server.PlayerManager.TryGetActiveMatch(initiator, out _))
        {
            reply($"{initiator.PlayerName} is already in a match.");
            return false;
        }

        if (this.server.PlayerManager.IsSpectating(initiator))
        {
            reply($"{initiator.PlayerName} must stop spectating before starting a duel.");
            return false;
        }

        if (this.server.PlayerManager.TryGetActiveMatch(target, out _))
        {
            reply($"{target.PlayerName} is already in a match.");
            return false;
        }

        if (this.server.PlayerManager.IsSpectating(target))
        {
            reply($"{target.PlayerName} must stop spectating before starting a duel.");
            return false;
        }

        bool eitherQueued = this.server.RankedQueue
            .Concat(this.server.UnrankedQueue)
            .Any(entry =>
                entry.SteamId == initiator.PlayerSteamId ||
                entry.SteamId == target.PlayerSteamId);
        if (eitherQueued)
        {
            reply("Both players must leave their queues before starting a duel.");
            return false;
        }

        if (
            initiator.GetHeroPawn() is not { IsValid: true, IsAlive: true } ||
            target.GetHeroPawn() is not { IsValid: true, IsAlive: true })
        {
            reply("Both players must have a living hero to start a duel.");
            return false;
        }

        return true;
    }

    private void PruneExpiredDuelRequests(DateTimeOffset now)
    {
        var requestsToRemove = this.pendingDuelRequests
            .Where(request =>
                request.Value.ExpiresAt <= now ||
                !request.Value.Initiator.IsValid ||
                !request.Value.Target.IsValid)
            .Select(request => request.Key)
            .ToArray();

        foreach (uint targetHandle in requestsToRemove)
        {
            this.pendingDuelRequests.Remove(targetHandle);
        }
    }

    /* Queued players who are online and not in a duel (a bot match does not block) */
    private QueuedPlayer[] GetMatchableQueuedPlayers(List<QueueEntry> queue)
    {
        var playersBySteamId = Players.GetAll()
            .Where(player => !player.IsBot)
            .GroupBy(player => player.PlayerSteamId)
            .ToDictionary(
                group => group.Key,
                group => group.First());
        return queue
            .Select((entry, index) => new QueuedPlayer(
                entry,
                index,
                playersBySteamId.TryGetValue(entry.SteamId, out var player)
                    ? player
                    : null))
            .Where(item =>
                item.Player != null &&
                (
                    !this.server.PlayerManager.TryGetActiveMatch(item.Player, out var match) ||
                    this.server.PlayerManager.IsBotMatch(match)))
            .ToArray();
    }

    /* Best pair: longest wait first, then smallest ping gap, then queue order */
    private (QueuedPlayer First, QueuedPlayer Second)? SelectBestPair(
        QueuedPlayer[] queuedPlayers)
    {
        (QueuedPlayer First, QueuedPlayer Second)? best = null;
        DateTimeOffset bestOldestEntry = DateTimeOffset.MaxValue;
        long bestPingDifference = long.MaxValue;
        int bestIndexSum = int.MaxValue;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < queuedPlayers.Length; i++)
        {
            var first = queuedPlayers[i];
            for (int j = i + 1; j < queuedPlayers.Length; j++)
            {
                var second = queuedPlayers[j];
                DateTimeOffset oldestEntry =
                    first.Entry.EnqueuedAt <= second.Entry.EnqueuedAt
                        ? first.Entry.EnqueuedAt
                        : second.Entry.EnqueuedAt;
                if (
                    !IsPingCompatible(
                        first.Player!,
                        second.Player!,
                        (now - oldestEntry).TotalSeconds,
                        out long pingDifference))
                {
                    continue;
                }

                int indexSum = first.QueueIndex + second.QueueIndex;
                bool isBetter = oldestEntry < bestOldestEntry
                    || (oldestEntry == bestOldestEntry
                        && pingDifference < bestPingDifference)
                    || (oldestEntry == bestOldestEntry
                        && pingDifference == bestPingDifference
                        && indexSum < bestIndexSum);
                if (!isBetter)
                {
                    continue;
                }

                best = (first, second);
                bestOldestEntry = oldestEntry;
                bestPingDifference = pingDifference;
                bestIndexSum = indexSum;
            }
        }

        return best;
    }

    /* With ping matching on for both, the ping gap must fit the window for their wait */
    private bool IsPingCompatible(
        CCitadelPlayerController firstPlayer,
        CCitadelPlayerController secondPlayer,
        double oldestWaitSeconds,
        out long pingDifference)
    {
        pingDifference = 0;
        bool usePingMatching =
            this.server.PlayerManager.IsPingBasedMatchmakingEnabled(firstPlayer) &&
            this.server.PlayerManager.IsPingBasedMatchmakingEnabled(secondPlayer);
        if (firstPlayer.IsBot || secondPlayer.IsBot || !usePingMatching)
        {
            return true;
        }

        int allowedDifference = GetAllowedPingDifference(
            Math.Max(0d, oldestWaitSeconds));
        if (
            this.server.PingManager.TryGetPing(firstPlayer, out int firstPing) &&
            this.server.PingManager.TryGetPing(secondPlayer, out int secondPing))
        {
            pingDifference = Math.Abs((long)firstPing - secondPing);
            return pingDifference <= allowedDifference;
        }

        // No ping reading: only pair once the window is fully open.
        return allowedDifference == int.MaxValue;
    }

    private bool IsArenaAvailable(int arenaIndex)
    {
        if (
            arenaIndex < 0 ||
            arenaIndex >= this.server.DuelLocations.Length ||
            this.server.PlayerManager.IsArenaReserved(arenaIndex))
        {
            return false;
        }

        foreach (var match in this.server.ActiveMatches.Values)
        {
            if (match.ArenaIndex == arenaIndex)
            {
                return false;
            }
        }

        return true;
    }

    private bool StartMatch(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        MatchQueueType queueType,
        int? selectedArenaIndex = null,
        bool alreadyInArena = false)
    {
        if (!this.TryPickArena(selectedArenaIndex, out int arenaIndex))
        {
            return false;
        }

        if (player1.IsBot)
        {
            this.server.PlayerManager.ResetPracticeBotRecord(player1);
        }

        if (player2.IsBot)
        {
            this.server.PlayerManager.ResetPracticeBotRecord(player2);
        }

        if (!player1.IsBot && !player2.IsBot)
        {
            this.AnnounceMatchFound(player1, player2, queueType);
        }

        Console.WriteLine(
            $"Starting match: {player1.PlayerName} vs {player2.PlayerName}");

        DuelMatch match = this.CreateMatch(player1, player2, queueType, arenaIndex);
        this.server.PendingRespawns.Remove(match.Player1);
        this.server.PendingRespawns.Remove(match.Player2);
        this.server.ActiveMatches[match.Player1] = match;
        this.server.ActiveMatches[match.Player2] = match;
        this.server.LeaderboardManager.RefreshActiveMatches();

        // A bot match whose player is already standing in the arena skips the 2 s
        // wait: there is nowhere to teleport to. InitializeMatchInArena shows its HUD;
        // building it here too sent every panel twice within two frames.
        if (alreadyInArena)
        {
            this.server.ScheduleNextTick(
                () => this.InitializeMatchInArena(match, player1, player2));
        }
        else
        {
            this.server.UIManager.ShowMatchHud(match);
            this.server.ScheduleOnce(
                2.Seconds(),
                () => this.InitializeMatchInArena(match, player1, player2));
        }

        return true;
    }

    private bool TryPickArena(int? selectedArenaIndex, out int arenaIndex)
    {
        if (selectedArenaIndex is int selectedIndex)
        {
            arenaIndex = selectedIndex;
            return this.IsArenaAvailable(selectedIndex);
        }

        return this.TryGetAvailableArena(out arenaIndex);
    }

    private void AnnounceMatchFound(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        MatchQueueType queueType)
    {
        string[] lines =
        {
            "  [MATCH FOUND]",
            this.GetMatchFoundPlayerLine(player1, queueType),
            this.GetMatchFoundPingLine(player1),
            "                 VS",
            this.GetMatchFoundPlayerLine(player2, queueType),
            this.GetMatchFoundPingLine(player2),
        };

        // Centre every line on the widest one.
        int width = lines.Max(line => line.Length);
        string message = string.Join(
            "\n",
            lines.Select(line => line.PadLeft((width + line.Length) / 2)));
        Chat.PrintToChat(player1, message);
        Chat.PrintToChat(player2, message);
    }

    private DuelMatch CreateMatch(
        CCitadelPlayerController player1,
        CCitadelPlayerController player2,
        MatchQueueType queueType,
        int arenaIndex)
    {
        var playerManager = this.server.PlayerManager;
        return new DuelMatch
        {
            Player1 = playerManager.GetMatchParticipantName(player1),
            Player2 = playerManager.GetMatchParticipantName(player2),
            Player1ItemsBeforeMatch =
                PlayerManager.GetOwnedDuelItems(player1.GetHeroPawn()),
            Player2ItemsBeforeMatch =
                PlayerManager.GetOwnedDuelItems(player2.GetHeroPawn()),
            QueueType = queueType,
            ArenaPosition = this.server.DuelLocations[arenaIndex],
            ArenaIndex = arenaIndex,
            BuyingAllowed =
                player1.IsBot ||
                player2.IsBot ||
                (
                    playerManager.AllowsDuelBuying(player1) &&
                    playerManager.AllowsDuelBuying(player2)),
        };
    }

    private void InitializeMatchInArena(
        DuelMatch match,
        CCitadelPlayerController player1,
        CCitadelPlayerController player2)
    {
        if (match.Finished)
        {
            return;
        }

        PlayerManager.EnsureTeam(player1, 2);
        PlayerManager.EnsureTeam(player2, 3);

        var pawn1 = player1.IsValid ? player1.GetHeroPawn() : null;
        var pawn2 = player2.IsValid ? player2.GetHeroPawn() : null;
        if (pawn1 == null || pawn2 == null)
        {
            Console.WriteLine("A player pawn is null.");
            this.CancelMatch(match, player1, player2);
            return;
        }

        var playerManager = this.server.PlayerManager;
        pawn1.RemoveModifier("modifier_speed_boost");
        pawn2.RemoveModifier("modifier_speed_boost");

        var (position1, angles1) = playerManager.GetDuelSpawn(match, isPlayer1: true);
        var (position2, angles2) = playerManager.GetDuelSpawn(match, isPlayer1: false);
        pawn1.Teleport(position1, angles1, null);
        pawn2.Teleport(position2, angles2, null);
        playerManager.ConfigurePracticeBotStrafe(pawn1, pawn2);
        this.server.UIManager.ShowMatchHud(match);

        playerManager.ApplyDuelMatchModifiers(pawn1);
        playerManager.ApplyDuelMatchModifiers(pawn2);
        playerManager.ApplyDuelBulletLifesteal(pawn1);
        playerManager.ApplyDuelBulletLifesteal(pawn2);

        // The item stack is the biggest batch of changes per pawn; do the second
        // pawn on the next tick so both don't land in the same server frame.
        playerManager.ApplyDuelItemStack(pawn1);
        pawn1.RemoveAbility("upgrade_infinite_rounds");
        this.server.ScheduleNextTick(() =>
        {
            if (match.Finished || !pawn2.IsValid)
            {
                return;
            }

            this.server.PlayerManager.ApplyDuelItemStack(pawn2);
            pawn2.RemoveAbility("upgrade_infinite_rounds");
        });

        match.ArenaInitialized = true;
        playerManager.ShowArenaScoreboard(match);
        playerManager.StartDuelHealthCapMonitor(match);
        playerManager.SetMatchDisarmed(match, true);
        this.server.ScheduleOnce(0.25.Seconds(), () =>
        {
            if (match.Finished)
            {
                return;
            }

            this.server.PlayerManager.PrepareDuelMatchHealth(match);
            this.server.EnableMatchInfiniteAmmo();
            this.StartCountdown(match);
        });

        Console.WriteLine(
            $"Match Found! {player1.PlayerName} vs {player2.PlayerName}");
    }

    /* A player lost their pawn before the match could start */
    private void CancelMatch(
        DuelMatch match,
        CCitadelPlayerController player1,
        CCitadelPlayerController player2)
    {
        match.Finished = true;
        this.server.ActiveMatches.Remove(match.Player1);
        this.server.ActiveMatches.Remove(match.Player2);
        this.server.PendingRespawns.Remove(match.Player1);
        this.server.PendingRespawns.Remove(match.Player2);
        if (player1.IsValid)
        {
            player1.ChangeTeam(2);
        }

        if (player2.IsValid)
        {
            player2.ChangeTeam(2);
        }

        this.server.UIManager.RestoreHudAfterMatch(match);
        this.server.PlayerManager.OnDuelMatchFinished(match);
        this.TryStartMatches();
    }

    private List<QueueEntry> GetQueue(MatchQueueType queueType) =>
        queueType switch
        {
            MatchQueueType.Ranked => this.server.RankedQueue,
            MatchQueueType.Unranked => this.server.UnrankedQueue,
            _ => throw new ArgumentOutOfRangeException(nameof(queueType)),
        };

    private string GetMatchFoundPlayerLine(
        CCitadelPlayerController player,
        MatchQueueType queueType)
    {
        string displayName = this.server.GuildManager.FormatPlayerNameWithSelectedTag(player);
        if (player.IsBot)
        {
            return displayName;
        }

        if (queueType == MatchQueueType.Ranked)
        {
            RankedEloStats stats = this.server.RankedElo.GetStats(
                this.server.PlayerManager.GetRankedEloId(player),
                player.PlayerName);
            int games = stats.Wins + stats.Losses;
            float winRate = games > 0
                ? (float)stats.Wins / games * 100f
                : 0f;

            return $"{displayName} | {stats.Rating} Elo | " +
                $"{stats.Wins}-{stats.Losses} | {winRate:F1}% WR";
        }

        PlayerRecord record = this.server.PlayerManager.GetRecord(player);
        int unrankedGames = record.Wins + record.Losses;
        float unrankedWinRate = unrankedGames > 0
            ? (float)record.Wins / unrankedGames * 100f
            : 0f;

        return $"{displayName} | {record.Wins}-{record.Losses} | " +
            $"{unrankedWinRate:F1}% WR";
    }

    private string GetMatchFoundPingLine(CCitadelPlayerController player) =>
        this.server.PingManager.TryGetPing(player, out int pingMs)
            ? $"      Ping: {pingMs} ms"
            : "           Ping: N/A";

    private void PrintCountdownMessage(DuelMatch match, string message)
    {
        foreach (string participantName in new[] { match.Player1, match.Player2 })
        {
            var participant = this.server.PlayerManager.FindMatchParticipant(
                participantName);
            if (participant is { IsValid: true, IsBot: false })
            {
                Chat.PrintToChat(participant, message);
            }
        }
    }

    private sealed record DuelRequest(
        CCitadelPlayerController Initiator,
        CCitadelPlayerController Target,
        int? ArenaIndex,
        DateTimeOffset ExpiresAt,
        float? LifestealPercent = null);

    // A lifesteal change proposed during a duel (/duel ls N), waiting for the opponent's
    // /accept. Keyed by the match.
    private sealed record LifestealProposal(
        uint ProposerHandle,
        string ProposerName,
        float Percent,
        DateTimeOffset ExpiresAt);

    private sealed record QueuedPlayer(
        QueueEntry Entry,
        int QueueIndex,
        CCitadelPlayerController? Player);
}