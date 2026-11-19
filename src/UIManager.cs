/*
Main UI functionality here
Info panel, player count, queue count, date, scoreboard, winner, etc
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace aimLOCK;

public class UIManager
{
    private readonly aimLOCK server;
    private readonly HashSet<ulong> hiddenServerInfo = new();
    // What each player's client currently has for the server-info panel:
    // true = built, false = destroyed, missing = unknown (rebuild to be safe).
    // Lets refreshes update two labels instead of re-sending the whole layout, and
    // skip re-sending destroys to players who are already in a match.
    private readonly Dictionary<ulong, bool> serverInfoPanelState = new();
    // Last date sent to each player; the date label is only re-sent when it changes.
    private readonly Dictionary<ulong, string> lastSentDate = new();
    private readonly Dictionary<ulong, string> lastSentTime = new();

    public UIManager(aimLOCK server)
    {
        this.server = server;
        UI.ClientResync += OnClientResync;
        ScheduleClockRefresh();
    }

    private void OnClientResync(int playerSlot)
    {
        var controller = Players.GetAll()
            .FirstOrDefault(player => player.Slot == playerSlot);

        if (controller == null)
            return;

        // The client lost its panels; forget what we think it has.
        serverInfoPanelState.Remove(controller.PlayerSteamId);
        lastSentDate.Remove(controller.PlayerSteamId);
        lastSentTime.Remove(controller.PlayerSteamId);
        ReconcileHud(controller);
        // Follow-up pass: only rebuilds if the first one did not leave the server-info
        // panel in a known state; otherwise a resync sent every panel twice.
        server.ScheduleOnce(0.25.Seconds(), () =>
        {
            if (!controller.IsValid)
                return;

            if (
                server.PlayerManager.TryGetActiveMatch(controller, out var match) &&
                !match.Finished
            )
                return;

            if (!serverInfoPanelState.ContainsKey(controller.PlayerSteamId))
                ReconcileHud(controller);
        });
    }

    private void ReconcileHud(CCitadelPlayerController controller)
    {
        if (!controller.IsValid)
            return;

        if (
            server.PlayerManager.TryGetActiveMatch(controller, out var match) &&
            !match.Finished
        )
        {
            ShowMatchHud(match);
            return;
        }

        HideMatchHud(controller);
        ShowServerInfo(controller);
    }

    /* Server Information HUD */

    private readonly record struct ServerInfoStats(
        string PlayerCountText,
        string RankedQueued,
        string RankedPlaying,
        string UnrankedQueued,
        string UnrankedPlaying
    );

    private const string DateFormat = "MM/dd/yyyy";
    private const string TimeFormat = "hh:mm tt";

    private ServerInfoStats GetServerInfoStats()
        => GetServerInfoStats(Players.GetAll().Count());

    private ServerInfoStats GetServerInfoStats(int playerCount)
    {
        int rankedMatches = 0;
        int unrankedMatches = 0;
        foreach (var match in server.ActiveMatches.Values.Distinct())
        {
            if (match.QueueType == MatchQueueType.Ranked)
                rankedMatches++;
            else if (match.QueueType == MatchQueueType.Unranked)
                unrankedMatches++;
        }

        return new ServerInfoStats(
            $"{playerCount} online",
            server.RankedQueue.Count.ToString(CultureInfo.InvariantCulture),
            (rankedMatches * 2).ToString(CultureInfo.InvariantCulture),
            server.UnrankedQueue.Count.ToString(CultureInfo.InvariantCulture),
            (unrankedMatches * 2).ToString(CultureInfo.InvariantCulture)
        );
    }

    private UINode Layout(ulong steamId, ServerInfoStats stats)
    {
        DateTimeOffset localNow = GetLocalTime(steamId);
        string currentDate = localNow.ToString(DateFormat, CultureInfo.InvariantCulture);
        string currentTime = localNow.ToString(TimeFormat, CultureInfo.InvariantCulture);

        return UI.Vertical()
            .WithStyle("horizontal-align", "right")
            .WithStyle("vertical-align", "center")
            .WithStyle("margin-top", "-80px")
            .WithStyle("background-color", "rgba(17, 17, 17, 0.65)")
            .WithStyle("padding", "15px 15px")
            .Add(

                UI.Horizontal()
                    .WithStyle("horizontal-align", "center")
                    .Add(
                        UI.Label("servertitle", "aimlock")
                            .WithStyle("font-size", "26px")
                            .WithStyle("font-weight", "bold")
                            .WithStyle("color", "#FFFFFF"),

                        UI.Label("serverdomain", ".xyz")
                            .WithStyle("font-size", "26px")
                            .WithStyle("font-weight", "bold")
                            .WithStyle("color", "#555555")
                    ),

                UI.Label("playercount", stats.PlayerCountText)
                    .WithStyle("font-size", "16px")
                    .WithStyle("color", "#AAAAAA")
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("margin-bottom", "10px"),

                UI.Horizontal()
                    .WithStyle("horizontal-align", "center")
                    .Add(
                        QueueTableCell("queuehead", "", QueueNameColumnWidth, header: true),
                        QueueTableCell("queuedhead", "QUEUED", QueueNumberColumnWidth, header: true),
                        QueueTableCell("playinghead", "PLAYING", QueueNumberColumnWidth, header: true)
                    ),

                QueueTableRow(
                    "ranked",
                    "Ranked",
                    stats.RankedQueued,
                    stats.RankedPlaying
                ),

                QueueTableRow(
                    "unranked",
                    "Unranked",
                    stats.UnrankedQueued,
                    stats.UnrankedPlaying
                ),

                UI.Label("queuestatus", GetQueueStatus(steamId, DateTimeOffset.UtcNow))
                    .WithStyle("font-size", "17px")
                    .WithStyle("font-weight", "bold")
                    .WithStyle("color", "#7FD4FF")
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("margin-top", "10px"),

                UI.Horizontal()
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("margin-top", "10px")
                    .Add(
                        UI.Label("date", currentDate)
                            .WithStyle("font-size", "14px")
                            .WithStyle("color", "#777777"),

                        UI.Label("time", currentTime)
                            .WithStyle("font-size", "14px")
                            .WithStyle("color", "#777777")
                            .WithStyle("margin-left", "8px")
                    )
            );
    }

    /* Queue table: fixed column widths keep the numbers lined up */
    private const string QueueNameColumnWidth = "100px";
    private const string QueueNumberColumnWidth = "80px";

    private static UINode QueueTableRow(
        string idPrefix,
        string name,
        string queued,
        string playing
    ) =>
        UI.Horizontal()
            .WithStyle("horizontal-align", "center")
            .Add(
                QueueTableCell(idPrefix + "name", name, QueueNameColumnWidth),
                QueueTableCell(idPrefix + "queued", queued, QueueNumberColumnWidth),
                QueueTableCell(idPrefix + "playing", playing, QueueNumberColumnWidth)
            );

    private static UINode QueueTableCell(
        string id,
        string text,
        string width,
        bool header = false
    ) =>
        UI.Label(id, text)
            .WithStyle("width", width)
            .WithStyle("font-size", header ? "12px" : "17px")
            .WithStyle("color", header ? "#888888" : "#FFFFFFFF")
            .WithStyle("text-align", width == QueueNameColumnWidth ? "left" : "center");

    private string GetQueueStatus(ulong steamId, DateTimeOffset now) =>
        GetQueuePosition(steamId, now) ?? "/join ranked or /join unranked";

    private string? GetQueuePosition(ulong steamId, DateTimeOffset now)
    {
        foreach (
            (List<QueueEntry> queue, string queueName) in new[]
            {
                (server.RankedQueue, "Ranked"),
                (server.UnrankedQueue, "Unranked")
            }
        )
        {
            int index = queue.FindIndex(entry => entry.SteamId == steamId);
            if (index < 0)
                continue;

            TimeSpan waited = now - queue[index].EnqueuedAt;
            if (waited < TimeSpan.Zero)
                waited = TimeSpan.Zero;

            return $"In {queueName} queue: #{index + 1} · {(int)waited.TotalMinutes}:{waited.Seconds:00}";
        }

        return null;
    }

    private void ScheduleClockRefresh()
    {
        server.ScheduleOnce(1.Seconds(), () =>
        {
            ScheduleClockRefresh();
            RefreshClock();
        });
    }

    private DateTimeOffset GetLocalTime(ulong steamId)
        => GetLocalTime(steamId, DateTimeOffset.UtcNow);

    private DateTimeOffset GetLocalTime(ulong steamId, DateTimeOffset utcNow)
    {
        TimeZoneInfo timeZone = server.PlayerManager.GetTimeZone(steamId);
        return TimeZoneInfo.ConvertTime(utcNow, timeZone);
    }

    private void RefreshClock()
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        foreach (var controller in Players.GetAll())
        {
            if (server.PlayerManager.TryGetActiveMatch(controller, out var activeMatch))
            {
                if (!controller.IsBot && server.PlayerManager.IsBotMatch(activeMatch))
                {
                    UI.Panel("duelhud").Set(
                        controller.Recipients,
                        "duelqueue",
                        GetQueuePosition(controller.PlayerSteamId, utcNow) ?? ""
                    );
                }

                continue;
            }

            if (hiddenServerInfo.Contains(controller.PlayerSteamId))
                continue;

            ulong steamId = controller.PlayerSteamId;
            if (!serverInfoPanelState.TryGetValue(steamId, out bool built) || !built)
                continue;

            DateTimeOffset localNow = GetLocalTime(steamId, utcNow);
            var recipients = controller.Recipients;
            string date = localNow.ToString(DateFormat, CultureInfo.InvariantCulture);
            if (!lastSentDate.TryGetValue(steamId, out string? sentDate) || sentDate != date)
            {
                UI.Panel("queuepanel").Set(recipients, "date", date);
                lastSentDate[steamId] = date;
            }

            // The clock shows minutes, so it is only sent when the minute changes.
            string time = localNow.ToString(TimeFormat, CultureInfo.InvariantCulture);
            if (!lastSentTime.TryGetValue(steamId, out string? sentTime) || sentTime != time)
            {
                UI.Panel("queuepanel").Set(recipients, "time", time);
                lastSentTime[steamId] = time;
            }
            UI.Panel("queuepanel").Set(
                recipients,
                "queuestatus",
                GetQueueStatus(steamId, utcNow)
            );
        }
    }

    public void RefreshHud()
    {
        var players = Players.GetAll().ToArray();
        var stats = GetServerInfoStats(players.Length);
        var panel = UI.Panel("queuepanel");
        foreach (var controller in players)
        {
            ulong steamId = controller.PlayerSteamId;
            bool wantVisible =
                !hiddenServerInfo.Contains(steamId) &&
                !server.PlayerManager.TryGetActiveMatch(controller, out _);

            if (serverInfoPanelState.TryGetValue(steamId, out bool built))
            {
                if (wantVisible && built)
                {
                    // Panel already on screen: update only the changing labels.
                    panel.Set(controller.Recipients, "playercount", stats.PlayerCountText);
                    panel.Set(controller.Recipients, "rankedqueued", stats.RankedQueued);
                    panel.Set(controller.Recipients, "rankedplaying", stats.RankedPlaying);
                    panel.Set(controller.Recipients, "unrankedqueued", stats.UnrankedQueued);
                    panel.Set(controller.Recipients, "unrankedplaying", stats.UnrankedPlaying);
                    panel.Set(
                        controller.Recipients,
                        "queuestatus",
                        GetQueueStatus(steamId, DateTimeOffset.UtcNow)
                    );
                    continue;
                }

                if (!wantVisible && !built)
                    continue; // already hidden
            }

            ShowServerInfo(controller, stats);
        }
    }

    public void RestoreHudAfterMatch(DuelMatch match)
    {
        string[] staleMatchKeys = server.ActiveMatches
            .Where(entry => ReferenceEquals(entry.Value, match))
            .Select(entry => entry.Key)
            .ToArray();
        foreach (string key in staleMatchKeys)
            server.ActiveMatches.Remove(key);
        server.LeaderboardManager.RefreshActiveMatches();

        void RestoreParticipants()
        {
            foreach (string playerName in new[] { match.Player1, match.Player2 })
            {
                var controller = server.PlayerManager.FindMatchParticipant(
                    playerName
                );
                if (controller == null)
                {
                    continue;
                }

                hiddenServerInfo.Remove(controller.PlayerSteamId);
                HideMatchHud(controller);
                ShowServerInfo(controller);
            }

        }

        RestoreParticipants();
        // Everyone else only needs the queue/match counts updated. Rebuilding every
        // player's panels here (four times per match end, including tearing down and
        // rebuilding the HUD of every other duel in progress) was the heaviest part
        // of finishing a match.
        RefreshHud();
        server.ScheduleOnce(0.5.Seconds(), RestoreParticipants);
        server.ScheduleOnce(1.5.Seconds(), RestoreParticipants);
        server.ScheduleOnce(3.Seconds(), RestoreParticipants);
    }

    public void RestoreServerInfoAfterSpawn(CCitadelPlayerController player)
    {
        void Restore()
        {
            if (
                !player.IsValid ||
                server.PlayerManager.TryGetActiveMatch(player, out _)
            )
                return;

            hiddenServerInfo.Remove(player.PlayerSteamId);
            HideMatchHud(player);
            ShowServerInfo(player);
        }

        Restore();
        server.ScheduleOnce(0.5.Seconds(), Restore);
    }

    public void SetServerInfoVisibility(
        CCitadelPlayerController player,
        bool visible)
    {
        ulong steamId = player.PlayerSteamId;
        if (visible)
            hiddenServerInfo.Remove(steamId);
        else
            hiddenServerInfo.Add(steamId);

        if (visible)
        {
            ShowServerInfo(player);
            return;
        }

        UI.Panel("queuepanel").DestroyLayout(player.Recipients);
        serverInfoPanelState[steamId] = false;
    }

    public void ShowQueuePanel(dynamic? player)
    {
        if (player == null)
            return;

        ShowServerInfo(player);
    }

    public void HideQueuePanel(params dynamic?[] players)
    {
        foreach (var player in players)
        {
            if (player == null)
                continue;

            UI.Panel("queuepanel")
                .DestroyLayout(player.Recipients);
            serverInfoPanelState[(ulong)player.PlayerSteamId] = false;
        }
    }

    public void ShowQueuePanel(params dynamic?[] players)
    {
        foreach (var player in players)
            ShowServerInfo(player);
    }

    private void ShowServerInfo(dynamic? player)
    {
        if (player == null)
            return;

        // Statically typed path: avoids dynamic dispatch for the common case.
        if (player is CCitadelPlayerController citadelController)
        {
            ShowServerInfo(citadelController, GetServerInfoStats());
            return;
        }

        var panel = UI.Panel("queuepanel");
        ulong steamId = player.PlayerSteamId;
        if (
            hiddenServerInfo.Contains(steamId) ||
            (
                player is CBasePlayerController controller &&
                server.PlayerManager.TryGetActiveMatch(controller, out _)
            )
        )
        {
            panel.DestroyLayout(player.Recipients);
            serverInfoPanelState[steamId] = false;
            return;
        }

        panel.BuildLayout(player.Recipients, Layout(steamId, GetServerInfoStats()));
        serverInfoPanelState[steamId] = true;
        lastSentDate.Remove(steamId);
        lastSentTime.Remove(steamId);
    }

    private void ShowServerInfo(
        CCitadelPlayerController player,
        ServerInfoStats stats)
    {
        var panel = UI.Panel("queuepanel");
        ulong steamId = player.PlayerSteamId;
        if (
            hiddenServerInfo.Contains(steamId) ||
            server.PlayerManager.TryGetActiveMatch(player, out _)
        )
        {
            panel.DestroyLayout(player.Recipients);
            serverInfoPanelState[steamId] = false;
            return;
        }

        panel.BuildLayout(player.Recipients, Layout(steamId, stats));
        serverInfoPanelState[steamId] = true;
        lastSentDate.Remove(steamId);
        lastSentTime.Remove(steamId);
    }

    /* Duel Scoreboard */

    public void ShowMatchHud(DuelMatch match)
    {
        var p1 = server.PlayerManager.FindMatchParticipant(match.Player1);
        var p2 = server.PlayerManager.FindMatchParticipant(match.Player2);

        if (p1 == null || p2 == null)
            return;

        HideQueuePanel(p1, p2);
        HideMatchHud(p1, p2);

        string title =
            $"{FormatMatchPlayerName(p1)} vs " +
            $"{FormatMatchPlayerName(p2)}";
        string score = $"{match.Score1} - {match.Score2}";

        var layout = UI.Vertical()
            .WithStyle("horizontal-align", "center")
            .WithStyle("vertical-align", "top")
            .WithStyle("margin-top", "0px")
            .WithStyle("background-color", "rgba(17, 17, 17, 0.86)")
            .WithStyle("padding", "10px 20px")
            .Add(

                UI.Label("dueltitle", title)
                    .WithStyle("font-size", "18px")
                    .WithStyle("color", "#FFFFFF")
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("font-weight", "bold"),

                UI.Label("separator", "────────")
                    .WithStyle("font-size", "16px")
                    .WithStyle("color", "#fefafc")
                    .WithStyle("horizontal-align", "center"),

                UI.Label("duelscore", score)
                    .WithStyle("font-size", "20px")
                    .WithStyle("color", "#FF8A00")
                    .WithStyle("horizontal-align", "center")
            );

        // Bot matches are the only ones a player can be queued during: show their
        // queue position under the score (RefreshClock keeps it ticking).
        if (p1.IsBot || p2.IsBot)
        {
            var human = p1.IsBot ? p2 : p1;
            layout.Add(
                UI.Label(
                    "duelqueue",
                    GetQueuePosition(human.PlayerSteamId, DateTimeOffset.UtcNow) ?? ""
                )
                    .WithStyle("font-size", "15px")
                    .WithStyle("font-weight", "bold")
                    .WithStyle("color", "#7FD4FF")
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("margin-top", "4px")
            );
        }

        UI.Panel("duelhud").BuildLayout(p1.Recipients, layout);
        UI.Panel("duelhud").BuildLayout(p2.Recipients, layout);
    }

    private string FormatMatchPlayerName(
        CCitadelPlayerController player
    )
    {
        string? practiceBotName =
            server.PlayerManager.GetPracticeBotDisplayName(player);
        if (practiceBotName != null)
            return practiceBotName;

        return server.GuildManager.FormatPlayerNameWithSelectedTag(player);
    }

    public void UpdateMatchHud(DuelMatch match)
    {
        string score = $"{match.Score1} - {match.Score2}";

        var p1 = server.PlayerManager.FindMatchParticipant(match.Player1);
        var p2 = server.PlayerManager.FindMatchParticipant(match.Player2);

        if (p1 != null)
        {
            UI.Panel("duelhud")
                .Set(p1.Recipients, "duelscore", score);
        }

        if (p2 != null)
        {
            UI.Panel("duelhud")
                .Set(p2.Recipients, "duelscore", score);
        }
    }

    public void HideMatchHud(params dynamic?[] players)
    {
        foreach (var player in players)
        {
            if (player == null)
                continue;

            UI.Panel("duelhud")
                .DestroyLayout(player.Recipients);
        }
    }
}