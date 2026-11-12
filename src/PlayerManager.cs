/*
This file handels player activity and matches
forces haze, bot spawning, player connections, score tracking, match completion, respawning
*/

using System;
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

    private readonly aimLOCK server;

    public PlayerManager(aimLOCK server)
    {
        this.server = server;
    }

    public void OnPlayerConnect(ClientFullConnectEvent args)
    {
        if (args.Controller is not { } controller)
            return;

        Console.WriteLine(
            $"Forcing Haze for {controller.PlayerName}"
        );

        controller.ChangeTeam(2);
        controller.SelectHero(Heroes.Haze);

        // Spawn testing bot
        if (!server.botSpawned)
        {
            server.botSpawned = true;

            var pawn = controller.GetHeroPawn();
            var spawn = pawn?.Position ?? new Vector3(0, 45f, 0);

            var bot = Bots.Add(
                Heroes.Haze,
                team: 2,
                spawn,
                BotDifficulty.Hard
            );

            if (bot == null)
            {
                Console.WriteLine("BOT FAILED TO SPAWN");
            }
            else
            {
                string botName =
                    bot.Controller?.PlayerName ?? "Bot";

                server.Queue.MoveBotToQueue(
                    botName,
                    MatchQueueType.Unranked
                );
                server.UIManager.RefreshHud();
                server.Queue.TryStartMatch(MatchQueueType.Unranked);

                Console.WriteLine(
                    $"Bot {botName} added to queue."
                );
            }
        }

        server.UIManager.ShowQueuePanel(controller);
    }

    public HookResult OnPlayerDeath(PlayerDeathEvent args)
    {
        var victim = args.UseridController;
        var killer = args.AttackerController;

        if (victim == null || killer == null)
            return HookResult.Continue;

        if (!server.ActiveMatches.TryGetValue(
            victim.PlayerName,
            out var match))
        {
            return HookResult.Continue;
        }

        if (match.Finished)
            return HookResult.Continue;

        SetMatchDisarmed(match, true);

        if (killer.PlayerName == match.Player1)
            match.Score1++;

        if (killer.PlayerName == match.Player2)
            match.Score2++;

        server.UIManager.UpdateMatchHud(match);

        if (
            match.Score1 >= aimLOCK.WIN_SCORE ||
            match.Score2 >= aimLOCK.WIN_SCORE
        )
        {
            match.Finished = true;
            server.ScheduleOnce(3.Seconds(), () =>
            {
                FinishMatch(match);
            });

            return HookResult.Continue;
        }

        server.PendingRespawns[victim.PlayerName] = match;

        return HookResult.Continue;
    }

    public HookResult OnPlayerRespawned(PlayerRespawnedEvent args)
    {
        var player = args.Userid?.Controller;

        if (player == null)
            return HookResult.Continue;

        if (!server.PendingRespawns.TryGetValue(
            player.PlayerName,
            out var match))
        {
            return HookResult.Continue;
        }

        server.PendingRespawns.Remove(player.PlayerName);

        if (match.Finished)
            return HookResult.Continue;

        RespawnIntoArena(match, player.PlayerName);

        return HookResult.Continue;
    }

    private void RespawnIntoArena(DuelMatch match, string playerName)
    {
        SetMatchDisarmed(match, true);

        var player = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == playerName);

        var pawn = player?.GetHeroPawn();

        if (pawn == null)
            return;

        if (playerName == match.Player1)
        {
            pawn.Teleport(
                match.ArenaPosition + new Vector3(-200f, 0f, 0f),
                new Vector3(0f, 45f, 0f),
                null
            );
        }
        else if (playerName == match.Player2)
        {
            pawn.Teleport(
                match.ArenaPosition + new Vector3(200f, 0f, 0f),
                new Vector3(0f, 180f, 0f),
                null
            );
        }

        server.ScheduleOnce(0.5.Seconds(), () =>
        {
            server.Queue.StartCountdown(match);
        });
    }

    private void FinishMatch(DuelMatch match)
    {
        if (!match.Finished)
            match.Finished = true;

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

        var winnerController = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == winner);

        var loserController = Players.GetAll()
            .FirstOrDefault(x =>
                x.PlayerName == (winner == match.Player1
                    ? match.Player2
                    : match.Player1));

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

        var player1 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player1);

        var player2 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player2);

        SetMatchDisarmed(match, false);

        server.ActiveMatches.Remove(match.Player1);
        server.ActiveMatches.Remove(match.Player2);
        server.PendingRespawns.Remove(match.Player1);
        server.PendingRespawns.Remove(match.Player2);

        MatchQueueType botQueueType =
            server.BotQueuePreference ?? match.QueueType;
        if (player1?.IsBot == true)
            server.Queue.MoveBotToQueue(player1.PlayerName, botQueueType);

        if (player2?.IsBot == true)
            server.Queue.MoveBotToQueue(player2.PlayerName, botQueueType);

        player1?.GetHeroPawn()?.RemoveModifier(
            "upgrade_vampire/modifier_intrinsic_base"
        );

        player2?.GetHeroPawn()?.RemoveModifier(
            "upgrade_vampire/modifier_intrinsic_base"
        );

        player1?.GetHeroPawn()?.Teleport(
            aimLOCK.MidtownSpawnPosition,
            null,
            null
        );
        player2?.GetHeroPawn()?.Teleport(
            aimLOCK.MidtownSpawnPosition,
            null,
            null
        );

        player1?.ChangeTeam(2);
        player2?.ChangeTeam(2);

        server.UIManager.RestoreHudAfterMatch(match);
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

    private static void SetMatchDisarmed(DuelMatch match, bool disarmed)
    {
        foreach (var player in Players.GetAll().Where(player =>
            player.PlayerName == match.Player1 ||
            player.PlayerName == match.Player2))
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

        var recordFound = server.PlayerRecords.Values.FirstOrDefault(x =>
            x.LastKnownName.Equals(
                targetName,
                StringComparison.OrdinalIgnoreCase));

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
