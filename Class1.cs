/*
(Nov. 10,  2026)
I deadass hate doing this but it's a new learning experience.  
Figured out how API references work and working on figuring out deployment to Deadworks...
Deployment figured out, retrieve file from aimLOCK\bin\Debug\net10.0 and drag it.
Queue implies a queue display to show if it's functioning system, so that will be next.
Queue feedback functions, now is queue display on the side.

SHELVED:
Ping Display UI
Ping Based-Matchmaking


POSSIBLE:
Map Voting for Duels/Coinflip, Letting user pick heads or tails and their favorite map, assigning it to their character and flipping a coin for it
in 1v1s.

NOT STARTED:
Win-Loss displayed once match begins.
Elo vs. Win/Loss System or Both
Display current time in info UI, add command to change timezones.
In server stat leaderboards, display top 10 players with most wins, and top 10 players with most losses.
Get rid of spawn timers for Bosses in Certain Arenas
Minion/Small Enemy Removal
Barriers around duel areas.
Winner text on screen for the winner, loser text on the loser's screen.

WORKING ON:

FUNCTIONING BUT NEEDS CHANGES/VISUAL UPGRADES:

FUNCTIONING AND CHANGED AS WANTED:
Spawning in as Haze, disabling any other hero.
Queue UI Display 
Matchmaking with Queue System + Teleportation to Duel Locations
Instant Respawns
Make one player or the other switch teams so damage can be done.
After match finishes, put players back on same team.
Proper Spell Selection for Bullet Velo and Move-speed, and such.
When put into a match, make scoreboard show the score of the duel, as well as "(Your Name) vs (Enemy Name)




*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;
using DeadworksManaged.Api.Sounds;
using DeadworksManaged.Api.UI;
using System.Numerics;
using System.Security.Cryptography.X509Certificates;
using System.Configuration.Assemblies;

namespace aimLOCK;

public class aimLOCK : DeadworksPluginBase
{
    public override string Name => "aimLOCK";

    private readonly Dictionary<string, DuelMatch> ActiveMatches = new();
    private const int WIN_SCORE = 6;

    public readonly List<QueueEntry> queue = new();
    private bool botSpawned;
    private readonly Random random = new();

    // List of Dueling Locations
    private readonly Vector3[] DuelLocations =
    {
        new Vector3(-5121f, -1867f, 351f)
    };

    // Modifier Values and Percentages
private const float DUEL_LIFESTEAL = 100f;
private const float DUEL_BONUS_HEALTH = 1000f;

// Glass Cannon
private const float DUEL_GLASS_CANNON_DAMAGE = 240f; // 85 * 8
private const float DUEL_GLASS_CANNON_HEALTH_LOSS = -1f; 


   // High Velo Rounds Values
private const float DUEL_BULLET_DAMAGE = 25f;
private const float DUEL_BULLET_VELOCITY = 100f;
    public override void OnLoad(bool isReload)
    {
        Console.WriteLine("aimLOCKED and loaded.");
    }

// Matchmaking System
private void TryStartMatch()
{
    Console.WriteLine($"Queue Count: {queue.Count}");

    if (queue.Count < 2)
        return;

    var player1 = Players.GetAll()
        .FirstOrDefault(p =>
            p.PlayerName == queue[0].PlayerName);

    var player2 = Players.GetAll()
        .FirstOrDefault(p =>
            p.PlayerName == queue[1].PlayerName);

    if (player1 == null || player2 == null)
    {
        Console.WriteLine("Player lookup failed.");
        return;
    }

    Console.WriteLine(
        $"Starting match: {player1.PlayerName} vs {player2.PlayerName}"
    );

    // Put players on opposite teams so they can damage each other.
    player1.ChangeTeam(2);
    player2.ChangeTeam(3);

    // Give the game a moment to process the team swap.
    Timer.Once(0.25.Seconds(), () =>
    {
        var pawn1 = player1.GetHeroPawn();
        var pawn2 = player2.GetHeroPawn();

        if (pawn1 == null)
        {
            Console.WriteLine("Player 1 pawn is null.");
            return;
        }

        if (pawn2 == null)
        {
            Console.WriteLine("Player 2 pawn is null.");
            return;
        }

        Vector3 arena =
            DuelLocations[random.Next(DuelLocations.Length)];

        Console.WriteLine(
            $"Teleporting {player1.PlayerName} and {player2.PlayerName}"
        );

        pawn1.Teleport(
            position: arena + new Vector3(-200f, 0f, 0f),
            angles: new Vector3(0f, 45f, 0f),
            velocity: null
        );

        pawn2.Teleport(
    position: arena + new Vector3(200f, 0f, 0f),
    angles: new Vector3(0f, 180f, 0f),
    velocity: null
);

// Apply lifesteal
pawn1.AddModifier(
    "upgrade_vampire/modifier_intrinsic_base",
    abilityValues: new()
    {
        ["BulletLifestealPercent"] = DUEL_LIFESTEAL
    }
);

pawn2.AddModifier(
    "upgrade_vampire/modifier_intrinsic_base",
    abilityValues: new()
    {
        ["BulletLifestealPercent"] = DUEL_LIFESTEAL
    }
);
// Extra Health
pawn1.AddModifier(
    "upgrade_health/modifier_intrinsic_base",
    abilityValues: new()
    {
        ["BonusHealth"] = DUEL_BONUS_HEALTH
    }
);

pawn2.AddModifier(
    "upgrade_health/modifier_intrinsic_base",
    abilityValues: new()
    {
        ["BonusHealth"] = DUEL_BONUS_HEALTH
    }
);
/* Increase Fire Rate (Don't really need but lol)
pawn1.AddModifier(
    "upgrade_rapid_rounds/modifier_intrinsic_base",
    abilityValues: new()
    {
        ["BonusFireRate"] = DUEL_FIRE_RATE
    }
);

pawn2.AddModifier(
    "upgrade_rapid_rounds/modifier_intrinsic_base",
    abilityValues: new()
    {
        ["BonusFireRate"] = DUEL_FIRE_RATE
    }
);*/

