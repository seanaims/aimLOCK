namespace AimLOCK;

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

public partial class PlayerManager
{
    private const string PracticeBotMatchKeyPrefix = "__aimlock_bot_";

    private readonly HashSet<string> respawnRecoveryInProgress =
        new(StringComparer.OrdinalIgnoreCase);

    public void OnDuelMatchFinished(DuelMatch match)
    {
        this.server.Queue.OnMatchFinished(match);
        var bot = this.practiceBots.Values.FirstOrDefault(state =>
            string.Equals(
                match.Player1,
                state.MatchKey,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                match.Player2,
                state.MatchKey,
                StringComparison.OrdinalIgnoreCase));
        if (bot != null)
        {
            this.RemovePracticeBot(bot.ControllerHandle);
        }

        this.ResetArenaScoreboardWhenParticipantsLeave(match);
    }

    public bool TryGetActiveMatch(
        CBasePlayerController player,
        out DuelMatch match)
    {
        // Called from the usercmd, damage and HUD paths: no work when nothing is
        // running, and the cached name instead of a fresh native string read.
        if (this.server.ActiveMatches.Count == 0)
        {
            match = null!;
            return false;
        }

        if (
            this.practiceBots.Count != 0 &&
            this.practiceBots.TryGetValue(player.EntityHandle, out var bot) &&
            this.server.ActiveMatches.TryGetValue(bot.MatchKey, out match!))
        {
            return true;
        }

        if (this.server.ActiveMatches.TryGetValue(this.GetMatchParticipantName(player), out match!))
        {
            return true;
        }

        match = null!;
        return false;
    }

    public bool IsBotMatch(DuelMatch match) =>
        this.FindMatchParticipant(match.Player1)?.IsBot == true ||
        this.FindMatchParticipant(match.Player2)?.IsBot == true;

    public string GetMatchParticipantName(CBasePlayerController player) =>
        this.practiceBots.TryGetValue(player.EntityHandle, out var bot) ? bot.MatchKey : ( player as CCitadelPlayerController ?? Players.GetAll().FirstOrDefault(candidate => candidate.EntityHandle == player.EntityHandle)) is { } controller ? controller.PlayerSteamId.ToString(CultureInfo.InvariantCulture) : player.PlayerName;

    public CCitadelPlayerController? FindMatchParticipant(
        string participantName)
    {
        return FindMatchParticipant(participantName, Players.GetAll());
    }

    public HookResult OnPlayerDeath(PlayerDeathEvent args)
    {
        var victim = args.UseridController;

        if (victim == null)
        {
            return HookResult.Continue;
        }

        if (!TryGetActiveMatch(victim, out var match))
        {
            return HookResult.Continue;
        }

        if (match.Finished || !match.ArenaInitialized)
        {
            return HookResult.Continue;
        }

        // Fixation resets every kill, in bot matches and player duels alike. Next tick,
        // so it doesn't add to the death frame.
        this.server.ScheduleNextTick(() => this.ResetMatchFixationStacks(match));

        bool scoreRound = match.CombatActive;
        if (match.CountdownActive)
        {
            match.CountdownActive = false;
            match.CountdownGeneration++;
        }

        this.SetMatchDisarmed(match, true);

        if (scoreRound)
        {
            string victimName = GetMatchParticipantName(victim);
            if (string.Equals(
                victimName,
                match.Player1,
                StringComparison.OrdinalIgnoreCase))
            {
                match.Score2++;
            }
            else
            {
                match.Score1++;
            }
        }

        if (scoreRound)
        {
            this.server.LeaderboardManager.RefreshActiveMatches();
            this.server.UIManager.UpdateMatchHud(match);
            this.UpdateArenaScoreboard(match);
            this.ReportRoundAccuracy(match);
        }

        if (
            scoreRound &&
            (
                match.Score1 >= aimLOCK.WINSCORE ||
                match.Score2 >= aimLOCK.WINSCORE))
        {
            match.Finished = true;
            match.CountdownActive = false;
            match.CountdownGeneration++;
            this.server.ScheduleOnce(3.Seconds(), () =>
            {
                this.FinishMatch(match);
            });

            return HookResult.Continue;
        }

        this.server.ScheduleOnce(0.5.Seconds(), () =>
        {
            if (match.Finished)
            {
                return;
            }

            this.ResetDuelCurrentHealth(match);
        });

        string participantName = GetMatchParticipantName(victim);
        this.BeginMatchRespawnRecovery(match, participantName);

        return HookResult.Continue;
    }

