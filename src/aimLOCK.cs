/*
Entry point and main server file

Call all the other files from here, handel some basic server stuff as well

*IMPORTANT* Since deadworks is silly, we have to expose all the api methods here to access them in other files!!!
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

public class aimLOCK : DeadworksPluginBase
{
    public override string Name => "aimLOCK";

    public readonly Dictionary<string, DuelMatch> ActiveMatches = new();
    public readonly List<QueueEntry> queue = new();

    public const int WIN_SCORE = 6;

    public readonly Vector3[] DuelLocations =
    {
        new Vector3(-5121f, -1867f, 351f)
    };

    public const float DUEL_LIFESTEAL = 100f;
    public const float DUEL_BONUS_HEALTH = 1000f;

    public const float DUEL_GLASS_CANNON_DAMAGE = 240f;
    public const float DUEL_GLASS_CANNON_HEALTH_LOSS = -1f;

    public const float DUEL_BULLET_DAMAGE = 25f;
    public const float DUEL_BULLET_VELOCITY = 100f;

    public readonly Random random = new();

    public bool botSpawned;

    // Managers
    public QueueManager Queue { get; private set; }
    public PlayerManager PlayerManager { get; private set; }
    public UIManager UIManager { get; private set; }

    public override void OnLoad(bool isReload)
    {
        Console.WriteLine("aimLOCKED and loaded.");

        Queue = new QueueManager(this);
        PlayerManager = new PlayerManager(this);
        UIManager = new UIManager(this);
    }

    public override void OnStartupServer()
    {
        ConVar.Find("citadel_player_spawn_time_max_respawn_time")?.SetFloat(-2f);
    }

    /* Timer, might be blocking, test with multiple player one day*/
    public void ScheduleOnce(Duration delay, Action callback)
    {
        Timer.Once(delay, callback);
    }

    /* Force Haze, Skip Team Selection */

    public override void OnClientFullConnect(ClientFullConnectEvent args)
    {
        PlayerManager.OnPlayerConnect(args);
    }

    /* Hero Swap Prevention */

    public override HookResult OnClientConCommand(ClientConCommandEvent e)
    {
        if (e.Command == "selecthero")
            return HookResult.Stop;

        return HookResult.Continue;
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

    /* Player Death / Score Tracking */

    [GameEventHandler("player_death")]
    public HookResult OnPlayerDeath(PlayerDeathEvent args)
    {
        return PlayerManager.OnPlayerDeath(args);
    }

    /* Queue Commands */

    [Command("queue", "join", Description = "Add player to queue.")]
    public void JoinQueue(Caller caller)
    {
        Queue.JoinQueue(caller);
    }

    [Command("leave", Description = "Remove player from queue.")]
    public void LeaveQueue(Caller caller)
    {
        Queue.LeaveQueue(caller);
    }
}

/* Match Data */

public sealed class DuelMatch
{
    public string Player1 = "";
    public string Player2 = "";

    public int Score1;
    public int Score2;

    public Vector3 ArenaPosition;
}

/* Queue Data */

public sealed record QueueEntry(string PlayerName);