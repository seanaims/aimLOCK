/*
Entry point and main server file

Call all the other files from here, handel some basic server stuff as well

*IMPORTANT* Since deadworks is silly, we have to expose all the api methods here to access them in other files!!!
*/
using System;
using System.Collections.Generic;
using System.Globalization;
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
    private bool _entityRemovalSweepRunning;
    private bool _rutgerForceFieldCastScheduled;
    private bool _rutgerForceFieldCastAttempted;

    public readonly Dictionary<string, DuelMatch> ActiveMatches = new();
    public readonly List<QueueEntry> RankedQueue = new();
    public readonly List<QueueEntry> UnrankedQueue = new();
    public readonly Dictionary<ulong, PlayerRecord> PlayerRecords = new();
    public readonly Dictionary<string, DuelMatch> PendingRespawns = new();

    public const int WIN_SCORE = 6;
    public const float DUEL_SPAWN_OFFSET = 65f;

    public readonly Vector3[] DuelLocations =
    {
        new Vector3(-5121f, -1867f, 351f),
        new Vector3(-2592f, 2580f, 10f),
        new Vector3(156f, 75f, -745f),
        new Vector3(1486f, 3697f, 468f),
        new Vector3(-1387f, 6881f, 599f)
    };

    public static readonly Vector3 MidtownSpawnPosition =
        new(-1202f, -10120f, 1323f);

    private static readonly Vector3 RutgerForceFieldPosition =
        new(-1238f, -3606f, -472f);

    public const float DUEL_BONUS_HEALTH = 1000f;

    public const float DUEL_GLASS_CANNON_DAMAGE = 500f;
    public const float DUEL_GLASS_CANNON_HEALTH_LOSS = -1f;
    public const float DUEL_BULLET_DAMAGE = 25f;
    public const float DUEL_BULLET_VELOCITY = 100f;

    public readonly Random random = new();

    // Managers
    public QueueManager Queue { get; private set; }
    public PlayerManager PlayerManager { get; private set; }
    public UIManager UIManager { get; private set; }
    public RankedEloManager RankedElo { get; private set; } = null!;
    public LeaderboardManager LeaderboardManager { get; private set; } = null!;
    public PingManager PingManager { get; private set; } = null!;
    public PlayerTagManager PlayerTagManager { get; private set; } = null!;
    public GuildManager GuildManager { get; private set; } = null!;

    public override void OnLoad(bool isReload)
    {
        Console.WriteLine("aimLOCKED and loaded.");

        Queue = new QueueManager(this);
        PlayerManager = new PlayerManager(this);
        PlayerManager.RemoveExistingBots();
        PlayerManager.ClearInjectedSoulsFromConnectedPlayers();
        UIManager = new UIManager(this);
        RankedElo = new RankedEloManager();
        PingManager = new PingManager(this);
        PlayerTagManager = new PlayerTagManager(this);
        GuildManager = new GuildManager();
        PlayerManager.LoadRecords();
        LeaderboardManager = new LeaderboardManager(this);
        UIManager.RefreshHud();
        Timer.Once(2.Seconds(), PlayerTagManager.Initialize);
    }

    public override void OnStartupServer()
    {
        DisableNpcSpawning("citadel_trooper_spawn_enabled");
        DisableNpcSpawning("citadel_npc_spawn_enabled");
        var maxRespawnTime = ConVar.Find(
            "citadel_player_spawn_time_max_respawn_time"
        );
        if (maxRespawnTime == null)
        {
            Console.WriteLine(
                "[aimLOCK] Could not find citadel_player_spawn_time_max_respawn_time; instant respawns were not enabled."
            );
        }
        else
        {
            maxRespawnTime.SetFloat(-2f);
            Console.WriteLine("[aimLOCK] Enabled instant player respawns.");
        }

        var duplicateHeroes = ConVar.Find("citadel_allow_duplicate_heroes");
        if (duplicateHeroes == null)
        {
            Console.WriteLine(
                "[aimLOCK] Could not find citadel_allow_duplicate_heroes; duplicate heroes were not enabled."
            );
        }
        else
        {
            duplicateHeroes.SetInt(1);
            Console.WriteLine("[aimLOCK] Enabled duplicate heroes.");
        }

        Timer.Once(2.Seconds(), PlayerManager.StartInitialSpawnRecoverySweep);
        Timer.Once(1.Seconds(), QueueExistingEntityRemovalSweep);
        Timer.Once(1.Seconds(), PlayerManager.CreateDuelArenaRadiusMarkers);
        Timer.Once(1.Seconds(), PlayerManager.RemovePracticeBotZoneBoundaries);
        Timer.Once(1.Seconds(), PlayerManager.CreatePracticeBotDoorwayLabel);
        Timer.Once(2.Seconds(), LeaderboardManager.Initialize);
        PingManager.Start();
    }

    public override void OnUnload()
    {
        PlayerManager.RemoveDuelArenaRadiusMarkers();
        PlayerManager.RemovePracticeBotZoneBoundaries();
        PlayerManager.RemovePracticeBotDoorwayLabel();
        base.OnUnload();
    }

    private void QueueExistingEntityRemovalSweep()
    {
        if (_entityRemovalSweepRunning)
            return;

        _entityRemovalSweepRunning = true;
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
        else
        {
            _entityRemovalSweepRunning = false;
            Timer.Once(10.Seconds(), QueueExistingEntityRemovalSweep);
        }
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
        else
        {
            _entityRemovalSweepRunning = false;
            Timer.Once(10.Seconds(), QueueExistingEntityRemovalSweep);
        }
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

        if (args.Controller is { } connectedPlayer)
        {
            try
            {
                GuildManager.UpdatePlayerName(connectedPlayer);
            }
            catch (IOException ex)
            {
                Console.WriteLine(
                    $"Failed to update guild member name for {connectedPlayer.PlayerName}: {ex}"
                );
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine(
                    $"Access denied updating guild member name for {connectedPlayer.PlayerName}: {ex}"
                );
            }
        }

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
        if (
            e.Command == "selecthero" &&
            e.Controller is CCitadelPlayerController controller &&
            controller.GetHeroPawn() is { IsValid: true }
            && !PlayerManager.IsInitialHeroSelectionPending(controller)
        )
            return HookResult.Stop;

        return HookResult.Continue;
    }

    public override void OnClientPutInServer(ClientPutInServerEvent args)
    {
        if (args.Controller is { } controller)
            PlayerManager.OnPlayerPutInServer(controller);
    }

    public override void OnClientDisconnect(ClientDisconnectedEvent args)
    {
        if (args.IsMapChange)
            return;

        if (args.Controller is not CCitadelPlayerController controller)
            return;

        PlayerManager.OnPlayerDisconnect(controller);
        controller.RemoveWithPawns();
    }

    public override HookResult OnTakeDamage(TakeDamageEvent args)
    {
        return PlayerManager.BlockPracticeBotDamage(args)
            ? HookResult.Stop
            : HookResult.Continue;
    }

    public override void OnProcessUsercmds(ProcessUsercmdsEvent args)
    {
        PlayerManager.EnforceDuelArenaBounds(args);
        PlayerManager.ProcessPracticeBotUsercmds(args);
        PlayerManager.ProcessPracticeBotDoorwayZone(args);
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
            caller.Reply("/pbmm <on|off> - Toggle ping-based matchmaking for your queue matches.");
            caller.Reply("Use /help 2 for stats and Elo, or /help 3 for spawn and UI settings.");
        }
        else if (page == 2)
        {
            caller.Reply("aimLOCK server commands (page 2/3):");
            caller.Reply("/stats [player] - View unranked wins, losses, and win rate.");
            caller.Reply("/elo - View ranked Elo, wins, losses, and win rate.");
            caller.Reply("/ping - View your server-reported ping.");
            caller.Reply("/resetelo <player name> - Reset Elo to 1000 and ranked wins/losses to 0-0 (permission required).");
            caller.Reply("/resetranked <player name> (alias: /resetrankedrecord) - Reset ranked wins/losses, keeping Elo (permission required).");
            caller.Reply("/resetunranked <player name> (alias: /resetunrankedrecord) - Reset unranked wins/losses (permission required).");
            caller.Reply("Use an exact name or unique name prefix. Use /help 1 for queueing or /help 3 for other commands.");
        }
        else if (page == 3)
        {
            caller.Reply("aimLOCK server commands (page 3/3):");
            caller.Reply("/bot [easy|medium|hard] - Start a bot match (default: hard).");
            caller.Reply("/botspeed <units/sec> - Set the practice bot's strafe speed (100-2500; default 2500).");
            caller.Reply("/arena [number] - List arenas or teleport to a configured arena.");
            caller.Reply("/spectate list | <player name> - List matches or spectate one until it ends.");
            caller.Reply("/tp <player name|arena number> - Teleport to a player or arena (permission required).");
            caller.Reply("/spawn - Return to Midtown spawn after a 2-second countdown; forfeits an active duel.");
            caller.Reply("/scoreboard <on|off> - Show or hide the server info UI.");
            caller.Reply("/timezone [IANA ID] - View or set your local time zone.");
            caller.Reply("/tag <playername> <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|off> - Set or remove an overhead tag (permission required).");
            caller.Reply("/tagoff - Hide your own overhead tag.");
            caller.Reply("/tagon - Show your overhead tag again.");
            caller.Reply("/guild help - Create and manage guilds, invitations, rosters, and access settings.");
            caller.Reply("/leaderboardmove <x> <y> <z> - Move the leaderboard (permission required).");
            caller.Reply("Use /help 1 for queueing or /help 2 for stats, Elo, and stat resets.");
        }
        else
        {
            caller.Reply("Help pages are 1-3. Use /help, /help 2, or /help 3.");
        }
    }

    [Command(
        "bot",
        Description = "Start a bot match: bot [easy|medium|hard] (default: hard).",
        SuppressChat = true
    )]
    public void BotCommand(Caller caller, string? difficulty = "hard")
    {
        PlayerManager.StartPracticeBotMatch(caller, difficulty);
    }

    [Command(
        "spectate",
        Description = "List active matches or spectate a player: spectate <list|player name>.",
        SuppressChat = true
    )]
    public void SpectateCommand(Caller caller, params string[] arguments)
    {
        PlayerManager.SpectateCommand(caller, arguments);
    }

    [Command(
        "botspeed",
        Description = "Set the practice bot's strafe speed: botspeed <100-2500 units/sec>.",
        SuppressChat = true
    )]
    public void BotSpeedCommand(Caller caller, float speed)
    {
        PlayerManager.SetPracticeBotStrafeSpeed(caller, speed);
    }

    [Command(
        "arena",
        Description = "List configured arenas or teleport to one: arena [number].",
        SuppressChat = true
    )]
    public void ArenaCommand(Caller caller, string? arenaNumber = null)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to use /arena.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arenaNumber))
        {
            for (int i = 0; i < DuelLocations.Length; i++)
            {
                Vector3 arena = DuelLocations[i];
                caller.Reply(
                    $"Arena {i + 1}: {arena.X:0}, {arena.Y:0}, {arena.Z:0}"
                );
            }
            caller.Reply("Use /arena <number> to teleport to an arena.");
            return;
        }

        if (!int.TryParse(arenaNumber, out int selectedArena) ||
            selectedArena < 1 ||
            selectedArena > DuelLocations.Length)
        {
            caller.Reply($"Choose an arena number from 1 to {DuelLocations.Length}.");
            return;
        }

        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true })
        {
            caller.Reply("Your hero is not available to teleport.");
            return;
        }

        Vector3 destination =
            PlayerManager.GetDuelArenaCenter(DuelLocations[selectedArena - 1]);
        pawn.Teleport(
            destination,
            null,
            null
        );
        PlayerManager.RegisterArenaBoundaryPosition(player, destination);
        caller.Reply($"Teleported to arena {selectedArena}.");
    }

    [Command(
        "tp",
        Description = "Teleport to an online player or configured arena: tp <player name|arena number>.",
        Permission = "aimlock.teleport",
        SuppressChat = true
    )]
    public void TeleportCommand(Caller caller, params string[] arguments)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to use /tp.");
            return;
        }

        if (arguments.Length == 0)
        {
            caller.Reply("Use /tp <player name> or /tp arena <number>.");
            return;
        }

        var sourcePawn = player.GetHeroPawn();
        if (sourcePawn is not { IsValid: true })
        {
            caller.Reply("Your hero is not available to teleport.");
            return;
        }

        if (string.Equals(arguments[0], "arena", StringComparison.OrdinalIgnoreCase))
        {
            if (
                arguments.Length != 2 ||
                !int.TryParse(arguments[1], out int selectedArena) ||
                selectedArena < 1 ||
                selectedArena > DuelLocations.Length
            )
            {
                caller.Reply($"Use /tp arena <number> (1-{DuelLocations.Length}).");
                return;
            }

            Vector3 destination =
                PlayerManager.GetDuelArenaCenter(DuelLocations[selectedArena - 1]);
            sourcePawn.Teleport(
                destination,
                null,
                null
            );
            PlayerManager.RegisterArenaBoundaryPosition(player, destination);
            caller.Reply($"Teleported to arena {selectedArena}.");
            return;
        }

        string targetName = string.Join(" ", arguments).Trim();
        var onlinePlayers = Players.GetAll()
            .Where(target =>
                !target.IsBot &&
                !string.Equals(
                    target.PlayerName,
                    player.PlayerName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        var exactMatches = onlinePlayers
            .Where(target => string.Equals(
                target.PlayerName,
                targetName,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
        var matches = exactMatches.Length > 0
            ? exactMatches
            : onlinePlayers
                .Where(target => target.PlayerName.StartsWith(
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                ))
                .ToArray();

        if (matches.Length == 0)
        {
            caller.Reply($"No online player matches \"{targetName}\".");
            return;
        }

        if (matches.Length > 1)
        {
            caller.Reply("That player name is ambiguous; use the full player name.");
            return;
        }

        var targetPawn = matches[0].GetHeroPawn();
        if (targetPawn is not { IsValid: true })
        {
            caller.Reply("That player's hero is not available.");
            return;
        }

        Vector3 playerDestination =
            targetPawn.Position + new Vector3(64f, 0f, 0f);
        sourcePawn.Teleport(
            playerDestination,
            null,
            null
        );
        PlayerManager.RegisterArenaBoundaryPosition(player, playerDestination);
        caller.Reply($"Teleported to {matches[0].PlayerName}.");
    }

    [Command(
        "guild",
        Description = "Create and manage player guilds: guild help.",
        SuppressChat = true
    )]
    public void GuildCommand(Caller caller, params string[] arguments)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("Guild commands are only available to players.");
            return;
        }

        try
        {
            string response = GuildManager.ExecuteCommand(player, arguments);
            if (
                arguments.Length > 0 &&
                arguments[0].ToLowerInvariant() is
                    "create" or "join" or "accept" or "leave" or "disband" or
                    "mode" or "access" or "newtag" or "rename" or "kick" or
                    "transfer"
            )
                LeaderboardManager.Refresh();

            caller.Reply(response);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to save guild data: {ex}");
            caller.Reply("Could not save the guild change.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied saving guild data: {ex}");
            caller.Reply("Could not save the guild change.");
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

    [Command(
        "pbmm",
        Description = "Enable or disable ping-based matchmaking: pbmm <on|off>.",
        SuppressChat = true
    )]
    public void PingBasedMatchmakingCommand(Caller caller, string setting)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to change this setting.");
            return;
        }

        bool enabled;
        if (string.Equals(setting, "on", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
        }
        else if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
        }
        else
        {
            caller.Reply("Choose a setting: /pbmm on or /pbmm off.");
            return;
        }

        try
        {
            PlayerManager.SetPingBasedMatchmakingEnabled(player, enabled);
            caller.Reply(
                enabled
                    ? "Ping-based matchmaking enabled."
                    : "Ping-based matchmaking disabled; you can be matched with any queued player."
            );
            Queue.TryStartMatches();
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to update ping-based matchmaking for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your ping-based matchmaking setting.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.WriteLine(
                $"Invalid Steam ID for ping-based matchmaking setting: {ex}"
            );
            caller.Reply("Your player account is not ready; try again shortly.");
        }
    }

    [Command("spawn", Description = "Return to Midtown spawn after a 2-second countdown or forfeit an active duel.")]
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
            var matchPawn = player.GetHeroPawn();
            var matchRespawnEffect = matchPawn is { IsValid: true }
                ? PlayerManager.BeginRespawnEffect(matchPawn)
                : null;

            caller.Reply("Returning to Midtown spawn in 2...");
            ScheduleOnce(1.Seconds(), () =>
                caller.Reply("Returning to Midtown spawn in 1..."));
            ScheduleOnce(2.Seconds(), () =>
            {
                if (!player.IsValid)
                {
                    PlayerManager.EndRespawnEffect(matchRespawnEffect);
                    return;
                }

                if (
                    PlayerManager.ForfeitMatch(
                        player.PlayerName,
                        matchRespawnEffect
                    )
                )
                {
                    UIManager.RestoreServerInfoAfterSpawn(player);
                    caller.Reply("Forfeit recorded as a loss. The match has ended.");
                }
                else
                {
                    PlayerManager.EndRespawnEffect(matchRespawnEffect);
                    caller.Reply("The match is already ending; /spawn was not used.");
                }
            });
            return;
        }

        var pawn = player.GetHeroPawn();
        if (pawn == null)
        {
            caller.Reply("Your hero is not available to teleport.");
            return;
        }

        var respawnEffect = PlayerManager.BeginRespawnEffect(pawn);
        caller.Reply("Returning to Midtown spawn in 2...");
        ScheduleOnce(1.Seconds(), () => caller.Reply("Returning to Midtown spawn in 1..."));
        ScheduleOnce(2.Seconds(), () =>
        {
            if (!player.IsValid || !pawn.IsValid)
            {
                PlayerManager.EndRespawnEffect(respawnEffect);
                return;
            }

            PlayerManager.StopSpectating(player);
            player.ChangeTeam(2);
            pawn.Teleport(MidtownSpawnPosition, null, null);
            ScheduleOnce(1.Seconds(), () =>
                PlayerManager.EndRespawnEffect(respawnEffect));
            UIManager.RestoreServerInfoAfterSpawn(player);
            caller.Reply("Returned to Midtown spawn.");
        });
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
        "resetelo",
        Description = "Reset ranked Elo and wins/losses by exact name or unique name prefix.",
        Permission = "aimlock.records.reset",
        SuppressChat = true
    )]
    public void ResetEloCommand(Caller caller, string playerName)
    {
        if (!TryResolvePlayerSteamId(
            playerName,
            out ulong steamId,
            out string targetName,
            out string error
        ))
        {
            caller.Reply(error);
            return;
        }

        try
        {
            RankedElo.ResetRating(
                steamId.ToString(CultureInfo.InvariantCulture),
                targetName
            );
            LeaderboardManager.Refresh();
            caller.Reply(
                $"Reset {targetName}'s ranked Elo to {RankedEloManager.InitialRating} and ranked wins/losses."
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to reset Elo for {targetName}: {ex}");
            caller.Reply("Could not save the Elo reset.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied resetting Elo for {targetName}: {ex}");
            caller.Reply("Could not save the Elo reset.");
        }
    }

    [Command(
        "resetrankedrecord",
        "resetranked",
        Description = "Reset ranked wins/losses by exact name or unique name prefix.",
        Permission = "aimlock.records.reset",
        SuppressChat = true
    )]
    public void ResetRankedRecordCommand(Caller caller, string playerName)
    {
        if (!TryResolvePlayerSteamId(
            playerName,
            out ulong steamId,
            out string targetName,
            out string error
        ))
        {
            caller.Reply(error);
            return;
        }

        try
        {
            RankedElo.ResetRecord(
                steamId.ToString(CultureInfo.InvariantCulture),
                targetName
            );
            LeaderboardManager.Refresh();
            caller.Reply(
                $"Reset {targetName}'s ranked wins/losses; Elo was preserved."
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to reset ranked record for {targetName}: {ex}");
            caller.Reply("Could not save the ranked record reset.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied resetting ranked record for {targetName}: {ex}"
            );
            caller.Reply("Could not save the ranked record reset.");
        }
    }

    [Command(
        "resetunrankedrecord",
        "resetunranked",
        Description = "Reset unranked wins/losses by exact name or unique name prefix.",
        Permission = "aimlock.records.reset",
        SuppressChat = true
    )]
    public void ResetUnrankedRecordCommand(Caller caller, string playerName)
    {
        if (!TryResolvePlayerSteamId(
            playerName,
            out ulong steamId,
            out string targetName,
            out string error
        ))
        {
            caller.Reply(error);
            return;
        }

        try
        {
            PlayerManager.ResetUnrankedRecord(steamId, targetName);
            LeaderboardManager.Refresh();
            caller.Reply($"Reset {targetName}'s unranked wins/losses.");
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to reset unranked record for {targetName}: {ex}"
            );
            caller.Reply("Could not save the unranked record reset.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied resetting unranked record for {targetName}: {ex}"
            );
            caller.Reply("Could not save the unranked record reset.");
        }
    }

    private bool TryResolvePlayerSteamId(
        string playerName,
        out ulong steamId,
        out string targetName,
        out string error)
    {
        steamId = 0;
        targetName = playerName?.Trim() ?? "";
        error = "";
        if (string.IsNullOrWhiteSpace(targetName))
        {
            error = "A player name is required.";
            return false;
        }

        string lookupName = targetName;
        var onlinePlayers = Players.GetAll()
            .Where(player => !player.IsBot)
            .ToArray();

        ulong[] matchingIds = FindMatchingIds(exactMatch: true);
        bool usedPrefixMatch = false;
        if (matchingIds.Length == 0)
        {
            matchingIds = FindMatchingIds(exactMatch: false);
            usedPrefixMatch = true;
        }

        if (matchingIds.Length == 1)
        {
            ulong resolvedSteamId = matchingIds[0];
            steamId = resolvedSteamId;
            targetName = onlinePlayers
                .FirstOrDefault(player => player.PlayerSteamId == resolvedSteamId)
                ?.PlayerName
                ?? (PlayerRecords.TryGetValue(resolvedSteamId, out var savedRecord)
                    ? savedRecord.LastKnownName
                    : lookupName);
            return true;
        }

        error = matchingIds.Length > 1
            ? usedPrefixMatch
                ? $"More than one player name starts with '{lookupName}'; use a more specific name."
                : $"More than one player is named '{lookupName}'."
            : $"No online player or saved record found for '{targetName}'.";
        return false;

        ulong[] FindMatchingIds(bool exactMatch)
        {
            bool Matches(string? name) =>
                !string.IsNullOrWhiteSpace(name) &&
                (exactMatch
                    ? string.Equals(name, lookupName, StringComparison.OrdinalIgnoreCase)
                    : name.StartsWith(lookupName, StringComparison.OrdinalIgnoreCase));

            IEnumerable<ulong> ids = onlinePlayers
                .Where(player => Matches(player.PlayerName))
                .Select(player => player.PlayerSteamId)
                .Concat(PlayerRecords.Values
                    .Where(record => Matches(record.LastKnownName))
                    .Select(record => record.SteamId));

            IEnumerable<string> rankedIds = exactMatch
                ? RankedElo.FindPlayerIdsByName(lookupName)
                : RankedElo.FindPlayerIdsStartingWith(lookupName);

            return ids
                .Concat(rankedIds.Select(id =>
                    ulong.TryParse(
                        id,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out ulong parsedId
                    ) ? parsedId : 0))
                .Where(id => id != 0 && id != ulong.MaxValue)
                .Distinct()
                .ToArray();
        }
    }

    [Command(
        "tag",
        Description = "Set or remove an overhead player tag: tag <playername> <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|off>.",
        Permission = "aimlock.tags.manage",
        SuppressChat = true
    )]
    public void PlayerTagCommand(Caller caller, string playerName, string tag)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            caller.Reply("A player name is required.");
            return;
        }

        string normalizedName = playerName.Trim();
        if (string.Equals(tag, "off", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                PlayerTagManager.RemoveTag(normalizedName);
                caller.Reply($"Removed the overhead tag for {normalizedName}.");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Failed to remove tag for {normalizedName}: {ex}");
                caller.Reply("Could not save the tag change.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Access denied removing tag for {normalizedName}: {ex}");
                caller.Reply("Could not save the tag change.");
            }

            return;
        }

        PlayerTag playerTag;
        if (string.Equals(tag, "YT", StringComparison.OrdinalIgnoreCase))
        {
            playerTag = PlayerTag.YouTube;
        }
        else if (string.Equals(tag, "TTV", StringComparison.OrdinalIgnoreCase))
        {
            playerTag = PlayerTag.Twitch;
        }
        else if (string.Equals(tag, "OWNER", StringComparison.OrdinalIgnoreCase))
        {
            playerTag = PlayerTag.Owner;
        }
        else if (string.Equals(tag, "STAFF", StringComparison.OrdinalIgnoreCase))
        {
            playerTag = PlayerTag.Staff;
        }
        else if (string.Equals(tag, "AIMLOCK", StringComparison.OrdinalIgnoreCase))
        {
            playerTag = PlayerTag.Aimlock;
        }
        else if (string.Equals(tag, "SUPPORTER", StringComparison.OrdinalIgnoreCase))
        {
            playerTag = PlayerTag.Supporter;
        }
        else
        {
            caller.Reply("Use /tag <playername> <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|off>.");
            return;
        }

        try
        {
            PlayerTagManager.SetTag(normalizedName, playerTag);
            string displayTag = playerTag == PlayerTag.Aimlock
                ? "aimlock.xyz"
                : playerTag == PlayerTag.Supporter
                    ? "[♥]"
                : $"[{tag.ToUpperInvariant()}]";
            caller.Reply($"Set the overhead tag for {normalizedName} to {displayTag}.");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to set tag for {normalizedName}: {ex}");
            caller.Reply("Could not save the tag change.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied setting tag for {normalizedName}: {ex}");
            caller.Reply("Could not save the tag change.");
        }
    }

    [Command(
        "tagoff",
        Description = "Turn off your own overhead tag.",
        SuppressChat = true
    )]
    public void PlayerTagOffCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to turn off your overhead tag.");
            return;
        }

        try
        {
            PlayerTagManager.SetTagVisibility(player, false);
            caller.Reply("Your overhead tag has been turned off.");
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to persist tag visibility for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your tag setting.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied saving tag visibility for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your tag setting.");
        }
    }

    [Command(
        "tagon",
        Description = "Show your overhead tag again.",
        SuppressChat = true
    )]
    public void PlayerTagOnCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to show your overhead tag.");
            return;
        }

        try
        {
            PlayerTagManager.SetTagVisibility(player, true);
            caller.Reply("Your overhead tag is on.");
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to persist tag visibility for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your tag setting.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied saving tag visibility for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your tag setting.");
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
    public int CountdownGeneration;
    public bool ArenaInitialized;
    public bool Finished;
    public int SharedMaxHealth;
    public string? ForfeitingPlayer;
    public MatchQueueType QueueType;
    public string Player1 = "";
    public string Player2 = "";
    public bool Player1HadBulletLifestealItem;
    public bool Player2HadBulletLifestealItem;
    public bool Player1HadBlitzBulletsItem;
    public bool Player2HadBlitzBulletsItem;
    public bool Player1HadInfiniteRoundsItem;
    public bool Player2HadInfiniteRoundsItem;
    public bool Player1HadCardioCalibratorItem;
    public bool Player2HadCardioCalibratorItem;
    public bool Player1HadBurstFireItem;
    public bool Player2HadBurstFireItem;
    public bool Player1HadHealthItem;
    public bool Player2HadHealthItem;
    public int Score1;
    public int Score2;
    public bool CombatActive;

    public Vector3 ArenaPosition;
}

public sealed class PlayerRecord
{
    public ulong SteamId { get; set; }
    public string LastKnownName { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    public bool PingBasedMatchmakingEnabled { get; set; } = true;

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