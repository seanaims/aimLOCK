using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using DeadworksManaged.Api;

namespace aimLOCK;

public sealed class LeaderboardManager
{
    private const int EntriesPerLeaderboard = 10;
    private const int MinimumWinRateGames = 5;
    private const float PanelSpacing = 250f;
    private const float FontSize = 34f;
    private const float HeaderFontSize = 40f;
    private const float WorldUnitsPerPx = 0.6f;

    private static readonly Vector3 DefaultPosition =
        new(-2642f, -9702f, 1481f);
    private static readonly Vector3 TextAngles = new(180f, 90f, 270f);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly aimLOCK server;
    private readonly string positionFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "aimLOCK",
        "leaderboard-position.json"
    );

    private Vector3 position;
    private readonly CPointWorldText?[] bodyTexts = new CPointWorldText?[3];
    private readonly CPointWorldText?[] headerTexts = new CPointWorldText?[3];
    private readonly int[] panelLineCounts = new int[3];

    public LeaderboardManager(aimLOCK server)
    {
        this.server = server;
        position = LoadPosition();
    }

    public void Initialize()
    {
        Refresh();
    }

    public void Refresh()
    {
        LeaderboardPanelContent[] panels = BuildMessages();
        for (int i = 0; i < bodyTexts.Length; i++)
        {
            LeaderboardPanelContent panel = panels[i];
            panelLineCounts[i] = panel.Lines.Length + 1;
            string bodyMessage = BuildBodyMessage(panel.Lines);
            string headerMessage = BuildHeaderMessage(
                panel.Title,
                panelLineCounts[i]
            );

            var bodyText = bodyTexts[i];
            if (bodyText is { IsValid: true })
            {
                bodyText.SetMessage(bodyMessage);
            }
            else
            {
                bodyText = CPointWorldText.Create(
                    bodyMessage,
                    GetPanelPosition(i),
                    fontSize: FontSize,
                    worldUnitsPerPx: WorldUnitsPerPx,
                    fontName: "RetailDemo-Regular",
                    reorientMode: 1
                );
                if (bodyText == null)
                {
                    Console.WriteLine($"[aimLOCK] Failed to create leaderboard panel {i + 1}.");
                }
                else
                {
                    bodyText.JustifyHorizontal = HorizontalJustify.Center;
                    bodyText.Teleport(angles: TextAngles);
                    bodyTexts[i] = bodyText;
                }
            }

            var headerText = headerTexts[i];
            if (headerText is { IsValid: true })
            {
                headerText.SetMessage(headerMessage);
                headerText.Teleport(
                    position: GetHeaderPosition(i, panelLineCounts[i])
                );
            }
            else
            {
                headerText = CPointWorldText.Create(
                    headerMessage,
                    GetHeaderPosition(i, panelLineCounts[i]),
                    fontSize: HeaderFontSize,
                    worldUnitsPerPx: WorldUnitsPerPx,
                    fontName: "RetailDemo-Bold",
                    reorientMode: 1
                );
                if (headerText == null)
                {
                    Console.WriteLine($"[aimLOCK] Failed to create leaderboard heading {i + 1}.");
                }
                else
                {
                    headerText.JustifyHorizontal = HorizontalJustify.Center;
                    headerText.Teleport(angles: TextAngles);
                    headerTexts[i] = headerText;
                }
            }
        }
    }

    public bool MoveTo(Vector3 newPosition)
    {
        ValidatePosition(newPosition);
        SavePosition(newPosition);
        position = newPosition;

        bool allPanelsMoved = true;
        for (int i = 0; i < bodyTexts.Length; i++)
        {
            var bodyText = bodyTexts[i];
            var headerText = headerTexts[i];
            if (
                bodyText is not { IsValid: true } ||
                headerText is not { IsValid: true }
            )
            {
                allPanelsMoved = false;
                continue;
            }

            bodyText.Teleport(position: GetPanelPosition(i));
            headerText.Teleport(
                position: GetHeaderPosition(i, panelLineCounts[i])
            );
        }

        if (allPanelsMoved)
            return true;

        Refresh();
        return bodyTexts.All(text => text is { IsValid: true }) &&
            headerTexts.All(text => text is { IsValid: true });
    }

    private Vector3 GetPanelPosition(int index) =>
        position + new Vector3(PanelSpacing * index, 0f, 0f);

    private Vector3 GetHeaderPosition(int index, int lineCount) =>
        GetPanelPosition(index) + new Vector3(
            (HeaderFontSize - FontSize) *
            WorldUnitsPerPx *
            (lineCount - 1),
            0f,
            0f
        );

    private static string BuildBodyMessage(string[] lines) =>
        "\n " + string.Join("\n", lines);

    private static string BuildHeaderMessage(string title, int lineCount) =>
        title + string.Concat(Enumerable.Repeat("\n ", lineCount - 1));

    private LeaderboardPanelContent[] BuildMessages()
    {
        var records = server.PlayerRecords.Values
            .Where(record => record.SteamId != 0 && record.SteamId != ulong.MaxValue)
            .ToArray();
        var winRateLeaders = records
            .Where(record => record.Wins + record.Losses >= MinimumWinRateGames)
            .OrderByDescending(record =>
                (double)record.Wins / (record.Wins + record.Losses))
            .ThenByDescending(record => record.Wins + record.Losses)
            .ThenBy(record => record.LastKnownName, StringComparer.OrdinalIgnoreCase)
            .Take(EntriesPerLeaderboard)
            .ToArray();
        var winLeaders = records
            .Where(record => record.Wins > 0)
            .OrderByDescending(record => record.Wins)
            .ThenByDescending(record =>
                (double)record.Wins / (record.Wins + record.Losses))
            .ThenBy(record => record.LastKnownName, StringComparer.OrdinalIgnoreCase)
            .Take(EntriesPerLeaderboard)
            .ToArray();
        var eloLeaders = server.RankedElo.GetLeaderboard(EntriesPerLeaderboard);

        return new[]
        {
            BuildWinRateMessage(winRateLeaders),
            BuildWinCountMessage(winLeaders),
            BuildEloMessage(eloLeaders.ToArray())
        };
    }

    private static LeaderboardPanelContent BuildWinRateMessage(
        PlayerRecord[] leaders)
    {
        if (leaders.Length == 0)
        {
            return new LeaderboardPanelContent(
                "TOP UNRANKED WIN RATE",
                new[] { "No eligible players yet." }
            );
        }

        var lines = new string[leaders.Length];
        for (int i = 0; i < leaders.Length; i++)
        {
            var record = leaders[i];
            int games = record.Wins + record.Losses;
            double winRate = (double)record.Wins / games * 100d;
            lines[i] = string.Format(
                CultureInfo.InvariantCulture,
                "{0} - {1:F1}% ({2}-{3})",
                record.LastKnownName,
                winRate,
                record.Wins,
                record.Losses
            );
        }

        return new LeaderboardPanelContent("TOP UNRANKED WIN RATE", lines);
    }

    private static LeaderboardPanelContent BuildWinCountMessage(
        PlayerRecord[] leaders)
    {
        if (leaders.Length == 0)
        {
            return new LeaderboardPanelContent(
                "TOP UNRANKED WINS",
                new[] { "No wins recorded yet." }
            );
        }

        var lines = new string[leaders.Length];
        for (int i = 0; i < leaders.Length; i++)
        {
            var record = leaders[i];
            lines[i] = string.Format(
                CultureInfo.InvariantCulture,
                "{0} - {1} wins",
                record.LastKnownName,
                record.Wins
            );
        }

        return new LeaderboardPanelContent("TOP UNRANKED WINS", lines);
    }

    private static LeaderboardPanelContent BuildEloMessage(
        RankedEloStats[] leaders)
    {
        if (leaders.Length == 0)
        {
            return new LeaderboardPanelContent(
                "TOP RANKED ELO",
                new[] { "No ranked results recorded yet." }
            );
        }

        var lines = new string[leaders.Length];
        for (int i = 0; i < leaders.Length; i++)
        {
            RankedEloStats player = leaders[i];
            lines[i] = string.Format(
                CultureInfo.InvariantCulture,
                "{0} - {1} Elo",
                player.PlayerName,
                player.Rating
            );
        }

        return new LeaderboardPanelContent("TOP RANKED ELO", lines);
    }

    private Vector3 LoadPosition()
    {
        if (!File.Exists(positionFilePath))
            return DefaultPosition;

        string json = File.ReadAllText(positionFilePath);
        var savedPosition = JsonSerializer.Deserialize<LeaderboardPosition>(json)
            ?? throw new InvalidDataException(
                $"Leaderboard position data is empty or invalid: {positionFilePath}"
            );
        var loadedPosition = new Vector3(
            savedPosition.X,
            savedPosition.Y,
            savedPosition.Z
        );
        ValidatePosition(loadedPosition);
        return loadedPosition;
    }

    private void SavePosition(Vector3 newPosition)
    {
        string directory = Path.GetDirectoryName(positionFilePath)
            ?? throw new InvalidOperationException(
                $"Could not determine the leaderboard position directory: {positionFilePath}"
            );
        Directory.CreateDirectory(directory);

        string temporaryPath = positionFilePath + ".tmp";
        string json = JsonSerializer.Serialize(
            new LeaderboardPosition(newPosition.X, newPosition.Y, newPosition.Z),
            JsonOptions
        );
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, positionFilePath, overwrite: true);
    }

    private static void ValidatePosition(Vector3 candidate)
    {
        if (
            !float.IsFinite(candidate.X) ||
            !float.IsFinite(candidate.Y) ||
            !float.IsFinite(candidate.Z)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidate),
                "Leaderboard coordinates must be finite numbers."
            );
        }
    }

    private sealed record LeaderboardPosition(float X, float Y, float Z);

    private sealed record LeaderboardPanelContent(string Title, string[] Lines);
}