    public HookResult OnPlayerRespawned(PlayerRespawnedEvent args)
    {
        var controller = args.Userid?.Controller as CBasePlayerController;

        if (controller == null)
        {
            return HookResult.Continue;
        }

        if (
            controller is CCitadelPlayerController spectatingPlayer &&
            this.IsSpectating(spectatingPlayer))
        {
            var spectatorPawn = spectatingPlayer.GetHeroPawn();
            if (spectatorPawn is { IsValid: true })
            {
                spectatorPawn.AddModifier("modifier_citadel_disarmed");
            }
        }

        if (controller is CCitadelPlayerController player && !player.IsBot)
        {
            this.server.PlayerTagManager.RefreshPlayerTag(player);
        }

        if (
            controller is CCitadelPlayerController connectingPlayer &&
            this.initialHeroSelectionPending.Contains(connectingPlayer.EntityHandle) &&
            this.initialHeroSelectionIssued.Contains(connectingPlayer.EntityHandle))
        {
            this.server.ScheduleOnce(0.25.Seconds(), () =>
                this.WaitForInitialHazeSpawn(connectingPlayer, 4));
        }

        if (controller is CCitadelPlayerController fixationPlayer)
        {
            this.server.ScheduleOnce(0.25.Seconds(), () =>
            {
                if (!fixationPlayer.IsValid)
                {
                    return;
                }

                var pawn = fixationPlayer.GetHeroPawn();
                if (pawn is { IsValid: true })
                {
                    ApplyHazeLoadout(pawn);
                }
            });
        }

        string participantName = this.GetMatchParticipantName(controller);
        if (
            this.TryGetActiveMatch(controller, out var activeMatch) &&
            !activeMatch.ArenaInitialized)
        {
            this.server.PendingRespawns.Remove(participantName);
            return HookResult.Continue;
        }

        if (!this.server.PendingRespawns.TryGetValue(
            participantName,
            out var match))
        {
            return HookResult.Continue;
        }

        if (match.Finished)
        {
            return HookResult.Continue;
        }

        this.BeginMatchRespawnRecovery(match, participantName);

        return HookResult.Continue;
    }

    /// <summary>
    /// Start of every duel and every round (the countdown): both players' Fixation
    /// stacks are cleared and both are set to full health.
    /// </summary>
    public void ResetRoundState(DuelMatch match)
    {
        this.ResetMatchFixationStacks(match);
        this.FillMatchHealth(match);
        this.ResetRoundAccuracy(match);
    }

    public bool ForfeitMatch(string playerName)
    {
        if (
            !this.server.ActiveMatches.TryGetValue(playerName, out var match) ||
            match.Finished)
        {
            return false;
        }

        if (
            !StringComparer.OrdinalIgnoreCase.Equals(playerName, match.Player1) &&
            !StringComparer.OrdinalIgnoreCase.Equals(playerName, match.Player2))
        {
            return false;
        }

        match.ForfeitingPlayer = playerName;
        match.Finished = true;
        this.FinishMatch(match);
        return true;
    }

    public void SetMatchDisarmed(DuelMatch match, bool disarmed)
    {
        var onlinePlayers = Players.GetAll().ToArray();
        SetMatchDisarmed(
            match,
            disarmed,
            FindMatchParticipant(match.Player1, onlinePlayers),
            FindMatchParticipant(match.Player2, onlinePlayers));
    }

