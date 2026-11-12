/*
Entry point and main server file

Call all the other files from here, handel some basic server stuff as well

*IMPORTANT* Since deadworks is silly, we have to expose all the api methods here to access them in other files!!!
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

public class aimLOCK : DeadworksPluginBase
{
    public override string Name => "aimLOCK";

    private static readonly HashSet<string> EntityDesignerNamesToRemove = new(StringComparer.Ordinal)
    {
        "npc_boss_tier3",
        "npc_boss_tier2",
        "npc_boss_tier1",
        "npc_barrack_boss",
        "npc_base_defense_sentry",
        "npc_trooper_boss",
        "npc_trooper"
    };

    private readonly Queue<uint> _pendingEntityRemovals = new();
    private bool _rutgerForceFieldCastScheduled;
    private bool _rutgerForceFieldCastAttempted;

    public readonly Dictionary<string, DuelMatch> ActiveMatches = new();
    public readonly List<QueueEntry> RankedQueue = new();
    public readonly List<QueueEntry> UnrankedQueue = new();
    public readonly Dictionary<ulong, PlayerRecord> PlayerRecords = new();
    public readonly Dictionary<string, DuelMatch> PendingRespawns = new();

    public const int WIN_SCORE = 6;

    public readonly Vector3[] DuelLocations =
    {
        new Vector3(-5121f, -1867f, 351f)
    };

    public static readonly Vector3 MidtownSpawnPosition =
        new(-1202f, -10120f, 1323f);

    private static readonly Vector3 RutgerForceFieldPosition =
        new(-1238f, -3606f, -472f);

    public const float DUEL_LIFESTEAL = 100f;
    public const float DUEL_BONUS_HEALTH = 1000f;

    public const float DUEL_GLASS_CANNON_DAMAGE = 240f;
    public const float DUEL_GLASS_CANNON_HEALTH_LOSS = -1f;

    public const float DUEL_BULLET_DAMAGE = 25f;
    public const float DUEL_BULLET_VELOCITY = 100f;

    public readonly Random random = new();

    public bool botSpawned;
    public MatchQueueType? BotQueuePreference;

    // Managers
    public QueueManager Queue { get; private set; }
    public PlayerManager PlayerManager { get; private set; }
    public UIManager UIManager { get; private set; }
    public RankedEloManager RankedElo { get; private set; } = null!;
    public LeaderboardManager LeaderboardManager { get; private set; } = null!;
    public PingManager PingManager { get; private set; } = null!;

    public override void OnLoad(bool isReload)
    {
        Console.WriteLine("aimLOCKED and loaded.");

        Queue = new QueueManager(this);
        PlayerManager = new PlayerManager(this);
        UIManager = new UIManager(this);
        RankedElo = new RankedEloManager();
        PingManager = new PingManager(this);
        PlayerManager.LoadRecords();
        LeaderboardManager = new LeaderboardManager(this);
        UIManager.RefreshHud();
    }

    public override void OnStartupServer()
    {
        DisableNpcSpawning("citadel_trooper_spawn_enabled");
        DisableNpcSpawning("citadel_npc_spawn_enabled");
        ConVar.Find("citadel_player_spawn_time_max_respawn_time")?.SetFloat(-2f);
        Timer.Once(1.Seconds(), QueueExistingEntityRemovalSweep);
        Timer.Once(2.Seconds(), LeaderboardManager.Initialize);
        PingManager.Start();
    }

    private void QueueExistingEntityRemovalSweep()
    {
        int queued = 0;
        foreach (var entity in Entities.All)
        {
            if (entity.IsValid && EntityDesignerNamesToRemove.Contains(entity.DesignerName))
            {
                _pendingEntityRemovals.Enqueue(entity.EntityHandle);
                queued++;
            }
        }

        Console.WriteLine($"[aimLOCK] Queued {queued} matching NPCs/objectives for staggered removal.");
        if (_pendingEntityRemovals.Count > 0)
            Timer.NextTick(RemoveNextQueuedEntity);
    }

    private void RemoveNextQueuedEntity()
    {
        if (!_pendingEntityRemovals.TryDequeue(out uint entityHandle))
            return;

        var entity = CBaseEntity.FromHandle(entityHandle);
        if (entity != null
            && entity.IsValid
            && EntityDesignerNamesToRemove.Contains(entity.DesignerName))
        {
            entity.Remove();
        }

        if (_pendingEntityRemovals.Count > 0)
            Timer.NextTick(RemoveNextQueuedEntity);
    }

    private static void DisableNpcSpawning(string conVarName)
    {
        var conVar = ConVar.Find(conVarName);
        if (conVar == null)
        {
            Console.WriteLine($"[aimLOCK] Could not find {conVarName}; NPC spawning was not disabled.");
            return;
        }

        conVar.SetInt(0);
        Console.WriteLine($"[aimLOCK] Set {conVarName} to 0.");
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

        if (_rutgerForceFieldCastScheduled
            || _rutgerForceFieldCastAttempted
            || args.Controller is not { } controller)
        {
            return;
        }

        _rutgerForceFieldCastScheduled = true;
        ScheduleOnce(2.Seconds(), () =>
            TryCastRutgerForceField(controller, 5));
    }

    private void TryCastRutgerForceField(
        CCitadelPlayerController controller,
        int retriesRemaining)
    {
        if (_rutgerForceFieldCastAttempted)
            return;

        if (!controller.IsValid)
        {
            _rutgerForceFieldCastScheduled = false;
            return;
        }

        var pawn = controller.GetHeroPawn();
        if (pawn == null || !pawn.IsValid)
        {
            if (retriesRemaining > 0)
            {
                ScheduleOnce(1.Seconds(), () =>
                    TryCastRutgerForceField(controller, retriesRemaining - 1));
            }
            else
            {
                Console.WriteLine(
                    "[aimLOCK] Could not cast Rutger's Force Field: " +
                    "the first player's hero pawn was unavailable."
                );
                _rutgerForceFieldCastScheduled = false;
            }

            return;
        }

        _rutgerForceFieldCastAttempted = true;
        _rutgerForceFieldCastScheduled = false;

        var ability = pawn.AddAbility(
            "rutger_force_field",
            (ushort)EAbilitySlot.None
        );
        if (ability == null || !ability.IsValid)
        {
            Console.WriteLine(
                "[aimLOCK] Could not add rutger_force_field to the first player's pawn."
            );
            return;
        }

        try
        {
            ability.SetField(
                "CAbility_Rutger_ForceField"u8,
                "m_vSpawnPos"u8,
                RutgerForceFieldPosition
            );

            int result = pawn.ExecuteAbility(ability);
            if (result != 0)
            {
                pawn.RemoveAbility("rutger_force_field");
                Console.WriteLine(
                    $"[aimLOCK] Rutger Force Field execution failed with result {result}."
                );
                return;
            }

            Console.WriteLine(
                "[aimLOCK] Submitted a Rutger Force Field cast at " +
                $"{RutgerForceFieldPosition} using {controller.PlayerName} as the caster."
            );
        }
        catch (Exception ex)
        {
            pawn.RemoveAbility("rutger_force_field");
            Console.WriteLine($"[aimLOCK] Rutger Force Field cast failed: {ex}");
        }
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
        return PlayerManager.OnPlayerRespawned(args);
    }

    /* Player Death / Score Tracking */

    [GameEventHandler("player_death")]
    public HookResult OnPlayerDeath(PlayerDeathEvent args)
    {
        return PlayerManager.OnPlayerDeath(args);
    }

    /* Queue Commands */

    [Command(
        "help",
        "commands",
        Description = "List aimLOCK server commands: help [page].",
        SuppressChat = true
    )]
    public void HelpCommand(Caller caller, int page = 1)
    {
        if (page == 1)
        {
            caller.Reply("aimLOCK server commands (page 1/3):");
            caller.Reply("/join <ranked|unranked> (alias: /queue) - Join a queue.");
            caller.Reply("/leave - Leave your current queue.");
            caller.Reply("Use /help 2 for stats and Elo, or /help 3 for spawn and UI settings.");
        }
        else if (page == 2)
        {
            caller.Reply("aimLOCK server commands (page 2/3):");
            caller.Reply("/stats [player] - View unranked wins, losses, and win rate.");
            caller.Reply("/elo - View ranked Elo, wins, losses, and win rate.");
            caller.Reply("/ping - View your server-reported ping.");
            caller.Reply("Use /help 1 for queueing or /help 3 for spawn and UI settings.");
        }
        else if (page == 3)
        {
            caller.Reply("aimLOCK server commands (page 3/3):");
            caller.Reply("/spawn - Return to Midtown spawn; forfeits an active duel.");
            caller.Reply("/scoreboard <on|off> - Show or hide the server info UI.");
            caller.Reply("/timezone [IANA ID] - View or set your local time zone.");
            caller.Reply("/leaderboardmove <x> <y> <z> - Move the leaderboard (permission required).");
            caller.Reply("Use /help 1 for queueing or /help 2 for stats and Elo.");
        }
        else
        {
            caller.Reply("Help pages are 1-3. Use /help, /help 2, or /help 3.");
        }
    }

    [Command("join", "queue", Description = "Join a queue: join <ranked|unranked>.")]
    public void JoinQueue(Caller caller, string queueType)
    {
        MatchQueueType parsedQueueType;
        if (string.Equals(queueType, "ranked", StringComparison.OrdinalIgnoreCase))
        {
            parsedQueueType = MatchQueueType.Ranked;
        }
        else if (string.Equals(queueType, "unranked", StringComparison.OrdinalIgnoreCase))
        {
            parsedQueueType = MatchQueueType.Unranked;
        }
        else
        {
            caller.Reply("Choose a queue: /join ranked or /join unranked.");
            return;
        }

        Queue.JoinQueue(caller, parsedQueueType);
    }

    [Command("leave", Description = "Remove player from queue.")]
    public void LeaveQueue(Caller caller)
    {
        Queue.LeaveQueue(caller);
    }

    [Command("spawn", Description = "Return to Midtown spawn or forfeit an active duel.")]
    public void SpawnCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to return to Midtown spawn.");
            return;
        }

        if (ActiveMatches.ContainsKey(player.PlayerName))
        {
            if (PlayerManager.ForfeitMatch(player.PlayerName))
                caller.Reply("Forfeit recorded as a loss. The match has ended.");
            else
                caller.Reply("The match is already ending; /spawn was not used.");
            return;
        }

        var pawn = player.GetHeroPawn();
        if (pawn == null)
        {
            caller.Reply("Your hero is not available to teleport.");
            return;
        }

        player.ChangeTeam(2);
        pawn.Teleport(MidtownSpawnPosition, null, null);
        caller.Reply("Returned to Midtown spawn.");
    }

    [Command(
        "scoreboard",
        Description = "Show or hide the server info UI: scoreboard <on|off>.",
        SuppressChat = true
    )]
    public void ScoreboardCommand(Caller caller, string state)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to change the server info UI.");
            return;
        }

        bool visible;
        if (string.Equals(state, "on", StringComparison.OrdinalIgnoreCase))
        {
            visible = true;
        }
        else if (string.Equals(state, "off", StringComparison.OrdinalIgnoreCase))
        {
            visible = false;
        }
        else
        {
            caller.Reply("Use /scoreboard on or /scoreboard off.");
            return;
        }

        UIManager.SetServerInfoVisibility(player, visible);
        caller.Reply(
            $"Server info UI turned {(visible ? "on" : "off")}."
        );
    }

    [Command("stats", Description = "View player stats.")]
    public void StatsCommand(Caller caller, string? targetName = null)
    {
        PlayerManager.StatsCommand(caller, targetName);
    }

    [Command(
        "elo",
        Description = "Check your ranked Elo rating.",
        SuppressChat = true
    )]
    public void EloCommand(Caller caller)
    {
        var player = caller.Player;

        if (player == null)
        {
            caller.Reply("You must be a player to check your Elo.");
            return;
        }

        RankedEloStats stats = RankedElo.GetStats(
            player.PlayerSteamId.ToString(),
            player.PlayerName
        );
        int rankedGames = stats.Wins + stats.Losses;
        float rankedWinRate = rankedGames > 0
            ? (float)stats.Wins / rankedGames * 100f
            : 0f;

        caller.Reply(
            $"[{player.PlayerName}] Ranked | " +
            $"Record: {stats.Wins}-{stats.Losses} | " +
            $"Elo: {stats.Rating} | WR: {rankedWinRate:F1}%"
        );
    }

    [Command(
        "ping",
        Description = "View your server-reported ping.",
        SuppressChat = true
    )]
    public void PingCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to check your ping.");
            return;
        }

        if (PingManager.TryGetPing(player, out int pingMs))
        {
            caller.Reply($"[PING] {pingMs} ms (server-reported).");
            return;
        }

        PingManager.Refresh();
        caller.Reply(
            "[PING] No fresh, unambiguous server reading is available yet. " +
            "Try /ping again shortly."
        );
    }

    [Command(
        "leaderboardmove",
        Description = "Move the server leaderboard: leaderboardmove <x> <y> <z>.",
        Permission = "aimlock.leaderboard.move",
        SuppressChat = true
    )]
    public void MoveLeaderboardCommand(Caller caller, float x, float y, float z)
    {
        var position = new Vector3(x, y, z);
        try
        {
            bool moved = LeaderboardManager.MoveTo(position);
            caller.Reply(moved
                ? "Leaderboard moved and position saved."
                : "Leaderboard position saved, but its text entity is unavailable. Check the server log.");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to move the leaderboard: {ex}");
            caller.Reply("Could not save the leaderboard position.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            caller.Reply(ex.Message);
        }
    }

    [Command(
        "timezone",
        Description = "Show or set your timezone: timezone <IANA ID>.",
        SuppressChat = true
    )]
    public void TimeZoneCommand(Caller caller, string? ianaTimeZoneId = null)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to check or set your timezone.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ianaTimeZoneId))
        {
            Chat.PrintToChat(
                player,
                $"[TIME] Timezone: {PlayerManager.GetTimeZoneId(player.PlayerSteamId)}. " +
                "Set it with /timezone <IANA ID>."
            );
            return;
        }

        try
        {
            PlayerManager.SetTimeZone(player, ianaTimeZoneId);
            UIManager.ShowQueuePanel(player);
            Chat.PrintToChat(player, $"[TIME] Timezone set to {ianaTimeZoneId}.");
        }
        catch (TimeZoneNotFoundException)
        {
            Chat.PrintToChat(
                player,
                $"[TIME] Unknown IANA timezone: {ianaTimeZoneId}."
            );
        }
        catch (InvalidTimeZoneException ex)
        {
            Console.WriteLine($"Invalid timezone data for {ianaTimeZoneId}: {ex}");
            Chat.PrintToChat(
                player,
                "[TIME] That timezone is unavailable on the server."
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to save timezone for {player.PlayerName}: {ex}");
            Chat.PrintToChat(player, "[TIME] Could not save your timezone setting.");
        }
    }

}

/* Match Data */

public sealed class DuelMatch
{
    public bool CountdownActive;
    public bool Finished;
    public string? ForfeitingPlayer;
    public MatchQueueType QueueType;
    public string Player1 = "";
    public string Player2 = "";

    public int Score1;
    public int Score2;

    public Vector3 ArenaPosition;
}

public sealed class PlayerRecord
{
    public ulong SteamId { get; set; }
    public string LastKnownName { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";

    public int Wins { get; set; }
    public int Losses { get; set; }
}

/* Queue Data */

public enum MatchQueueType
{
    Ranked,
    Unranked
}

public sealed record QueueEntry(string PlayerName)
{
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
}