/* High-Velo Rounds for Better Accuracy
pawn1.AddModifier(
    "upgrade_high_velocity_mag/modifier_intrinsic_base",
    abilityValues: new()
    {
        ["BaseAttackDamagePercent"] = DUEL_BULLET_DAMAGE,
        ["BonusBulletSpeedPercent"] = DUEL_BULLET_VELOCITY
    }
);

pawn2.AddModifier(
    "upgrade_high_velocity_mag/modifier_intrinsic_base",
    abilityValues: new()
    {
        ["BaseAttackDamagePercent"] = DUEL_BULLET_DAMAGE,
        ["BonusBulletSpeedPercent"] = DUEL_BULLET_VELOCITY
    }
);
*/
// Glass Cannon DMG Boost
pawn1.AddModifier(
    "upgrade_glass_cannon/modifier_glass_cannon_intrinstic",
    new Dictionary<string, float>
    {
        ["MaxHealthLossPercent"] = DUEL_GLASS_CANNON_HEALTH_LOSS,
        ["BaseAttackDamagePercent"] = DUEL_GLASS_CANNON_DAMAGE
    }
);

pawn2.AddModifier(
    "upgrade_glass_cannon/modifier_glass_cannon_intrinstic",
    new Dictionary<string, float>
    {
        ["MaxHealthLossPercent"] = DUEL_GLASS_CANNON_HEALTH_LOSS,
        ["BaseAttackDamagePercent"] = DUEL_GLASS_CANNON_DAMAGE
    }
);

pawn1.AddItem("upgrade_infinite_rounds");
pawn2.AddItem("upgrade_infinite_rounds");

        var match = new DuelMatch
{
    Player1 = player1.PlayerName,
    Player2 = player2.PlayerName,
    ArenaPosition = arena,
    Score1 = 0,
    Score2 = 0
};

ActiveMatches[player1.PlayerName] = match;
ActiveMatches[player2.PlayerName] = match;

ShowMatchHud(match);

// Hide server info while in duel
HideQueuePanel(player1, player2);

