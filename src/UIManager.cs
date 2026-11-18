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
        string OverallInfoText
    );

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
            $"Players Online: {playerCount}",
            $"\nRanked Queue: {server.RankedQueue.Count}" +
            $"\nIn Match: {rankedMatches * 2}\n" +
            $"\nUnranked Queue: {server.UnrankedQueue.Count}" +
            $"\nIn Match: {unrankedMatches * 2}\n"
        );
    }

    private UINode Layout(ulong steamId, ServerInfoStats stats)
    {
        DateTimeOffset localNow = GetLocalTime(steamId);
        string currentDate = localNow.ToString(
            "MM/dd/yyyy",
            CultureInfo.InvariantCulture
        );
        string currentTime = localNow.ToString(
            "hh:mm:ss tt",
            CultureInfo.InvariantCulture
        );

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

                UI.Label("separator", "─────────────────")
                    .WithStyle("font-size", "16px")
                    .WithStyle("color", "#fefafc")
                    .WithStyle("horizontal-align", "center"),

                UI.Label("playercount", stats.PlayerCountText)
                    .WithStyle("font-size", "18px")
                    .WithStyle("color", "#FFFFFFFF")
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("text-align", "center"),

                UI.Label("separator", "─────────────────")
                    .WithStyle("font-size", "16px")
                    .WithStyle("color", "#fefafc")
                    .WithStyle("horizontal-align", "center"),

                UI.Label("overallinfo", stats.OverallInfoText)
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("text-align", "center")
                    .WithStyle("font-size", "18px")
                    .WithStyle("color", "#FFFFFFFF"),

                UI.Label("date", currentDate)
                    .WithStyle("font-size", "14px")
                    .WithStyle("color", "#AAAAAA")
                    .WithStyle("margin-top", "8px"),

                UI.Label("time", currentTime)
                    .WithStyle("font-size", "14px")
                    .WithStyle("color", "#AAAAAA")
                    .WithStyle("margin-top", "8px")
            );
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
            if (
                server.PlayerManager.TryGetActiveMatch(controller, out _) ||
                hiddenServerInfo.Contains(controller.PlayerSteamId)
            )
            {
                continue;
            }

            ulong steamId = controller.PlayerSteamId;
            if (!serverInfoPanelState.TryGetValue(steamId, out bool built) || !built)
                continue;

            DateTimeOffset localNow = GetLocalTime(steamId, utcNow);
            var recipients = controller.Recipients;
            string date = localNow.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            if (!lastSentDate.TryGetValue(steamId, out string? sentDate) || sentDate != date)
            {
                UI.Panel("queuepanel").Set(recipients, "date", date);
                lastSentDate[steamId] = date;
            }

            // The clock is cosmetic: send it unreliably so it never queues up behind
            // (or delays) important UI traffic; the next second replaces it anyway.
            UI.Panel("queuepanel").SetUnreliable(
                recipients,
                "time",
                localNow.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture)
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
                    // Panel already on screen: update only the two changing labels.
                    panel.Set(controller.Recipients, "playercount", stats.PlayerCountText);
                    panel.Set(controller.Recipients, "overallinfo", stats.OverallInfoText);
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