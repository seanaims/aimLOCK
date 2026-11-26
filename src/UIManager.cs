/*
Main UI functionality here
Info panel, player count, queue count, date, scoreboard, winner, etc
*/

namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

public class UIManager
{
    private const string DateFormat = "MM/dd/yyyy";

    private const string TimeFormat = "hh:mm tt";
    /* Queue table: fixed column widths keep the numbers lined up */
    private const string QueueNameColumnWidth = "100px";

    private const string QueueNumberColumnWidth = "80px";
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
        this.ScheduleClockRefresh();
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
                !this.hiddenServerInfo.Contains(steamId) &&
                !this.server.PlayerManager.TryGetActiveMatch(controller, out _);

            if (this.serverInfoPanelState.TryGetValue(steamId, out bool built))
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
                        this.GetQueueStatus(steamId, DateTimeOffset.UtcNow));
                    continue;
                }

                if (!wantVisible && !built)
                {
                    continue; // already hidden
                }
            }

            ShowServerInfo(controller, stats);
        }
    }

    public void RestoreHudAfterMatch(DuelMatch match)
    {
        string[] staleMatchKeys = this.server.ActiveMatches
            .Where(entry => ReferenceEquals(entry.Value, match))
            .Select(entry => entry.Key)
            .ToArray();
        foreach (string key in staleMatchKeys)
        {
            this.server.ActiveMatches.Remove(key);
        }

        this.server.LeaderboardManager.RefreshActiveMatches();

        void RestoreParticipants()
        {
            foreach (string playerName in new[] { match.Player1, match.Player2 })
            {
                var controller = this.server.PlayerManager.FindMatchParticipant(
                    playerName);
                if (controller == null)
                {
                    continue;
                }

                this.hiddenServerInfo.Remove(controller.PlayerSteamId);
                HideMatchHud(controller);
                ShowServerInfo(controller);
            }
        }

        RestoreParticipants();

        // Everyone else only needs the queue/match counts updated. Rebuilding every
        // player's panels here (four times per match end, including tearing down and
        // rebuilding the HUD of every other duel in progress) was the heaviest part
        // of finishing a match.
        this.RefreshHud();
        this.server.ScheduleOnce(0.5.Seconds(), RestoreParticipants);
        this.server.ScheduleOnce(1.5.Seconds(), RestoreParticipants);
        this.server.ScheduleOnce(3.Seconds(), RestoreParticipants);
    }

    public void RestoreServerInfoAfterSpawn(CCitadelPlayerController player)
    {
        void Restore()
        {
            if (
                !player.IsValid ||
                this.server.PlayerManager.TryGetActiveMatch(player, out _))
            {
                return;
            }

            this.hiddenServerInfo.Remove(player.PlayerSteamId);
            HideMatchHud(player);
            ShowServerInfo(player);
        }

        Restore();
        this.server.ScheduleOnce(0.5.Seconds(), Restore);
    }

    public void SetServerInfoVisibility(
        CCitadelPlayerController player,
        bool visible)
    {
        ulong steamId = player.PlayerSteamId;
        if (visible)
        {
            this.hiddenServerInfo.Remove(steamId);
        }
        else
        {
            this.hiddenServerInfo.Add(steamId);
        }

        if (visible)
        {
            ShowServerInfo(player);
            return;
        }

        UI.Panel("queuepanel").DestroyLayout(player.Recipients);
        this.serverInfoPanelState[steamId] = false;
    }

    public void ShowQueuePanel(dynamic? player)
    {
        if (player == null)
        {
            return;
        }

        this.ShowServerInfo(player);
    }

    public void HideQueuePanel(params dynamic?[] players)
    {
        foreach (var player in players)
        {
            if (player == null)
            {
                continue;
            }

            UI.Panel("queuepanel")
                .DestroyLayout(player.Recipients);
            this.serverInfoPanelState[(ulong)player.PlayerSteamId] = false;
        }
    }

    public void ShowQueuePanel(params dynamic?[] players)
    {
        foreach (var player in players)
        {
            this.ShowServerInfo(player);
        }
    }

    /* Duel Scoreboard */

    public void ShowMatchHud(DuelMatch match)
    {
        var p1 = this.server.PlayerManager.FindMatchParticipant(match.Player1);
        var p2 = this.server.PlayerManager.FindMatchParticipant(match.Player2);

        if (p1 == null || p2 == null)
        {
            return;
        }

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
                    .WithStyle("horizontal-align", "center"));

        // Bot matches are the only ones a player can be queued during: show their
        // queue position under the score (RefreshClock keeps it ticking).
        if (p1.IsBot || p2.IsBot)
        {
            var human = p1.IsBot ? p2 : p1;
            layout.Add(
                UI.Label(
                    "duelqueue",
                    GetQueuePosition(human.PlayerSteamId, DateTimeOffset.UtcNow) ?? string.Empty)
                    .WithStyle("font-size", "15px")
                    .WithStyle("font-weight", "bold")
                    .WithStyle("color", "#7FD4FF")
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("margin-top", "4px"));
        }

        UI.Panel("duelhud").BuildLayout(p1.Recipients, layout);
        UI.Panel("duelhud").BuildLayout(p2.Recipients, layout);
    }

    public void UpdateMatchHud(DuelMatch match)
    {
        string score = $"{match.Score1} - {match.Score2}";

        var p1 = this.server.PlayerManager.FindMatchParticipant(match.Player1);
        var p2 = this.server.PlayerManager.FindMatchParticipant(match.Player2);

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
            {
                continue;
            }

            UI.Panel("duelhud")
                .DestroyLayout(player.Recipients);
        }
    }

    private static UINode PanelDivider() =>
        UI.Horizontal()
            .WithStyle("width", "260px")
            .WithStyle("height", "1px")
            .WithStyle("horizontal-align", "center")
            .WithStyle("margin-top", "8px")
            .WithStyle("margin-bottom", "8px")
            .WithStyle("background-color", "rgba(255, 255, 255, 0.15)");

    private static UINode QueueTableRow(
        string idPrefix,
        string name,
        string queued,
        string playing) =>
        UI.Horizontal()
            .WithStyle("horizontal-align", "center")
            .Add(
                QueueTableCell(idPrefix + "name", name, QueueNameColumnWidth),
                QueueTableCell(idPrefix + "queued", queued, QueueNumberColumnWidth),
                QueueTableCell(idPrefix + "playing", playing, QueueNumberColumnWidth));

    private static UINode QueueTableCell(
        string id,
        string text,
        string width,
        bool header = false) =>
        UI.Label(id, text)
            .WithStyle("width", width)
            .WithStyle("font-size", header ? "12px" : "17px")
            .WithStyle("color", header ? "#888888" : "#FFFFFFFF")
            .WithStyle("text-align", width == QueueNameColumnWidth ? "left" : "center");

    private void OnClientResync(int playerSlot)
    {
        var controller = Players.GetAll()
            .FirstOrDefault(player => player.Slot == playerSlot);

        if (controller == null)
        {
            return;
        }

        // The client lost its panels; forget what we think it has.
        this.serverInfoPanelState.Remove(controller.PlayerSteamId);
        this.lastSentDate.Remove(controller.PlayerSteamId);
        this.lastSentTime.Remove(controller.PlayerSteamId);
        ReconcileHud(controller);

        // Follow-up pass: only rebuilds if the first one did not leave the server-info
        // panel in a known state; otherwise a resync sent every panel twice.
        this.server.ScheduleOnce(0.25.Seconds(), () =>
        {
            if (!controller.IsValid)
            {
                return;
            }

            if (
                this.server.PlayerManager.TryGetActiveMatch(controller, out var match) &&
                !match.Finished)
            {
                return;
            }

            if (!this.serverInfoPanelState.ContainsKey(controller.PlayerSteamId))
            {
                ReconcileHud(controller);
            }
        });
    }

    private void ReconcileHud(CCitadelPlayerController controller)
    {
        if (!controller.IsValid)
        {
            return;
        }

        if (
            this.server.PlayerManager.TryGetActiveMatch(controller, out var match) &&
            !match.Finished)
        {
            this.ShowMatchHud(match);
            return;
        }

        HideMatchHud(controller);
        ShowServerInfo(controller);
    }

    private ServerInfoStats GetServerInfoStats()
        => this.GetServerInfoStats(Players.GetAll().Count());

    private ServerInfoStats GetServerInfoStats(int playerCount)
    {
        int rankedMatches = 0;
        int unrankedMatches = 0;
        foreach (var match in this.server.ActiveMatches.Values.Distinct())
        {
            if (match.QueueType == MatchQueueType.Ranked)
            {
                rankedMatches++;
            }
            else if (match.QueueType == MatchQueueType.Unranked)
            {
                unrankedMatches++;
            }
        }

        return new ServerInfoStats(
            $"{playerCount} online",
            this.server.RankedQueue.Count.ToString(CultureInfo.InvariantCulture),
            (rankedMatches * 2).ToString(CultureInfo.InvariantCulture),
            this.server.UnrankedQueue.Count.ToString(CultureInfo.InvariantCulture),
            (unrankedMatches * 2).ToString(CultureInfo.InvariantCulture));
    }

    private UINode Layout(ulong steamId, ServerInfoStats stats)
    {
        DateTimeOffset localNow = this.GetLocalTime(steamId);
        string currentDate = localNow.ToString(DateFormat, CultureInfo.InvariantCulture);
        string currentTime = localNow.ToString(TimeFormat, CultureInfo.InvariantCulture);

        return UI.Vertical()
            .WithStyle("horizontal-align", "right")
            .WithStyle("vertical-align", "center")
            .WithStyle("margin-top", "-80px")
            .WithStyle("background-color", "rgba(17, 17, 17, 0.88)")
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
                            .WithStyle("color", "#555555")),
                PanelDivider(),
                UI.Label("playercount", stats.PlayerCountText)
                    .WithStyle("font-size", "16px")
                    .WithStyle("color", "#AAAAAA")
                    .WithStyle("horizontal-align", "center"),
                PanelDivider(),
                UI.Horizontal()
                    .WithStyle("horizontal-align", "center")
                    .Add(
                        QueueTableCell("queuehead", string.Empty, QueueNameColumnWidth, header: true),
                        QueueTableCell("queuedhead", "QUEUED", QueueNumberColumnWidth, header: true),
                        QueueTableCell("playinghead", "PLAYING", QueueNumberColumnWidth, header: true)),
                QueueTableRow(
                    "ranked",
                    "Ranked",
                    stats.RankedQueued,
                    stats.RankedPlaying),
                QueueTableRow(
                    "unranked",
                    "Unranked",
                    stats.UnrankedQueued,
                    stats.UnrankedPlaying),
                UI.Label("queuestatus", this.GetQueueStatus(steamId, DateTimeOffset.UtcNow))
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
                            .WithStyle("margin-left", "8px")));
    }

    private string GetQueueStatus(ulong steamId, DateTimeOffset now) =>
        this.GetQueuePosition(steamId, now) ?? "/join ranked or /join unranked";

    private string? GetQueuePosition(ulong steamId, DateTimeOffset now)
    {
        foreach (
            (List<QueueEntry> queue, string queueName) in new[]
            {
                (this.server.RankedQueue, "Ranked"),
                (this.server.UnrankedQueue, "Unranked"),
            })
        {
            int index = queue.FindIndex(entry => entry.SteamId == steamId);
            if (index < 0)
            {
                continue;
            }

            TimeSpan waited = now - queue[index].EnqueuedAt;
            if (waited < TimeSpan.Zero)
            {
                waited = TimeSpan.Zero;
            }

            return $"In {queueName} queue: #{index + 1} · {(int)waited.TotalMinutes}:{waited.Seconds:00}";
        }

        return null;
    }

    private void ScheduleClockRefresh()
    {
        this.server.ScheduleOnce(1.Seconds(), () =>
        {
            this.ScheduleClockRefresh();
            this.RefreshClock();
        });
    }

    private DateTimeOffset GetLocalTime(ulong steamId)
        => this.GetLocalTime(steamId, DateTimeOffset.UtcNow);

    private DateTimeOffset GetLocalTime(ulong steamId, DateTimeOffset utcNow)
    {
        TimeZoneInfo timeZone = this.server.PlayerManager.GetTimeZone(steamId);
        return TimeZoneInfo.ConvertTime(utcNow, timeZone);
    }

    private void RefreshClock()
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        foreach (var controller in Players.GetAll())
        {
            if (this.server.PlayerManager.TryGetActiveMatch(controller, out var activeMatch))
            {
                if (!controller.IsBot && this.server.PlayerManager.IsBotMatch(activeMatch))
                {
                    UI.Panel("duelhud").Set(
                        controller.Recipients,
                        "duelqueue",
                        GetQueuePosition(controller.PlayerSteamId, utcNow) ?? string.Empty);
                }

                continue;
            }

            if (this.hiddenServerInfo.Contains(controller.PlayerSteamId))
            {
                continue;
            }

            ulong steamId = controller.PlayerSteamId;
            if (!this.serverInfoPanelState.TryGetValue(steamId, out bool built) || !built)
            {
                continue;
            }

            DateTimeOffset localNow = this.GetLocalTime(steamId, utcNow);
            var recipients = controller.Recipients;
            string date = localNow.ToString(DateFormat, CultureInfo.InvariantCulture);
            if (!this.lastSentDate.TryGetValue(steamId, out string? sentDate) || sentDate != date)
            {
                UI.Panel("queuepanel").Set(recipients, "date", date);
                this.lastSentDate[steamId] = date;
            }

            // The clock shows minutes, so it is only sent when the minute changes.
            string time = localNow.ToString(TimeFormat, CultureInfo.InvariantCulture);
            if (!this.lastSentTime.TryGetValue(steamId, out string? sentTime) || sentTime != time)
            {
                UI.Panel("queuepanel").Set(recipients, "time", time);
                this.lastSentTime[steamId] = time;
            }

            UI.Panel("queuepanel").Set(
                recipients,
                "queuestatus",
                this.GetQueueStatus(steamId, utcNow));
        }
    }

    private void ShowServerInfo(dynamic? player)
    {
        if (player == null)
        {
            return;
        }

        // Statically typed path: avoids dynamic dispatch for the common case.
        if (player is CCitadelPlayerController citadelController)
        {
            this.ShowServerInfo(citadelController, this.GetServerInfoStats());
            return;
        }

        var panel = UI.Panel("queuepanel");
        ulong steamId = player.PlayerSteamId;
        if (
            this.hiddenServerInfo.Contains(steamId) ||
            (
                player is CBasePlayerController controller &&
                this.server.PlayerManager.TryGetActiveMatch(controller, out _)))
        {
            panel.DestroyLayout(player.Recipients);
            this.serverInfoPanelState[steamId] = false;
            return;
        }

        panel.BuildLayout(player.Recipients, this.Layout(steamId, this.GetServerInfoStats()));
        this.serverInfoPanelState[steamId] = true;
        this.lastSentDate.Remove(steamId);
        this.lastSentTime.Remove(steamId);
    }

    private void ShowServerInfo(
        CCitadelPlayerController player,
        ServerInfoStats stats)
    {
        var panel = UI.Panel("queuepanel");
        ulong steamId = player.PlayerSteamId;
        if (
            this.hiddenServerInfo.Contains(steamId) ||
            this.server.PlayerManager.TryGetActiveMatch(player, out _))
        {
            panel.DestroyLayout(player.Recipients);
            this.serverInfoPanelState[steamId] = false;
            return;
        }

        panel.BuildLayout(player.Recipients, this.Layout(steamId, stats));
        this.serverInfoPanelState[steamId] = true;
        this.lastSentDate.Remove(steamId);
        this.lastSentTime.Remove(steamId);
    }

    private string FormatMatchPlayerName(
        CCitadelPlayerController player)
    {
        string? practiceBotName =
            this.server.PlayerManager.GetPracticeBotDisplayName(player);
        if (practiceBotName != null)
        {
            return practiceBotName;
        }

        return this.server.GuildManager.FormatPlayerNameWithSelectedTag(player);
    }

    /* Server Information HUD */

    private readonly record struct ServerInfoStats(
        string PlayerCountText,
        string RankedQueued,
        string RankedPlaying,
        string UnrankedQueued,
        string UnrankedPlaying);
}