    private static bool DidPlayer1Win(DuelMatch match)
    {
        if (match.ForfeitingPlayer is { } forfeitingPlayer)
        {
            return !StringComparer.OrdinalIgnoreCase.Equals(
                forfeitingPlayer,
                match.Player1);
        }

        return match.Score1 > match.Score2;
    }

    private static void SetMatchDisarmed(
        DuelMatch match,
        bool disarmed,
        CCitadelPlayerController? player1,
        CCitadelPlayerController? player2)
    {
        match.CombatActive = !disarmed && !match.Finished;

        if (player1 is { IsValid: true })
        {
            var pawn = player1.GetHeroPawn();
            if (pawn != null)
            {
                if (disarmed)
                {
                    pawn.AddModifier(DisarmedModifierName);
                }
                else
                {
                    RemoveAllDisarms(pawn);
                }
            }
        }

        if (
            player2 is { IsValid: true } &&
            (player1 == null || player2.EntityHandle != player1.EntityHandle))
        {
            var pawn = player2.GetHeroPawn();
            if (pawn != null)
            {
                // Re-arming removes every copy, so repeated disarms (death, respawn,
                // countdown) can't pile up and leave someone disarmed.
                if (disarmed)
                {
                    pawn.AddModifier(DisarmedModifierName);
                }
                else
                {
                    RemoveAllDisarms(pawn);
                }
            }
        }
    }

    private static string FormatEloChange(int change) =>
        change > 0 ? $"+{change}" : change.ToString(CultureInfo.InvariantCulture);

    private CCitadelPlayerController? FindMatchParticipant(
        string participantName,
        IEnumerable<CCitadelPlayerController> players)
    {
        // Called many times per round (HUD, countdown, respawns, damage checks):
        // one pass, no LINQ, no native string read per player.
        bool hasSteamId = ulong.TryParse(
            participantName,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ulong steamId);
        uint botHandle = 0;
        if (
            this.practiceBots.Count != 0 &&
            participantName.StartsWith(PracticeBotMatchKeyPrefix, StringComparison.Ordinal))
        {
            foreach (var state in this.practiceBots.Values)
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
                {
                    return player;
                }

                continue;
            }

            if (!player.IsBot && hasSteamId && player.PlayerSteamId == steamId)
            {
                return player;
            }
        }

