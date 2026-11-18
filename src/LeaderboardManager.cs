using System;
using System.Collections.Generic;
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
    private const float TopPanelsVerticalOffset = 2f * FontSize * WorldUnitsPerPx;
    private const float PreviousBodyColumnOffset = 160f;
    private const float PreviousHeaderColumnOffset = 80f;
    private const float BackgroundDepthOffset = -1f;
    private const int BackgroundHorizontalPadding = 8;
    private const int BackgroundVerticalPadding = 2;
    private const byte BackgroundAlpha = 160;
    private const char BackgroundGlyph = '\u2588';

    private static readonly Vector3 DefaultPosition =
        new(-2642f, -9702f, 1481f);
    private Vector3 ActiveMatchesPosition =>
        new(201f, -9719f, position.Z);
    private static readonly Vector3 TextAngles = new(180f, 90f, 270f);
    private const uint HeaderColorABGR = 0xFF00A5FF;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly aimLOCK server;
    private readonly string positionFilePath =
        JsonStore.PathFor("leaderboard-position.json");

    private Vector3 position;
    private readonly CPointWorldText?[] bodyTexts = new CPointWorldText?[4];
    private readonly CPointWorldText?[] headerTexts = new CPointWorldText?[4];
    private readonly CPointWorldText?[] backgroundTexts = new CPointWorldText?[4];
    private readonly int[] panelLineCounts = new int[4];
    private CPointWorldText? activeMatchesBodyText;
    private CPointWorldText? activeMatchesHeaderText;
    private CPointWorldText? activeMatchesBackgroundText;
    private string? lastAppliedActiveMatchesState;
    // Text and layout last pushed to the world-text entities. When a refresh would
    // produce the same thing, nothing is re-sent to clients.
    private string? lastAppliedState;
    private bool refreshScheduled;
    private const double DeferredRefreshDelaySeconds = 3;

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
        Vector3[] legacyGuildPositions =
        {
            new(88f, -9615f, position.Z),
            new(-2710f, -9355f, 1215f)
        };

        foreach (var entity in Entities.All)
        {
            if (!entity.Is<CPointWorldText>())
                continue;

            Vector3 entityPosition = entity.Position;
            if (IsActiveMatchesTextPosition(entityPosition))
            {
                entity.Remove();
                continue;
            }

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

                if (!isLeaderboardText && panelIndex == 3)
                {
                    isLeaderboardText = IsLegacyGuildEloPanelText(
                        entityPosition,
                        legacyGuildPositions
                    );
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

    private bool IsActiveMatchesTextPosition(Vector3 entityPosition)
    {
        if (IsNear(entityPosition, ActiveMatchesPosition))
            return true;

        for (
            int panelLineCount = 2;
            panelLineCount <= server.DuelLocations.Length + 1;
            panelLineCount++
        )
        {
            if (
                IsNear(
                    entityPosition,
                    GetActiveMatchesHeaderPosition(panelLineCount)
                ) ||
                IsNear(
                    entityPosition,
                    GetActiveMatchesBackgroundPosition(panelLineCount)
                )
            )
                return true;
        }

        return false;
    }

    /// <summary>
    /// Refresh a few seconds from now, coalescing repeated requests. Used at match end,
    /// so the sorting and world-text updates don't land on the same frame as the
    /// teleports, team changes and HUD rebuilds of finishing a match.
    /// </summary>
    public void RequestRefresh()
    {
        if (refreshScheduled)
            return;

        refreshScheduled = true;
        server.ScheduleOnce(DeferredRefreshDelaySeconds.Seconds(), () =>
        {
            refreshScheduled = false;
            Refresh();
        });
    }

    public void Refresh()
    {
        LeaderboardPanelContent[] panels = BuildMessages();
        string state = BuildAppliedState(panels);
        if (state == lastAppliedState && AllPanelEntitiesValid())
            return;

        int longestLine = panels
            .SelectMany(panel => panel.Lines.Append(panel.Title))
            .Max(line => line.Length);
        int backgroundGlyphCount = Math.Max(
            1,
            (int)Math.Ceiling(longestLine / 2d) + BackgroundHorizontalPadding
        );
        int backgroundLineCount =
            panels.Max(panel => panel.Lines.Length) + BackgroundVerticalPadding;

        for (int i = 0; i < bodyTexts.Length; i++)
        {
            LeaderboardPanelContent panel = panels[i];
            panelLineCounts[i] = panel.Lines.Length + 1;
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

            string bodyMessage = BuildBodyMessage(panel.Lines);
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

        lastAppliedState = state;
        RefreshActiveMatches();
    }

    public void RefreshActiveMatches()
    {
        DuelMatch[] matches = server.ActiveMatches.Values
            .Where(match => !match.Finished)
            .Distinct()
            .OrderBy(match => match.ArenaIndex)
            .ToArray();
        string[] lines = matches.Length == 0
            ? new[] { "No active matches." }
            : matches.Select(BuildActiveMatchLine).ToArray();
        string state = string.Join('\n', lines);
        if (
            state == lastAppliedActiveMatchesState &&
            activeMatchesBodyText is { IsValid: true } &&
            activeMatchesHeaderText is { IsValid: true } &&
            activeMatchesBackgroundText is { IsValid: true }
        )
            return;

        int panelLineCount = lines.Length + 1;
        int longestLine = Math.Max(
            "ACTIVE MATCHES".Length,
            lines.Max(line => line.Length)
        );
        int backgroundGlyphCount = Math.Max(
            1,
            (int)Math.Ceiling(longestLine / 2d) + BackgroundHorizontalPadding
        );
        string backgroundLine = new(BackgroundGlyph, backgroundGlyphCount);
        string backgroundMessage = BuildBodyMessage(
            Enumerable.Repeat(
                backgroundLine,
                lines.Length + BackgroundVerticalPadding
            ).ToArray()
        );
        Vector3 backgroundPosition =
            GetActiveMatchesBackgroundPosition(panelLineCount);

        var backgroundText = activeMatchesBackgroundText;
        if (backgroundText is { IsValid: true })
        {
            backgroundText.SetMessage(backgroundMessage);
            backgroundText.Teleport(position: backgroundPosition);
        }
        else
        {
            backgroundText = CPointWorldText.Create(
                backgroundMessage,
                backgroundPosition,
                fontSize: FontSize,
                worldUnitsPerPx: WorldUnitsPerPx,
                fontName: "RetailDemo-Regular",
                reorientMode: 1
            );
            if (backgroundText == null)
            {
                Console.WriteLine(
                    "[aimLOCK] Failed to create active matches background."
                );
                return;
            }

            activeMatchesBackgroundText = backgroundText;
            backgroundText.Teleport(angles: TextAngles);
        }

        backgroundText.ColorABGR = (uint)BackgroundAlpha << 24;
        backgroundText.Fullbright = true;
        backgroundText.DepthOffset = BackgroundDepthOffset;
        backgroundText.JustifyHorizontal = HorizontalJustify.Center;

        string bodyMessage = BuildBodyMessage(lines);
        var bodyText = activeMatchesBodyText;
        if (bodyText is { IsValid: true })
        {
            bodyText.SetMessage(bodyMessage);
        }
        else
        {
            bodyText = CPointWorldText.Create(
                bodyMessage,
                ActiveMatchesPosition,
                fontSize: FontSize,
                worldUnitsPerPx: WorldUnitsPerPx,
                fontName: "RetailDemo-Regular",
                reorientMode: 1
            );
            if (bodyText == null)
            {
                Console.WriteLine("[aimLOCK] Failed to create active matches text.");
                return;
            }

            activeMatchesBodyText = bodyText;
            bodyText.JustifyHorizontal = HorizontalJustify.Center;
            bodyText.Teleport(angles: TextAngles);
        }

        string headerMessage = BuildHeaderMessage(
            "ACTIVE MATCHES",
            panelLineCount
        );
        Vector3 headerPosition =
            GetActiveMatchesHeaderPosition(panelLineCount);
        var headerText = activeMatchesHeaderText;
        if (headerText is { IsValid: true })
        {
            headerText.SetMessage(headerMessage);
            headerText.ColorABGR = HeaderColorABGR;
            headerText.Teleport(position: headerPosition);
        }
        else
        {
            headerText = CPointWorldText.Create(
                headerMessage,
                headerPosition,
                fontSize: HeaderFontSize,
                worldUnitsPerPx: WorldUnitsPerPx,
                fontName: "RetailDemo-Bold",
                reorientMode: 1
            );
            if (headerText == null)
            {
                Console.WriteLine(
                    "[aimLOCK] Failed to create active matches heading."
                );
                return;
            }

            activeMatchesHeaderText = headerText;
            headerText.JustifyHorizontal = HorizontalJustify.Center;
            headerText.ColorABGR = HeaderColorABGR;
            headerText.Teleport(angles: TextAngles);
        }

        lastAppliedActiveMatchesState = state;
    }

    private string BuildActiveMatchLine(DuelMatch match)
    {
        string participant1 = GetActiveMatchParticipantName(match.Player1);
        string participant2 = GetActiveMatchParticipantName(match.Player2);
        string arenaName =
            (uint)match.ArenaIndex < (uint)aimLOCK.DuelArenaNames.Length
                ? aimLOCK.DuelArenaNames[match.ArenaIndex]
                : $"Arena {match.ArenaIndex + 1}";
        string queueName = match.QueueType == MatchQueueType.Ranked
            ? "Ranked"
            : "Unranked";
        return $"{arenaName} | {queueName} | {participant1} {match.Score1}-{match.Score2} {participant2}";
    }

    private string GetActiveMatchParticipantName(string participantName)
    {
        var player = server.PlayerManager.FindMatchParticipant(participantName);
        return player == null
            ? participantName
            : server.PlayerManager.GetPracticeBotDisplayName(player) ??
                server.GuildManager.FormatPlayerNameWithSelectedTag(player);
    }

    private Vector3 GetActiveMatchesHeaderPosition(int panelLineCount) =>
        ActiveMatchesPosition + new Vector3(
            (HeaderFontSize - FontSize) *
            WorldUnitsPerPx *
            (panelLineCount - 1),
            0f,
            0f
        );

    private Vector3 GetActiveMatchesBackgroundPosition(
        int panelLineCount
    )
    {
        float headerOffset =
            GetActiveMatchesHeaderPosition(panelLineCount).X -
            ActiveMatchesPosition.X;
        return ActiveMatchesPosition + new Vector3(headerOffset * 0.75f, 0f, 0f);
    }

    private string BuildAppliedState(LeaderboardPanelContent[] panels)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(position.X).Append(',').Append(position.Y).Append(',')
            .Append(position.Z).Append('\n');
        foreach (var panel in panels)
        {
            builder.Append(panel.Title).Append('\n');
            foreach (string line in panel.Lines)
                builder.Append(line).Append('\n');
            builder.Append('\u0001');
        }

        return builder.ToString();
    }

    private bool AllPanelEntitiesValid()
    {
        for (int i = 0; i < bodyTexts.Length; i++)
        {
            if (
                bodyTexts[i] is not { IsValid: true } ||
                headerTexts[i] is not { IsValid: true } ||
                backgroundTexts[i] is not { IsValid: true }
            )
                return false;
        }

        return true;
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

    private Vector3 GetPanelPosition(int index)
    {
        Vector3 panelPosition = index == 3
            ? GetGuildEloPosition()
            : position + new Vector3(PanelSpacing * index, 0f, 0f);

        return index < 3
            ? panelPosition + new Vector3(TopPanelsVerticalOffset, 0f, 0f)
            : panelPosition;
    }

    private Vector3 GetBodyPosition(int index) =>
        GetPanelPosition(index);

    private static bool IsLegacyGuildEloRowText(
        Vector3 entityPosition,
        int lineIndex,
        Vector3 bodyPosition
    )
    {
        float rowSpacing = FontSize * WorldUnitsPerPx;
        float maxHalfWidth = rowSpacing * 50f;
        float previousX = bodyPosition.X + rowSpacing * (lineIndex + 1);
        float latestX = bodyPosition.X +
            rowSpacing * 1.5f * (lineIndex + 1);
        return Math.Abs(entityPosition.Z - bodyPosition.Z) <= 5f &&
            Math.Abs(entityPosition.Y - bodyPosition.Y) <= maxHalfWidth &&
            (
                Math.Abs(entityPosition.X - previousX) <= 5f ||
                Math.Abs(entityPosition.X - latestX) <= 5f
            );
    }

    private static bool IsLegacyGuildEloPanelText(
        Vector3 entityPosition,
        Vector3[] legacyGuildPositions
    )
    {
        foreach (Vector3 bodyPosition in legacyGuildPositions)
        {
            if (
                IsNear(entityPosition, bodyPosition) ||
                IsNear(
                    entityPosition,
                    bodyPosition + new Vector3(0f, PreviousBodyColumnOffset, 0f)
                )
            )
            {
                return true;
            }

            for (
                int lineCount = 2;
                lineCount <= EntriesPerLeaderboard + 1;
                lineCount++
            )
            {
                float headerOffset = (HeaderFontSize - FontSize) *
                    WorldUnitsPerPx *
                    (lineCount - 1);
                Vector3 headerPosition =
                    bodyPosition + new Vector3(headerOffset, 0f, 0f);
                if (
                    IsNear(entityPosition, headerPosition) ||
                    IsNear(
                        entityPosition,
                        headerPosition +
                            new Vector3(0f, PreviousHeaderColumnOffset, 0f)
                    ) ||
                    IsNear(
                        entityPosition,
                        bodyPosition +
                            new Vector3(headerOffset * 0.75f, 0f, 0f)
                    )
                )
                {
                    return true;
                }
            }

            for (int lineIndex = 0; lineIndex < EntriesPerLeaderboard; lineIndex++)
            {
                if (
                    IsLegacyGuildEloRowText(
                        entityPosition,
                        lineIndex,
                        bodyPosition
                    )
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private Vector3 GetGuildEloPosition() =>
        new(-2779f, -9345f, position.Z);

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
            BuildEloMessage(eloLeaders),
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
        IReadOnlyList<RankedEloStats> leaders)
    {
        if (leaders.Count == 0)
        {
            return new LeaderboardPanelContent(
                "TOP RANKED ELO",
                new[] { "No ranked results recorded yet." }
            );
        }

        var lines = new string[leaders.Count];
        for (int i = 0; i < leaders.Count; i++)
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
                new[] { "No guilds yet." }
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
                ? string.Empty
                : $"{entry.Guild.Name} ";
            string tag = $"[{entry.Guild.Tag}]";
            int averageRating = (int)Math.Round(
                entry.AverageRating,
                MidpointRounding.AwayFromZero
            );
            string rating = string.Format(
                CultureInfo.InvariantCulture,
                " - {0} Elo",
                averageRating
            );
            lines[i] = guildName + tag + rating;
        }

        return new LeaderboardPanelContent("TOP GUILD ELO", lines);
    }

    private Vector3 LoadPosition()
    {
        var savedPosition = JsonStore.Load(
            positionFilePath,
            () => new LeaderboardPosition(
                DefaultPosition.X,
                DefaultPosition.Y,
                DefaultPosition.Z
            ),
            JsonOptions,
            candidate =>
            {
                ValidatePosition(
                    new Vector3(candidate.X, candidate.Y, candidate.Z)
                );
                return candidate;
            }
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
        JsonStore.Save(
            positionFilePath,
            new LeaderboardPosition(newPosition.X, newPosition.Y, newPosition.Z),
            JsonOptions
        );
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
