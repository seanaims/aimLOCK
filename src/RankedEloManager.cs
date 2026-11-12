using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace aimLOCK;

public sealed class RankedEloManager
{
    public const int InitialRating = 1000;
    public const int KFactor = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private readonly string dataFilePath;
    private Dictionary<string, StoredPlayer> players;

    public RankedEloManager(string? dataFilePath = null)
    {
        this.dataFilePath = Path.GetFullPath(
            dataFilePath ?? Path.Combine(
                AppContext.BaseDirectory,
                "aimLOCK",
                "ranked-elo.json"
            )
        );
        players = LoadPlayers();
    }

    public RankedEloStats GetStats(string steamId64, string playerName)
    {
        string playerId = ValidateSteamId(steamId64, nameof(steamId64));
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        lock (syncRoot)
        {
            if (players.TryGetValue(playerId, out var player))
                return ToStats(player);

            return new RankedEloStats(
                playerName,
                InitialRating,
                0,
                0
            );
        }
    }

    public IReadOnlyList<RankedEloStats> GetLeaderboard(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        lock (syncRoot)
        {
            return players
                .Where(player => player.Key != ulong.MaxValue.ToString(
                    CultureInfo.InvariantCulture
                ))
                .Select(player => ToStats(player.Value))
                .OrderByDescending(player => player.Rating)
                .ThenByDescending(player => player.Wins + player.Losses)
                .ThenBy(player => player.PlayerName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToArray();
        }
    }

    public EloMatchResult RecordMatchResult(
        string player1SteamId64,
        string player1Name,
        string player2SteamId64,
        string player2Name,
        bool player1Won)
    {
        string player1Id = ValidateSteamId(
            player1SteamId64,
            nameof(player1SteamId64)
        );
        string player2Id = ValidateSteamId(
            player2SteamId64,
            nameof(player2SteamId64)
        );
        ArgumentException.ThrowIfNullOrWhiteSpace(player1Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(player2Name);

        if (StringComparer.Ordinal.Equals(player1Id, player2Id))
            throw new ArgumentException("A player cannot be matched against themselves.");

        lock (syncRoot)
        {
            players.TryGetValue(player1Id, out var existingPlayer1);
            players.TryGetValue(player2Id, out var existingPlayer2);

            int rating1Before = existingPlayer1?.Rating ?? InitialRating;
            int rating2Before = existingPlayer2?.Rating ?? InitialRating;
            double expected1 = 1.0 / (
                1.0 + Math.Pow(
                    10.0,
                    ((double)rating2Before - rating1Before) / 400.0
                )
            );
            double score1 = player1Won ? 1.0 : 0.0;
            int rating1After = Math.Max(
                0,
                (int)Math.Round(
                    rating1Before + KFactor * (score1 - expected1),
                    MidpointRounding.AwayFromZero
                )
            );
            int rating2After = Math.Max(
                0,
                (int)Math.Round(
                    rating2Before + KFactor * ((1.0 - score1) - (1.0 - expected1)),
                    MidpointRounding.AwayFromZero
                )
            );

            var updatedPlayers = new Dictionary<string, StoredPlayer>(
                players,
                StringComparer.Ordinal
            )
            {
                [player1Id] = new StoredPlayer
                {
                    PlayerName = player1Name,
                    Rating = rating1After,
                    Wins = (existingPlayer1?.Wins ?? 0) + (player1Won ? 1 : 0),
                    Losses = (existingPlayer1?.Losses ?? 0) + (player1Won ? 0 : 1)
                },
                [player2Id] = new StoredPlayer
                {
                    PlayerName = player2Name,
                    Rating = rating2After,
                    Wins = (existingPlayer2?.Wins ?? 0) + (player1Won ? 0 : 1),
                    Losses = (existingPlayer2?.Losses ?? 0) + (player1Won ? 1 : 0)
                }
            };

            SavePlayers(updatedPlayers);
            players = updatedPlayers;

            return new EloMatchResult(
                rating1Before,
                rating1After,
                rating2Before,
                rating2After
            );
        }
    }

    private Dictionary<string, StoredPlayer> LoadPlayers()
    {
        if (!File.Exists(dataFilePath))
            return new Dictionary<string, StoredPlayer>(StringComparer.Ordinal);

        using var stream = File.OpenRead(dataFilePath);
        var storedPlayers =
            JsonSerializer.Deserialize<Dictionary<string, StoredPlayer>>(
                stream,
                JsonOptions
            ) ?? throw new InvalidDataException(
                $"Ranked Elo data is empty or invalid: {dataFilePath}"
            );

        var loadedPlayers = new Dictionary<string, StoredPlayer>(
            StringComparer.Ordinal
        );

        foreach (var entry in storedPlayers)
        {
            string playerId = ValidateSteamId(entry.Key, nameof(dataFilePath));
            StoredPlayer player = entry.Value;

            if (
                string.IsNullOrWhiteSpace(player.PlayerName) ||
                player.Rating < 0 ||
                player.Wins < 0 ||
                player.Losses < 0
            )
            {
                throw new InvalidDataException(
                    $"Ranked Elo data contains an invalid player record: {playerId}"
                );
            }

            loadedPlayers.Add(playerId, player);
        }

        return loadedPlayers;
    }

    private void SavePlayers(Dictionary<string, StoredPlayer> updatedPlayers)
    {
        string directory = Path.GetDirectoryName(dataFilePath)
            ?? throw new InvalidOperationException(
                $"Could not determine the ranked Elo data directory: {dataFilePath}"
            );
        Directory.CreateDirectory(directory);

        string temporaryPath = dataFilePath + ".tmp";
        using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        ))
        {
            JsonSerializer.Serialize(stream, updatedPlayers, JsonOptions);
            stream.Flush(true);
        }

        File.Move(temporaryPath, dataFilePath, overwrite: true);
    }

    private static string ValidateSteamId(string steamId64, string parameterName)
    {
        if (
            !ulong.TryParse(
                steamId64,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong parsedId
            ) ||
            parsedId == 0
        )
        {
            throw new ArgumentException(
                "A valid, non-zero SteamID64 is required.",
                parameterName
            );
        }

        return parsedId.ToString(CultureInfo.InvariantCulture);
    }

    private static RankedEloStats ToStats(StoredPlayer player) =>
        new(player.PlayerName, player.Rating, player.Wins, player.Losses);

    private sealed class StoredPlayer
    {
        public string PlayerName { get; set; } = string.Empty;
        public int Rating { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
    }
}

public sealed record RankedEloStats(
    string PlayerName,
    int Rating,
    int Wins,
    int Losses
);

public sealed record EloMatchResult(
    int Player1RatingBefore,
    int Player1RatingAfter,
    int Player2RatingBefore,
    int Player2RatingAfter
);
