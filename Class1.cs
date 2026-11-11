/*
(Nov. 10,  2026)
I deadass hate doing this but it's a new learning experience.  
Figured out how API references work and working on figuring out deployment to Deadworks...
Deployment figured out, retrieve file from aimLOCK\bin\Debug\net10.0 and drag it.
Queue implies a queue display to show if it's functioning system, so that will be next.
Queue feedback functions, now is queue display on the side.

POSSIBLE:
Map Voting for Duels/Coinflip, Letting user pick heads or tails and their favorite map, assigning it to their character and flipping a coin for it
in 1v1s.

WANT: 
Ping Display UI
Ping Based-Matchmaking
Win-Loss displayed once match begins.
Scoreboard off when in a match, but shows the ping of both players when match is entered
Text Leaderboards in the Lobby, display player stats.
Proper Spell Selection for Bullet Velo and Move-speed, and such.
Win/Loss System
Elo vs. Win/Loss System or Both
Display current time in info UI, add command to change timezones.
In server stat leaderboards, display top 10 players with most wins, and top 10 players with most losses.

FUNCTIONING BUT NEEDS CHANGES/VISUAL UPGRADES:

FUNCTIONING AND CHANGES AS WANTED:
Spawning in as Haze, disabling any other hero.
Queue UI Display 
*/

using DeadworksManaged.Api;
using DeadworksManaged.Api.Sounds;
using DeadworksManaged.Api.UI;
using System.Numerics;

namespace aimLOCK;

public class aimLOCK : DeadworksPluginBase
{
    public override string Name => "aimLOCK";

    public readonly List<QueueEntry> queue = new();
    private bool botSpawned;

    public override void OnLoad(bool isReload)
    {
        Console.WriteLine("aimLOCKED and loaded.");
    }

    /* Online + Queue Count UI */
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

        // Server Title
        UI.Label(
            "servertitle",
            "aimlock.gg"
        )
        .WithStyle("font-size", "26px")
        .WithStyle("font-weight", "bold")
        .WithStyle("color", "#ff9beb")
        .WithStyle("horizontal-align", "left"),

        // Separator Line
        UI.Label(
            "separator",
            "─────────────────"
        )
        .WithStyle("font-size", "16px")
        .WithStyle("color", "#fefafc")
        .WithStyle("horizontal-align", "center"),

        // Server Info
        UI.Label(
            "overallinfo",
            $"l> Players Online: {playerCount}\nl> In Queue: {queueCount}"
        )
        .WithStyle("font-size", "18px")
        .WithStyle("color", "#FFFFFFFF"),

          UI.Label(
                "date",
                currentDate
            )
            .WithStyle("font-size", "14px")
            .WithStyle("color", "#AAAAAA")
            .WithStyle("margin-top", "8px")
    );
}

    private void RefreshHud()
    {
        foreach (var controller in Players.GetAll())
        {
            UI.Panel("queuepanel")
                .BuildLayout(
                    controller.Recipients,
                    Layout(queue.Count)
                );
        }
    }

    /* Force Haze, Skip Team Selection*/
public override void OnClientFullConnect(ClientFullConnectEvent args)
{
    if (args.Controller is not { } controller)
        return;

    Console.WriteLine(
        $"Forcing Haze for {controller.PlayerName}"
    );

    controller.ChangeTeam(2);
    controller.SelectHero(Heroes.Haze);

    // Spawn queue bot once
    if (!botSpawned)
    {
        botSpawned = true;

        var pawn = controller.GetHeroPawn();
        var spawn = pawn?.Position ?? new Vector3(0, 0, 0);

        var bot = Bots.Add(
            Heroes.Haze,
            team: 2,
            spawn,
            BotDifficulty.Hard
        );

        if (bot == null)
        {
            Console.WriteLine("BOT FAILED TO SPAWN");
        }
        else
        {
            Bots.SetAiEnabled(true);

            string botName =
                bot.Controller?.PlayerName ?? "Bot";

            if (!queue.Any(x =>
                StringComparer.OrdinalIgnoreCase.Equals(
                    x.PlayerName,
                    botName)))
            {
                queue.Add(
                    new QueueEntry(botName)
                );

                RefreshHud();
            }

            Console.WriteLine(
                $"Bot {botName} added to queue."
            );
        }
    }

    UI.Panel("queuepanel")
        .BuildLayout(
            controller.Recipients,
            Layout(queue.Count)
        );
}


    /* Debug Hero Change */
    [GameEventHandler("player_hero_changed")]
    public HookResult OnPlayerHeroChanged(PlayerHeroChangedEvent args)
    {
        Console.WriteLine("player_hero_changed fired.");

        return HookResult.Continue;
    }

    /* Debug Respawn */
    [GameEventHandler("player_respawned")]
    public HookResult OnPlayerRespawned(PlayerRespawnedEvent args)
    {
        Console.WriteLine("player_respawned fired.");

        return HookResult.Continue;
    }

    /* Queue Commands */

    [Command("queue","join", Description = "Add player to queue.")]
    public void JoinQueue(Caller caller)
    {
        var controller = caller.Player;

        if (controller == null)
        {
            caller.Reply("You must be a player to join the queue.");
            return;
        }

        if (queue.Any(x =>
            StringComparer.OrdinalIgnoreCase.Equals(
                x.PlayerName,
                controller.PlayerName)))
        {
            caller.Reply("You are already in the queue.");
            return;
        }

        queue.Add(new QueueEntry(controller.PlayerName));

        RefreshHud();

        caller.Reply("You have been added to the queue.");
        Sounds.Play(
            "Damage.Send.Crit",
            RecipientFilter.Single(controller.Slot)
        );

        Console.WriteLine(
            $"{controller.PlayerName} has joined the queue."
        );
    }

    [Command("leave", Description = "Remove player from queue.")]
    public void LeaveQueue(Caller caller)
    {
        var controller = caller.Player;

        if (controller == null)
            return;

        var player = queue.FirstOrDefault(x =>
            StringComparer.OrdinalIgnoreCase.Equals(
                x.PlayerName,
                controller.PlayerName));

        if (player == null)
        {
            caller.Reply("You are not currently in the queue.");
            return;
        }

        queue.Remove(player);

        RefreshHud();

        caller.Reply("You have left the queue.");

                Console.WriteLine(
            $"{controller.PlayerName} has left the queue."
        );
    }
/* Hero Swap Prevention */
    public override HookResult OnClientConCommand(ClientConCommandEvent e)
    {
        if (e.Command == "selecthero")
            return HookResult.Stop;

        return HookResult.Continue;
    }
}

public sealed record QueueEntry(string PlayerName);