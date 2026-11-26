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
    public const string RecordsFileName = "records.json";

    private static readonly JsonSerializerOptions PlayerRecordJsonOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly string RecordsFilePath = JsonStore.PathFor(RecordsFileName);

    private readonly Dictionary<string, TimeZoneInfo> resolvedTimeZones =
        new(StringComparer.Ordinal);

    public PlayerRecord GetRecord(CCitadelPlayerController player)
    {
        ulong steamId = player.PlayerSteamId;

        if (!this.server.PlayerRecords.TryGetValue(steamId, out var record))
        {
            record = new PlayerRecord
            {
                SteamId = steamId,
                LastKnownName = player.PlayerName,
            };

            this.server.PlayerRecords[steamId] = record;
        }

        record.LastKnownName = player.PlayerName;
        return record;
    }

    public bool IsPingBasedMatchmakingEnabled(CCitadelPlayerController player)
    {
        return !this.server.PlayerRecords.TryGetValue(
                player.PlayerSteamId,
                out var record)
            || record.PingBasedMatchmakingEnabled;
    }

    public void SetPingBasedMatchmakingEnabled(
        CCitadelPlayerController player,
        bool enabled)
    {
        ulong steamId = player.PlayerSteamId;
        if (steamId == 0 || steamId == ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(player));
        }

        bool hadRecord = this.server.PlayerRecords.TryGetValue(
            steamId,
            out var record);
        if (!hadRecord && enabled)
        {
            return;
        }

        record ??= new PlayerRecord
        {
            SteamId = steamId,
            LastKnownName = player.PlayerName,
        };
        bool previousValue = record.PingBasedMatchmakingEnabled;
        if (hadRecord && previousValue == enabled)
        {
            return;
        }

        record.LastKnownName = player.PlayerName;
        record.PingBasedMatchmakingEnabled = enabled;
        this.server.PlayerRecords[steamId] = record;

        if (this.SaveRecords())
        {
            return;
        }

        if (hadRecord)
        {
            record.PingBasedMatchmakingEnabled = previousValue;
        }
        else
        {
            this.server.PlayerRecords.Remove(steamId);
        }

        throw new IOException(
            "Could not save your ping-based matchmaking setting.");
    }

    /// <summary>The player's saved /handicap lifesteal percent, or null when off.</summary>
    public float? GetHandicapLifestealPercent(CCitadelPlayerController player)
    {
        if (
            !this.server.PlayerRecords.TryGetValue(player.PlayerSteamId, out var record) ||
            !record.HandicapEnabled)
        {
            return null;
        }

        return record.HandicapLifestealPercent
            ?? this.DuelLifestealPercent - LegacyHandicapLifestealReduction;
    }

    /// <summary>Saves the player's /handicap lifesteal percent; null turns it off.</summary>
    public void SetHandicapLifestealPercent(
        CCitadelPlayerController player,
        float? percent)
    {
        ulong steamId = player.PlayerSteamId;
        if (steamId == 0 || steamId == ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(player));
        }

        bool hadRecord = this.server.PlayerRecords.TryGetValue(
            steamId,
            out var record);
        if (!hadRecord && percent == null)
        {
            return;
        }

        record ??= new PlayerRecord
        {
            SteamId = steamId,
            LastKnownName = player.PlayerName,
        };
        bool previousEnabled = record.HandicapEnabled;
        float? previousPercent = record.HandicapLifestealPercent;
        string previousName = record.LastKnownName;

        record.LastKnownName = player.PlayerName;
        record.HandicapEnabled = percent != null;
        record.HandicapLifestealPercent = percent;
        this.server.PlayerRecords[steamId] = record;

        if (!this.SaveRecords())
        {
            if (hadRecord)
            {
                record.HandicapEnabled = previousEnabled;
                record.HandicapLifestealPercent = previousPercent;
                record.LastKnownName = previousName;
            }
            else
            {
                this.server.PlayerRecords.Remove(steamId);
            }

            throw new IOException("Could not save your handicap setting.");
        }
    }

    public void ResetUnrankedRecord(ulong steamId, string playerName)
    {
        if (steamId == 0 || steamId == ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(steamId));
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            throw new ArgumentException("A player name is required.", nameof(playerName));
        }

        bool hadRecord = this.server.PlayerRecords.TryGetValue(
            steamId,
            out var record);
        record ??= new PlayerRecord
        {
            SteamId = steamId,
            LastKnownName = playerName,
        };

        string previousName = record.LastKnownName;
        int previousWins = record.Wins;
        int previousLosses = record.Losses;
        record.LastKnownName = playerName;
        record.Wins = 0;
        record.Losses = 0;
        this.server.PlayerRecords[steamId] = record;

        if (this.SaveRecords())
        {
            return;
        }

        if (hadRecord)
        {
            record.LastKnownName = previousName;
            record.Wins = previousWins;
            record.Losses = previousLosses;
        }
        else
        {
            this.server.PlayerRecords.Remove(steamId);
        }

        throw new IOException("Could not save the unranked record reset.");
    }

    public string GetRankedEloId(CCitadelPlayerController player) =>
        (player.IsBot ? BotRankedEloId : player.PlayerSteamId)
            .ToString(CultureInfo.InvariantCulture);

    public string GetTimeZoneId(ulong steamId)
    {
        if (
            !this.server.PlayerRecords.TryGetValue(steamId, out var record) ||
            string.IsNullOrWhiteSpace(record.TimeZoneId))
        {
            return "UTC";
        }

        return record.TimeZoneId;
    }

    public TimeZoneInfo GetTimeZone(ulong steamId)
    {
        string timeZoneId = this.GetTimeZoneId(steamId);
        if (this.resolvedTimeZones.TryGetValue(timeZoneId, out var timeZone))
        {
            return timeZone;
        }

        timeZone = ResolveIanaTimeZone(timeZoneId);
        this.resolvedTimeZones.Add(timeZoneId, timeZone);
        return timeZone;
    }

    public void SetTimeZone(CCitadelPlayerController player, string ianaTimeZoneId)
    {
        _ = ResolveIanaTimeZone(ianaTimeZoneId);

        var record = this.GetRecord(player);
        string previousTimeZoneId = record.TimeZoneId;
        record.TimeZoneId = ianaTimeZoneId;

        if (!this.SaveRecords())
        {
            record.TimeZoneId = previousTimeZoneId;
            throw new IOException("Could not save your timezone setting.");
        }
    }

    public void StatsCommand(Caller caller, string? targetName = null)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            var player = caller.Player;

            if (player == null)
            {
                return;
            }

            var record = GetRecord(player);
            int games = record.Wins + record.Losses;
            float winRate = games > 0
                ? (float)record.Wins / games * 100f
                : 0f;

            caller.Reply(
                $"[{record.LastKnownName}] " +
                $"Unranked | Record: {record.Wins}-{record.Losses} | " +
                $"WR: {winRate:F1}%");
            return;
        }

        // Same lookup as the record admin commands: current names of online players,
        // saved names, unique prefixes or a SteamID64, and never a guess between two
        // players who share a name.
        if (!this.server.TryResolvePlayerSteamId(
            targetName,
            out ulong targetSteamId,
            out string resolvedName,
            out string error))
        {
            caller.Reply(error);
            return;
        }

        this.server.PlayerRecords.TryGetValue(targetSteamId, out var recordFound);
        int targetWins = recordFound?.Wins ?? 0;
        int targetLosses = recordFound?.Losses ?? 0;
        int totalGames = targetWins + targetLosses;
        float targetWinRate = totalGames > 0
            ? (float)targetWins / totalGames * 100f
            : 0f;

        caller.Reply(
            $"[{resolvedName}] " +
            $"Unranked | Record: {targetWins}-{targetLosses} | " +
            $"WR: {targetWinRate:F1}%");
    }

    private static TimeZoneInfo ResolveIanaTimeZone(string ianaTimeZoneId)
    {
        string normalizedId = ianaTimeZoneId.Trim();
        if (string.Equals(normalizedId, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            throw new TimeZoneNotFoundException("An IANA timezone ID is required.");
        }

        if (
            !TimeZoneInfo.TryConvertIanaIdToWindowsId(
                normalizedId,
                out string? windowsTimeZoneId))
        {
            string canonicalId = NormalizeIanaTimeZoneId(normalizedId);
            if (
                !TimeZoneInfo.TryConvertIanaIdToWindowsId(
                    canonicalId,
                    out windowsTimeZoneId))
            {
                throw new TimeZoneNotFoundException(
                    $"Unknown IANA timezone: {ianaTimeZoneId}");
            }
        }

        if (string.IsNullOrWhiteSpace(windowsTimeZoneId))
        {
            throw new TimeZoneNotFoundException(
                $"Unknown IANA timezone: {ianaTimeZoneId}");
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
                    {
                        chars[j] = char.ToUpperInvariant(chars[j]);
                    }

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
                    StringComparison.OrdinalIgnoreCase) || normalizedSegment.StartsWith(
                    "Gmt-",
                    StringComparison.OrdinalIgnoreCase) => "GMT" + normalizedSegment.Substring(3),
                _ when normalizedSegment.StartsWith(
                    "Gmt",
                    StringComparison.OrdinalIgnoreCase) && normalizedSegment.Length > 3 && char.IsDigit(normalizedSegment[3])
                    => "GMT" + normalizedSegment.Substring(3),
                _ => normalizedSegment,
            };
        }

        return string.Join("/", segments);
    }

    public void LoadRecords()
    {
        try
        {
            var loaded = JsonStore.Load(
                RecordsFilePath,
                () => new Dictionary<ulong, PlayerRecord>(),
                PlayerRecordJsonOptions);

            this.server.PlayerRecords.Clear();
            foreach (var pair in loaded)
            {
                this.server.PlayerRecords[pair.Key] = pair.Value;
            }

            Console.WriteLine($"Loaded {this.server.PlayerRecords.Count} player records.");
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
            JsonStore.Save(
                RecordsFilePath,
                this.server.PlayerRecords,
                PlayerRecordJsonOptions);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save records: {ex}");
            return false;
        }
    }
}
