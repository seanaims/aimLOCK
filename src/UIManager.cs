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

        if (
            !server.ActiveMatches.TryGetValue(
                controller.PlayerName,
                out var match
            ) ||
            match.Finished
        )
        {
            HideMatchHud(controller);
            ShowServerInfo(controller);
            return;
        }

        ShowMatchHud(match);
    }

    /* Server Information HUD */

    private UINode Layout(ulong steamId)
    {
        int playerCount = Players.GetAll().Count();
        int rankedInMatchCount = server.ActiveMatches.Values
            .Distinct()
            .Count(match => match.QueueType == MatchQueueType.Ranked) * 2;
        int unrankedInMatchCount = server.ActiveMatches.Values
            .Distinct()
            .Count(match => match.QueueType == MatchQueueType.Unranked) * 2;
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
                            .WithStyle("color", "#FF8A00"),

                        UI.Label("serverdomain", ".gg")
                            .WithStyle("font-size", "26px")
                            .WithStyle("font-weight", "bold")
                            .WithStyle("color", "#555555")
                    ),

                UI.Label("separator", "─────────────────")
                    .WithStyle("font-size", "16px")
                    .WithStyle("color", "#fefafc")
                    .WithStyle("horizontal-align", "center"),

                UI.Label("playercount", $"Players Online: {playerCount}")
                    .WithStyle("font-size", "18px")
                    .WithStyle("color", "#FFFFFFFF")
                    .WithStyle("horizontal-align", "center")
                    .WithStyle("text-align", "center"),

                UI.Label("separator", "─────────────────")
                    .WithStyle("font-size", "16px")
                    .WithStyle("color", "#fefafc")
                    .WithStyle("horizontal-align", "center"),

                UI.Label(
                    "overallinfo",
                    $"\nRanked Queue: {server.RankedQueue.Count}" +
                    $"\nIn Match: {rankedInMatchCount}\n" +
                    $"\nUnranked Queue: {server.UnrankedQueue.Count}" +
                    $"\nIn Match: {unrankedInMatchCount}\n"
                )
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
    {
        TimeZoneInfo timeZone = server.PlayerManager.GetTimeZone(steamId);
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
    }

    private void RefreshClock()
    {
        foreach (var controller in Players.GetAll())
        {
            if (
                server.ActiveMatches.ContainsKey(controller.PlayerName) ||
                hiddenServerInfo.Contains(controller.PlayerSteamId)
            )
            {
                continue;
            }

            DateTimeOffset localNow = GetLocalTime(controller.PlayerSteamId);
            UI.Panel("queuepanel").Set(
                controller.Recipients,
                "date",
                localNow.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
            );
            UI.Panel("queuepanel").Set(
                controller.Recipients,
                "time",
                localNow.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture)
            );
        }
    }

    public void RefreshHud()
    {
        foreach (var controller in Players.GetAll())
            ShowServerInfo(controller);
    }

    public void RestoreHudAfterMatch(DuelMatch match)
    {
        string[] staleMatchKeys = server.ActiveMatches
            .Where(entry => ReferenceEquals(entry.Value, match))
            .Select(entry => entry.Key)
            .ToArray();
        foreach (string key in staleMatchKeys)
            server.ActiveMatches.Remove(key);

        foreach (var controller in Players.GetAll())
        {
            if (server.ActiveMatches.ContainsKey(controller.PlayerName))
                continue;

            HideMatchHud(controller);
            ShowServerInfo(controller);
        }
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

        var panel = UI.Panel("queuepanel");
        if (
            hiddenServerInfo.Contains((ulong)player.PlayerSteamId) ||
            server.ActiveMatches.ContainsKey(player.PlayerName)
        )
        {
            panel.DestroyLayout(player.Recipients);
            return;
        }

        ulong steamId = player.PlayerSteamId;
        panel.BuildLayout(player.Recipients, Layout(steamId));
    }

    /* Duel Scoreboard */

    public void ShowMatchHud(DuelMatch match)
    {
        var p1 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player1);

        var p2 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player2);

        if (p1 == null || p2 == null)
            return;

        HideQueuePanel(p1, p2);
        HideMatchHud(p1, p2);

        string title = $"{match.Player1} vs {match.Player2}";
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

    public void UpdateMatchHud(DuelMatch match)
    {
        string score = $"{match.Score1} - {match.Score2}";

        var p1 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player1);

        var p2 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player2);

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