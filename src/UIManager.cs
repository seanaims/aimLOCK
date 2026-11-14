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

        ReconcileHud(controller);
        server.ScheduleOnce(0.25.Seconds(), () =>
            ReconcileHud(controller));
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
                server.PlayerManager.TryGetActiveMatch(controller, out _) ||
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

        void RestoreParticipants()
        {
            foreach (string playerName in new[] { match.Player1, match.Player2 })
            {
                var controller = server.PlayerManager.FindMatchParticipant(
                    playerName
                );
                if (
                    controller == null ||
                    server.PlayerManager.TryGetActiveMatch(controller, out _)
                )
                {
                    continue;
                }

                hiddenServerInfo.Remove(controller.PlayerSteamId);
            }

            foreach (var controller in Players.GetAll())
                ReconcileHud(controller);
        }

        RestoreParticipants();
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
            (
                player is CBasePlayerController controller &&
                server.PlayerManager.TryGetActiveMatch(controller, out _)
            )
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
    ) =>
        server.PlayerManager.GetPracticeBotDisplayName(player) ??
        server.GuildManager.FormatPlayerName(player);

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