Console.WriteLine(
    $"Match Found! {player1.PlayerName} vs {player2.PlayerName}"
);
    });

    queue.RemoveAt(1);
    queue.RemoveAt(0);

    RefreshHud();
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

    // Spawns bot on Launch (for testing purposes)
    if (!botSpawned)
    {
        botSpawned = true;

        var pawn = controller.GetHeroPawn();
        var spawn = pawn?.Position ?? new Vector3(0, 45f, 0);

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
            // Bots.SetAiEnabled(true);

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
                TryStartMatch();
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
public override void OnStartupServer()
{
    ConVar.Find("citadel_player_spawn_time_max_respawn_time")?.SetFloat(-2f);
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
    [GameEventHandler("player_death")]
    
    // Score Tracking
public HookResult OnPlayerDeath(PlayerDeathEvent args)
{
    var victim = args.UseridController;
    var killer = args.AttackerController;

    if (victim == null || killer == null)
        return HookResult.Continue;

    if (!ActiveMatches.TryGetValue(
        victim.PlayerName,
        out var match))
    {
        return HookResult.Continue;
    }

    if (killer.PlayerName == match.Player1)
        match.Score1++;

    if (killer.PlayerName == match.Player2)
        match.Score2++;

    UpdateMatchHud(match);

    if (
    match.Score1 >= WIN_SCORE ||
    match.Score2 >= WIN_SCORE
)
{
    UpdateMatchHud(match);

    Timer.Once(3.Seconds(), () =>
    {
        FinishMatch(match);
    });

    return HookResult.Continue;
}

    Timer.Once(2.Seconds(), () =>
    {
        RespawnIntoArena(match);
    });

    return HookResult.Continue;
}


    /* Queue Commands */
    [Command("queue", "join", Description = "Add player to queue.")]
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

        TryStartMatch();

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
// In-Match Score HUD
private void ShowMatchHud(DuelMatch match)
{
    var p1 = Players.GetAll()
        .FirstOrDefault(x => x.PlayerName == match.Player1);

    var p2 = Players.GetAll()
        .FirstOrDefault(x => x.PlayerName == match.Player2);

    if (p1 == null || p2 == null)
        return;

    string title =
        $"{match.Player1} vs {match.Player2}";

    string score =
        $"{match.Score1} - {match.Score2}";

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


                 UI.Label(
            "separator",
            "────────"
        )
        .WithStyle("font-size", "16px")
        .WithStyle("color", "#fefafc")
        .WithStyle("horizontal-align", "center"),

            UI.Label("duelscore", score)
                .WithStyle("font-size", "20px")
                .WithStyle("color", "#ff9beb")
                .WithStyle("horizontal-align", "center")
        );

    UI.Panel("duelhud")
        .BuildLayout(p1.Recipients, layout);

    UI.Panel("duelhud")
        .BuildLayout(p2.Recipients, layout);
}
// Updating Score HUD
private void UpdateMatchHud(DuelMatch match)
{
    string score =
        $"{match.Score1} - {match.Score2}";

    var p1 = Players.GetAll()
        .FirstOrDefault(x => x.PlayerName == match.Player1);

    var p2 = Players.GetAll()
        .FirstOrDefault(x => x.PlayerName == match.Player2);

    if (p1 != null)
    {
        UI.Panel("duelhud")
            .Set(
                p1.Recipients,
                "duelscore",
                score
            );
    }

    if (p2 != null)
    {
        UI.Panel("duelhud")
            .Set(
                p2.Recipients,
                "duelscore",
                score
            );
    }
}
// Come back to arena after death
private void RespawnIntoArena(DuelMatch match)
{
    var player1 = Players.GetAll()
        .FirstOrDefault(
            x => x.PlayerName == match.Player1);

    var player2 = Players.GetAll()
        .FirstOrDefault(
            x => x.PlayerName == match.Player2);

    var pawn1 = player1?.GetHeroPawn();
    var pawn2 = player2?.GetHeroPawn();

    if (pawn1 == null || pawn2 == null)
        return;

    pawn1.Teleport(
        match.ArenaPosition +
        new Vector3(-200f, 0f, 0f),
        new Vector3(0f, 45f, 0f),
        null
    );

    pawn2.Teleport(
        match.ArenaPosition +
        new Vector3(200f, 0f, 0f),
        new Vector3(0f, 180f, 0f),
        null
    );
}
// Match Finishing Process
private void FinishMatch(DuelMatch match)
{
    var winner =
        match.Score1 > match.Score2
        ? match.Player1
        : match.Player2;

    Console.WriteLine(
        $"Match Over! Winner: {winner}"
    );

    var player1 = Players.GetAll()
        .FirstOrDefault(
            x => x.PlayerName == match.Player1);

    var player2 = Players.GetAll()
        .FirstOrDefault(
            x => x.PlayerName == match.Player2);

    string winnerText =
    $"Winner: {winner}";

if (player1 != null)
{
    UI.Panel("duelhud")
        .Set(
            player1.Recipients,
            "dueltitle",
            winnerText
        );
}

if (player2 != null)
{
    UI.Panel("duelhud")
        .Set(
            player2.Recipients,
            "dueltitle",
            winnerText
        );
}
Vector3 spawn =
    new Vector3(-1202, -10120, 1323);

Timer.Once(2.Seconds(), () =>
{
    if (player1 != null)
    {
        UI.Panel("duelhud")
            .DestroyLayout(player1.Recipients);
    }

    if (player2 != null)
    {
        UI.Panel("duelhud")
            .DestroyLayout(player2.Recipients);
    }

    player1?.GetHeroPawn()?.Teleport(
        spawn,
        null,
        null
    );

    player2?.GetHeroPawn()?.Teleport(
        spawn,
        null,
        null
    );

    RefreshHud();
});

        player1?.GetHeroPawn()?.RemoveModifier(
    "upgrade_vampire/modifier_intrinsic_base"
);

player2?.GetHeroPawn()?.RemoveModifier(
    "upgrade_vampire/modifier_intrinsic_base"
);
    ActiveMatches.Remove(match.Player1);
    ActiveMatches.Remove(match.Player2);

// Put both players back on the default team
player1?.ChangeTeam(2);
player2?.ChangeTeam(2);

    // Bring back server info panel
Timer.Once(1.Seconds(), () =>
{
    RefreshHud();
});

}

private void HideQueuePanel(params dynamic[] players)
{
    foreach (var player in players)
    {
        if (player == null)
            continue;

        UI.Panel("queuepanel")
            .DestroyLayout(player.Recipients);
    }
}

private void ShowQueuePanel(params dynamic[] players)
{
    foreach (var player in players)
    {
        if (player == null)
            continue;

        UI.Panel("queuepanel")
            .BuildLayout(
                player.Recipients,
                Layout(queue.Count)
            );
    }
}
public sealed class DuelMatch
{
    public string Player1 = "";
    public string Player2 = "";

    public int Score1;
    public int Score2;

    public Vector3 ArenaPosition;
}

} // end aimLOCK class

public sealed record QueueEntry(string PlayerName);
