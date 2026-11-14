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
    private const float FontSize = 68f;
    private const float HeaderFontSize = 80f;
    private const float WorldUnitsPerPx = 0.3f;
    private const float PreviousBodyColumnOffset = 160f;
    private const float PreviousHeaderColumnOffset = 80f;
    private const float BackgroundDepthOffset = -1f;
    private const int BackgroundHorizontalPadding = 8;
    private const int BackgroundVerticalPadding = 2;
    private const byte BackgroundAlpha = 160;
    private const char BackgroundGlyph = '\u2588';

    private static readonly Vector3 DefaultPosition =
        new(-2642f, -9702f, 1481f);
    private static readonly Vector3 TextAngles = new(180f, 90f, 270f);
    private const uint HeaderColorABGR = 0xFF00A5FF;
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
    private readonly CPointWorldText?[] bodyTexts = new CPointWorldText?[4];
    private readonly CPointWorldText?[] headerTexts = new CPointWorldText?[4];
    private readonly CPointWorldText?[] backgroundTexts = new CPointWorldText?[4];
    private readonly int[] panelLineCounts = new int[4];

    public LeaderboardManager(aimLOCK server)
    {
        this.server = server;
        position = LoadPosition();
    }

    public void Initialize()
    {
        RemoveExistingLeaderboardText();
        Refresh();
    }

    private void RemoveExistingLeaderboardText()
    {
        foreach (var entity in Entities.All)
        {
            if (!entity.Is<CPointWorldText>())
                continue;

            Vector3 entityPosition = entity.Position;
            bool isLeaderboardText = false;
            for (int panelIndex = 0; panelIndex < bodyTexts.Length; panelIndex++)
            {
                if (
                    IsNear(entityPosition, GetPanelPosition(panelIndex)) ||
                    IsNear(entityPosition, GetBodyPosition(panelIndex)) ||
                    IsNear(
                        entityPosition,
                        GetPanelPosition(panelIndex) +
                            new Vector3(0f, PreviousBodyColumnOffset, 0f)
                    )
                )
                {
                    isLeaderboardText = true;
                    break;
                }

                for (int lineCount = 2; lineCount <= EntriesPerLeaderboard + 1; lineCount++)
                {
                    if (IsNear(
                        entityPosition,
                        GetHeaderPosition(panelIndex, lineCount)
                    ) || IsNear(
                        entityPosition,
                        GetPreviousHeaderPosition(panelIndex, lineCount)
                    ) || IsNear(
                        entityPosition,
                        GetPreviousHeaderPosition(panelIndex, lineCount) +
                            new Vector3(0f, PreviousHeaderColumnOffset, 0f)
                    ) || IsNear(
                        entityPosition,
                        GetBackgroundPosition(panelIndex, lineCount)
                    ))
                    {
                        isLeaderboardText = true;
                        break;
                    }
                }

                if (isLeaderboardText)
                    break;
            }

            if (isLeaderboardText)
                entity.Remove();
        }
    }

    private static bool IsNear(Vector3 first, Vector3 second) =>
        Vector3.DistanceSquared(first, second) <= 25f;

    public void Refresh()
    {
        LeaderboardPanelContent[] panels = BuildMessages();
        int longestLine = panels
            .SelectMany(panel => panel.Lines.Append(panel.Title))
            .Max(line => line.Length);
        int backgroundGlyphCount = Math.Max(
            1,
            (int)Math.Ceiling(longestLine / 2d) + BackgroundHorizontalPadding
        );
        int backgroundLineCount = panels.Max(
            panel => panel.Lines.Length
        ) + BackgroundVerticalPadding;

        for (int i = 0; i < bodyTexts.Length; i++)
        {
            LeaderboardPanelContent panel = panels[i];
            panelLineCounts[i] = panel.Lines.Length + 1;
            string bodyMessage = BuildBodyMessage(panel.Lines);
            string headerMessage = BuildHeaderMessage(
                panel.Title,
                panelLineCounts[i]
            );

            RefreshBackgroundText(
                i,
                backgroundGlyphCount,
                backgroundLineCount,
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
                    GetBodyPosition(i),
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
                headerText.ColorABGR = HeaderColorABGR;
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
                    headerText.ColorABGR = HeaderColorABGR;
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
            var backgroundText = backgroundTexts[i];
            if (i == 3)
            {
                if (
                    bodyText is not { IsValid: true } ||
                    headerText is not { IsValid: true } ||
                    backgroundText is not { IsValid: true }
                )
                {
                    allPanelsMoved = false;
                }
                else
                {
                    bodyText.Teleport(position: GetBodyPosition(i));
                    headerText.Teleport(
                        position: GetHeaderPosition(i, panelLineCounts[i])
                    );
                    backgroundText.Teleport(
                        position: GetBackgroundPosition(i, panelLineCounts[i])
                    );
                }

                continue;
            }

            if (
                bodyText is not { IsValid: true } ||
                headerText is not { IsValid: true } ||
                backgroundText is not { IsValid: true }
            )
            {
                allPanelsMoved = false;
                continue;
            }

            bodyText.Teleport(position: GetBodyPosition(i));
            headerText.Teleport(
                position: GetHeaderPosition(i, panelLineCounts[i])
            );
            backgroundText.Teleport(
                position: GetBackgroundPosition(i, panelLineCounts[i])
            );
        }

        if (allPanelsMoved)
            return true;

        Refresh();
        return bodyTexts.All(text => text is { IsValid: true }) &&
            headerTexts.All(text => text is { IsValid: true }) &&
            backgroundTexts.All(text => text is { IsValid: true });
    }

    private Vector3 GetPanelPosition(int index) =>
        index == 3
            ? new Vector3(88f, -9615f, position.Z)
            : position + new Vector3(PanelSpacing * index, 0f, 0f);

    private Vector3 GetBodyPosition(int index) =>
        GetPanelPosition(index);

    private Vector3 GetHeaderPosition(int index, int lineCount) =>
        GetPreviousHeaderPosition(index, lineCount);

    private Vector3 GetBackgroundPosition(int index, int lineCount)
    {
        Vector3 bodyPosition = GetBodyPosition(index);
        float headerOffset = GetHeaderPosition(index, lineCount).X - bodyPosition.X;
        return bodyPosition + new Vector3(headerOffset * 0.75f, 0f, 0f);
    }

    private Vector3 GetPreviousHeaderPosition(int index, int lineCount) =>
        GetPanelPosition(index) + new Vector3(
            (HeaderFontSize - FontSize) *
            WorldUnitsPerPx *
            (lineCount - 1),
            0f,
            0f
        );

    private void RefreshBackgroundText(
        int panelIndex,
        int glyphCount,
        int lineCount,
        int panelTextLineCount
    )
    {
        string backgroundLine = new(BackgroundGlyph, glyphCount);
        string message = BuildBodyMessage(
            Enumerable.Repeat(backgroundLine, lineCount).ToArray()
        );
        Vector3 backgroundPosition = GetBackgroundPosition(
            panelIndex,
            panelTextLineCount
        );

        var backgroundText = backgroundTexts[panelIndex];
        if (backgroundText is { IsValid: true })
        {
            backgroundText.SetMessage(message);
            backgroundText.Teleport(position: backgroundPosition);
        }
        else
        {
            backgroundText = CPointWorldText.Create(
                message,
                backgroundPosition,
                fontSize: FontSize,
                worldUnitsPerPx: WorldUnitsPerPx,
                fontName: "RetailDemo-Regular",
                reorientMode: 1
            );
            if (backgroundText == null)
            {
                Console.WriteLine(
                    $"[aimLOCK] Failed to create leaderboard background {panelIndex + 1}."
                );
                return;
            }

            backgroundTexts[panelIndex] = backgroundText;
            backgroundText.Teleport(angles: TextAngles);
        }

        backgroundText.ColorABGR = (uint)BackgroundAlpha << 24;
        backgroundText.Fullbright = true;
        backgroundText.DepthOffset = BackgroundDepthOffset;
        backgroundText.JustifyHorizontal = HorizontalJustify.Center;
    }

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
            BuildEloMessage(eloLeaders.ToArray()),
            BuildGuildEloMessage()
        };
    }

    private LeaderboardPanelContent BuildWinRateMessage(
        PlayerRecord[] leaders)
    {
        if (leaders.Length == 0)
        {
            return new LeaderboardPanelContent(
                "TOP UNRANKED WR",
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
                server.GuildManager.FormatPlayerName(
                    record.SteamId,
                    record.LastKnownName
                ),
                winRate,
                record.Wins,
                record.Losses
            );
        }

        return new LeaderboardPanelContent("TOP UNRANKED WR", lines);
    }

    private LeaderboardPanelContent BuildWinCountMessage(
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
                server.GuildManager.FormatPlayerName(
                    record.SteamId,
                    record.LastKnownName
                ),
                record.Wins
            );
        }

        return new LeaderboardPanelContent("TOP UNRANKED WINS", lines);
    }

    private LeaderboardPanelContent BuildEloMessage(
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
                server.GuildManager.FormatPlayerName(
                    player.SteamId,
                    player.PlayerName
                ),
                player.Rating
            );
        }

        return new LeaderboardPanelContent("TOP RANKED ELO", lines);
    }

    private LeaderboardPanelContent BuildGuildEloMessage()
    {
        var leaders = server.GuildManager.GetLeaderboardEntries()
            .Select(guild => new
            {
                Guild = guild,
                AverageRating = guild.MemberSteamIds.Average(steamId =>
                    (double)server.RankedElo.GetStats(
                        steamId.ToString(CultureInfo.InvariantCulture),
                        guild.Name
                    ).Rating)
            })
            .OrderByDescending(entry => entry.AverageRating)
            .ThenBy(entry => entry.Guild.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(EntriesPerLeaderboard)
            .ToArray();

        if (leaders.Length == 0)
        {
            return new LeaderboardPanelContent(
                "TOP GUILD ELO",
                new[] { "No guilds created yet." }
            );
        }

        var lines = new string[leaders.Length];
        for (int i = 0; i < leaders.Length; i++)
        {
            var entry = leaders[i];
            string guildName = string.Equals(
                entry.Guild.Name,
                entry.Guild.Tag,
                StringComparison.Ordinal
            )
                ? $"[{entry.Guild.Tag}]"
                : $"{entry.Guild.Name} [{entry.Guild.Tag}]";
            int averageRating = (int)Math.Round(
                entry.AverageRating,
                MidpointRounding.AwayFromZero
            );
            lines[i] = string.Format(
                CultureInfo.InvariantCulture,
                "{0} - {1} Elo",
                guildName,
                averageRating
            );
        }

        return new LeaderboardPanelContent("TOP GUILD ELO", lines);
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

    private sealed record LeaderboardPanelContent(
        string Title,
        string[] Lines
    );
}