        return null;
    }

    private string GetOfflineParticipantName(string participantName) =>
        ulong.TryParse(
            participantName,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ulong steamId) &&
        this.server.PlayerRecords.TryGetValue(steamId, out var record) &&
        !string.IsNullOrWhiteSpace(record.LastKnownName)
            ? record.LastKnownName
            : participantName;

    private IEnumerable<CCitadelPlayerController> GetMatchParticipants(
        DuelMatch match)
    {
        var onlinePlayers = Players.GetAll().ToArray();
        var player1 = FindMatchParticipant(match.Player1, onlinePlayers);
        var player2 = FindMatchParticipant(match.Player2, onlinePlayers);

        if (player1 != null)
        {
            yield return player1;
        }

        if (
            player2 != null &&
            (player1 == null || player2.EntityHandle != player1.EntityHandle))
        {
            yield return player2;
        }
    }

    private bool HasActiveMatchFor(string matchKey) =>
        this.server.ActiveMatches.TryGetValue(matchKey, out var match) && !match.Finished;

    private void BeginMatchRespawnRecovery(
        DuelMatch match,
        string participantName)
    {
        if (match.Finished || !match.ArenaInitialized)
        {
            return;
        }

        this.server.PendingRespawns[participantName] = match;
        if (!this.respawnRecoveryInProgress.Add(participantName))
        {
            return;
        }

        this.RespawnIntoArena(match, participantName);
    }

    private void RespawnIntoArena(
        DuelMatch match,
        string playerName,
        int retriesRemaining = 20,
        CCitadelPlayerController? cachedPlayer = null)
    {
        if (match.Finished)
        {
            this.respawnRecoveryInProgress.Remove(playerName);
            return;
        }

        var player = cachedPlayer is { IsValid: true }
            ? cachedPlayer
            : this.FindMatchParticipant(playerName);

        var pawn = player?.GetHeroPawn();

        if (
            player is not { IsValid: true } ||
            pawn is not { IsValid: true, IsAlive: true })
        {
            if (retriesRemaining <= 0)
            {
                this.respawnRecoveryInProgress.Remove(playerName);
                Console.WriteLine(
                    $"[aimLOCK] Could not restore {playerName} to the arena after respawn.");
                this.server.Queue.StartCountdown(match);
                return;
            }

            this.server.ScheduleOnce(0.25.Seconds(), () =>
                this.RespawnIntoArena(
                    match,
                    playerName,
                    retriesRemaining - 1,
                    player));
            return;
        }

        this.SetMatchDisarmed(match, true);

        bool isPlayer1 = string.Equals(
            playerName,
            match.Player1,
            StringComparison.OrdinalIgnoreCase);
        bool isPlayer2 = string.Equals(
            playerName,
            match.Player2,
            StringComparison.OrdinalIgnoreCase);
        if (isPlayer1 || isPlayer2)
        {
            var (position, angles) = this.GetDuelSpawn(match, isPlayer1);
            pawn.Teleport(position, angles, null);
            PlayMatchTeleportEffect(pawn);
        }

        this.server.PendingRespawns.Remove(playerName);
        this.respawnRecoveryInProgress.Remove(playerName);

        // The respawn frame only teleports and disarms. The modifier and item work is
        // spread over the next ticks: the player is disarmed until the countdown
        // anyway, and RestoreDuelRespawnHealth re-checks health afterwards.
        CCitadelPlayerPawn respawnedPawn = pawn;
        this.RunDuelStepsOverTicks(
            match,
            respawnedPawn,
            new Action[]
            {
                () => this.ApplyDuelBulletLifesteal(respawnedPawn),
                () =>
                {
                    this.ApplyDuelItemStack(respawnedPawn);
                    respawnedPawn.RemoveAbility("upgrade_infinite_rounds");
                },
                () =>
                {
                    ApplyMaxBoons(respawnedPawn);
                    if (!HasDuelMaxHealth(respawnedPawn)) { ApplyDuelMaxHealth(respawnedPawn); } SetDuelStartingHealth(respawnedPawn);
                },
            });

        this.server.ScheduleOnce(0.5.Seconds(), () =>
        {
            this.RestoreDuelRespawnHealth(match, playerName);
        });
    }

    /* Runs one step per tick; stops when the match ends or the pawn is gone */
    private void RunDuelStepsOverTicks(
        DuelMatch match,
        CCitadelPlayerPawn pawn,
        Action[] steps,
        int index = 0)
    {
        if (index >= steps.Length)
        {
            return;
        }

        this.server.ScheduleNextTick(() =>
        {
            if (match.Finished || !pawn.IsValid)
            {
                return;
            }

            steps[index]();
            this.RunDuelStepsOverTicks(match, pawn, steps, index + 1);
        });
    }

    private void FinishMatch(
        DuelMatch match)
    {
        match.Finished = true;
        match.CountdownActive = false;
        match.CountdownGeneration++;

        var onlinePlayers = Players.GetAll().ToArray();
        var player1 = FindMatchParticipant(match.Player1, onlinePlayers);
        var player2 = FindMatchParticipant(match.Player2, onlinePlayers);
        SetMatchDisarmed(match, true, player1, player2);

        bool player1Won = DidPlayer1Win(match);
        this.RecordMatchResult(
            match,
            player1Won ? player1 : player2,
            player1Won ? player2 : player1);
        string winner = player1Won ? match.Player1 : match.Player2;
        Console.WriteLine(
            $"Match Over! Winner: {this.GetMatchParticipantDisplayName(winner)}");

        SetMatchDisarmed(match, false, player1, player2);

        this.server.ActiveMatches.Remove(match.Player1);
        this.server.ActiveMatches.Remove(match.Player2);
        this.server.PendingRespawns.Remove(match.Player1);
        this.server.PendingRespawns.Remove(match.Player2);
        this.server.RestoreMatchInfiniteAmmoIfIdle();

        var pawn1 = player1?.GetHeroPawn();
        var pawn2 = player2?.GetHeroPawn();
        RestorePawnAfterDuel(pawn1, match.Player1ItemsBeforeMatch);
        RestorePawnAfterDuel(pawn2, match.Player2ItemsBeforeMatch);
        this.server.ScheduleOnce(0.1.Seconds(), () =>
        {
            ClampHealthToMax(pawn1);
            ClampHealthToMax(pawn2);
        });

        ReturnToSpawnAfterDuel(player1, pawn1);
        ReturnToSpawnAfterDuel(player2, pawn2);

        this.server.UIManager.HideMatchHud(player1, player2);
        this.OnDuelMatchFinished(match);
        this.server.UIManager.RestoreHudAfterMatch(match);
        this.ReturnMatchSpectators(match);
        this.server.Queue.TryStartMatches();
    }

    /* Bot matches and matches with a missing player are not recorded */
    private void RecordMatchResult(
        DuelMatch match,
        CCitadelPlayerController? winner,
        CCitadelPlayerController? loser)
    {
        if (winner == null || loser == null || winner.IsBot || loser.IsBot)
        {
            return;
        }

        if (match.QueueType == MatchQueueType.Ranked)
        {
            RecordRankedResult(match, winner, loser);
        }
        else
        {
            RecordUnrankedResult(winner, loser);
        }

        this.server.LeaderboardManager.RequestRefresh();
    }

    private void RecordUnrankedResult(
        CCitadelPlayerController winner,
        CCitadelPlayerController loser)
    {
        var winnerRecord = this.GetRecord(winner);
        var loserRecord = this.GetRecord(loser);

        winnerRecord.Wins++;
        loserRecord.Losses++;
        this.SaveRecords();

        Chat.PrintToChatAll(
            $"{winner.PlayerName} wins! " +
            $"Unranked record: {winnerRecord.Wins}-{winnerRecord.Losses}");
    }

    private void ReturnToSpawnAfterDuel(
        CCitadelPlayerController? player,
        CCitadelPlayerPawn? pawn)
    {
        // The practice bot is parked or removed right after this, so it skips the trip
        // to spawn, the teleport particles and the team change.
        if (IsPracticeBotController(player))
        {
            return;
        }

        if (pawn != null)
        {
            pawn.Teleport(
                aimLOCK.MidtownSpawnPosition,
                null,
                null);
            PlayMatchTeleportEffect(pawn);
        }

        EnsureTeam(player, 2);
    }

    private void RecordRankedResult(
        DuelMatch match,
        CCitadelPlayerController winner,
        CCitadelPlayerController loser)
    {
        bool player1Won = StringComparer.OrdinalIgnoreCase.Equals(
            GetMatchParticipantName(winner),
            match.Player1);
        CCitadelPlayerController player1 = player1Won ? winner : loser;
        CCitadelPlayerController player2 = player1Won ? loser : winner;

        try
        {
            EloMatchResult result = this.server.RankedElo.RecordMatchResult(
                this.GetRankedEloId(player1),
                player1.PlayerName,
                this.GetRankedEloId(player2),
                player2.PlayerName,
                player1Won);

            RankedEloStats player1Stats = this.server.RankedElo.GetStats(
                this.GetRankedEloId(player1),
                player1.PlayerName);
            RankedEloStats player2Stats = this.server.RankedElo.GetStats(
                this.GetRankedEloId(player2),
                player2.PlayerName);

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
                $"Elo {loserStats.Rating} ({FormatEloChange(loserChange)})");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Failed to record ranked result for {match.Player1} vs " +
                $"{match.Player2}: {ex}");
            Chat.PrintToChatAll(
                $"[RANKED] {winner.PlayerName} wins, but the Elo result " +
                "could not be saved.");
        }
    }
}
