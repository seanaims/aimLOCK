/*
Main UI functionality here
Info panel, player count, queue count, date, scoreboard, winner, etc
*/

using System;
using System.Linq;

using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace aimLOCK;

public class UIManager
{
    private readonly aimLOCK server;

    public UIManager(aimLOCK server)
    {
        this.server = server;
    }

    /* Server Information HUD */

    private UINode Layout(int queueCount)
    {
        int playerCount = Players.GetAll().Count();
        string currentDate = DateTime.Now.ToString("MM/dd/yyyy");

        return UI.Vertical()
            .WithStyle("horizontal-align", "right")
            .WithStyle("vertical-align", "center")
            .WithStyle("margin-top", "-80px")
            .WithStyle("background-color", "rgba(17, 17, 17, 0.65)")
            .WithStyle("padding", "15px 15px")
            .Add(

                UI.Label("servertitle", "aimlock.gg")
                    .WithStyle("font-size", "26px")
                    .WithStyle("font-weight", "bold")
                    .WithStyle("color", "#ff9beb")
                    .WithStyle("horizontal-align", "left"),

                UI.Label("separator", "─────────────────")
                    .WithStyle("font-size", "16px")
                    .WithStyle("color", "#fefafc")
                    .WithStyle("horizontal-align", "center"),

                UI.Label(
                    "overallinfo",
                    $"l> Players Online: {playerCount}\nl> In Queue: {queueCount}"
                )
                    .WithStyle("font-size", "18px")
                    .WithStyle("color", "#FFFFFFFF"),

                UI.Label("date", currentDate)
                    .WithStyle("font-size", "14px")
                    .WithStyle("color", "#AAAAAA")
                    .WithStyle("margin-top", "8px")
            );
    }

    public void RefreshHud()
    {
        foreach (var controller in Players.GetAll())
        {
            UI.Panel("queuepanel")
                .BuildLayout(
                    controller.Recipients,
                    Layout(server.queue.Count)
                );
        }
    }

    public void ShowQueuePanel(dynamic player)
    {
        if (player == null)
            return;

        UI.Panel("queuepanel")
            .BuildLayout(
                player.Recipients,
                Layout(server.queue.Count)
            );
    }

    public void HideQueuePanel(params dynamic[] players)
    {
        foreach (var player in players)
        {
            if (player == null)
                continue;

            UI.Panel("queuepanel")
                .DestroyLayout(player.Recipients);
        }
    }

    public void ShowQueuePanel(params dynamic[] players)
    {
        foreach (var player in players)
        {
            if (player == null)
                continue;

            UI.Panel("queuepanel")
                .BuildLayout(
                    player.Recipients,
                    Layout(server.queue.Count)
                );
        }
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

        string title = $"{match.Player1} vs {match.Player2}";
        string score = $"{match.Score1} - {match.Score2}";

        var layout = UI.Vertical()
            .WithStyle("horizontal-align", "center")
            .WithStyle("vertical-align", "top")
            .WithStyle("margin-top", "0px")
            .WithStyle("background-color", "rgba(17, 17, 17, 0.86)")
            .WithStyle("padding", "10px 10px")
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
                    .WithStyle("color", "#ff9beb")
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

    public void ShowWinner(
        DuelMatch match,
        dynamic player1,
        dynamic player2,
        string winnerText)
    {
        if (player1 != null)
        {
            UI.Panel("duelhud")
                .Set(player1.Recipients, "dueltitle", winnerText);
        }

        if (player2 != null)
        {
            UI.Panel("duelhud")
                .Set(player2.Recipients, "dueltitle", winnerText);
        }
    }

    public void HideMatchHud(params dynamic[] players)
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