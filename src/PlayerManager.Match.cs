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
    private readonly HashSet<string> respawnRecoveryInProgress =
        new(StringComparer.OrdinalIgnoreCase);

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

    private bool HasActiveMatchFor(string matchKey) =>
        server.ActiveMatches.TryGetValue(matchKey, out var match) && !match.Finished;

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
