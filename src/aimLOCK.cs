/*
Entry point and main server file

Call all the other files from here, handel some basic server stuff as well

*IMPORTANT* Since deadworks is silly, we have to expose all the api methods here to access them in other files!!!
*/
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

[DeclarePermission(
    "aimlock.tags.manage",
    Description = "Grant tag access and assign overhead tags to players."
)]
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
    private static readonly string[] StartupForceKillCommands =
    [
        "citadel_force_kill walkers",
        "citadel_force_kill shrines",
        "citadel_force_kill guardians",
        "citadel_force_kill troopers",
        "citadel_force_kill haunts"
    ];
    private readonly HashSet<string> _missingNpcSpawnConVarsLogged =
        new(StringComparer.Ordinal);
    private readonly Queue<uint> _pendingEntityRemovals = new();
    private readonly HashSet<uint> _pendingEntityRemovalHandles = new();
    private bool _entityRemovalSweepRunning;
    private bool _initialEntityRemovalSweepStarted;
    private bool _entityCullingEnabled;
    private int _entityCullingGeneration;
    private bool _rutgerForceFieldCastScheduled;
    private bool _rutgerForceFieldCastAttempted;
    private int? _previousSvInfiniteAmmo;
    private bool _missingSvInfiniteAmmoLogged;

    public readonly Dictionary<string, DuelMatch> ActiveMatches = new();
    public readonly List<QueueEntry> RankedQueue = new();
    public readonly List<QueueEntry> UnrankedQueue = new();
    public readonly Dictionary<ulong, PlayerRecord> PlayerRecords = new();
    public readonly Dictionary<string, DuelMatch> PendingRespawns = new();

    public const int WIN_SCORE = 6;
    public const float DUEL_SPAWN_OFFSET = 65f;

    public readonly Vector3[] DuelLocations =
    {
        new Vector3(-3642f, 5979f, 508f),
        new Vector3(-6442f, -4817f, 476f),
        new Vector3(-5248f, 5063f, 516f),
        new Vector3(5767f, -5225f, 500f),
        new Vector3(-3661f, 163f, 89f),
        new Vector3(-32f, 88f, 346f),
        new Vector3(-6958f, 1895f, 333f),
        new Vector3(-8584f, 4053f, 684f),
        new Vector3(-2592f, 2580f, 10f),
        new Vector3(156f, 75f, -745f),
        new Vector3(1486f, 3697f, 468f),
        new Vector3(-1387f, 6881f, 599f)
    };
    public static readonly string[] DuelArenaNames =
    {
        "clock",
        "boxes",
        "stocks",
        "forest",
        "railroad",
        "bridge",
        "cola",
        "tunnel",
        "purple",
        "sewer",
        "front_door",
        "clock2",
    };

    public static readonly Vector3 MidtownSpawnPosition =
        new(-1202f, -10120f, 1323f);

    private static readonly Vector3 RutgerForceFieldPosition =
        new(-1238f, -3606f, -472f);

    public const float DUEL_GLASS_CANNON_DAMAGE = 300f;
    public const float DUEL_GLASS_CANNON_HEALTH_LOSS = 0f;
    public const float BASE_DUEL_BULLET_DAMAGE = 25f;
    public const float DUEL_BULLET_DAMAGE = 40f;
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

        JsonStore.ResetLocks();
        MigrateLegacyDataFiles();

        Queue = new QueueManager(this);
        PlayerManager = new PlayerManager(this);
        PlayerManager.RemoveExistingBots();
        PlayerManager.ClearInjectedSoulsFromConnectedPlayers();
        UIManager = new UIManager(this);
        RankedElo = new RankedEloManager();
        PingManager = new PingManager(this);
        PlayerTagManager = new PlayerTagManager(this);
        GuildManager = new GuildManager(RankedElo);
        PlayerManager.LoadRecords();
        LeaderboardManager = new LeaderboardManager(this);
        UIManager.RefreshHud();
        Timer.Once(2.Seconds(), PlayerTagManager.Initialize);
    }

    /* Move data files from older versions into the aimLOCK folder */

    private static void MigrateLegacyDataFiles()
    {
        string baseDirectory = AppContext.BaseDirectory;

        JsonStore.MigrateLegacyFile(
            PlayerManager.RecordsFileName,
            "aimlock_records.json",
            Path.Combine(baseDirectory, "aimlock_records.json")
        );
        JsonStore.MigrateLegacyFile(
            GuildManager.DataFileName,
            Path.Combine(baseDirectory, "aimlock_guilds.json")
        );
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.TagsFileName,
            Path.Combine(baseDirectory, "aimlock_player_tags.json")
        );
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.StableTagsFileName,
            Path.Combine(baseDirectory, "aimlock_player_tags_by_id.json")
        );
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.GrantedTagsFileName,
            Path.Combine(baseDirectory, "aimlock_player_tag_grants.json")
        );
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.VisibilityFileName,
            Path.Combine(baseDirectory, "aimlock_player_tag_visibility.json")
        );
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.StableVisibilityFileName,
            Path.Combine(baseDirectory, "aimlock_player_tag_visibility_by_id.json")
        );
    }

    public override void OnStartupServer()
    {
        int cullingGeneration = ++_entityCullingGeneration;
        _pendingEntityRemovals.Clear();
        _pendingEntityRemovalHandles.Clear();
        _entityRemovalSweepRunning = false;
        _initialEntityRemovalSweepStarted = false;
        _entityCullingEnabled = true;
        DisableServerPausing();
        DisableNpcSpawning("citadel_trooper_spawn_enabled");
        DisableNpcSpawning("citadel_npc_spawn_enabled");
        DisableHeroKillGoldRewards();
        Timer.Once(3.Seconds(), () => RunStartupForceKillCommand(0));

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
        Timer.Once(
            1.Seconds(),
            () => QueueExistingEntityRemovalSweep(cullingGeneration)
        );
        Timer.Once(1.Seconds(), PlayerManager.CreateDuelArenaRadiusMarkers);
        Timer.Once(1.Seconds(), PlayerManager.RemovePracticeBotZoneBoundaries);
        Timer.Once(1.Seconds(), PlayerManager.CreatePracticeBotDoorwayLabel);
        Timer.Once(2.Seconds(), LeaderboardManager.Initialize);
        PingManager.Start();
    }

    private void RunStartupForceKillCommand(int commandIndex)
    {
        if (commandIndex >= StartupForceKillCommands.Length)
            return;

        string command = StartupForceKillCommands[commandIndex];
        Console.WriteLine($"[aimLOCK] Running startup cleanup command: {command}");
        Server.ExecuteCommand(command, output =>
        {
            string result = string.IsNullOrWhiteSpace(output)
                ? "<no console output>"
                : output.Trim();
            Console.WriteLine(
                $"[aimLOCK] Startup cleanup result for {command}: {result}"
            );
        });

        if (commandIndex + 1 < StartupForceKillCommands.Length)
        {
            Timer.Once(
                0.5.Seconds(),
                () => RunStartupForceKillCommand(commandIndex + 1)
            );
        }
    }

    public override void OnUnload()
    {
        _entityCullingEnabled = false;
        _entityCullingGeneration++;
        _pendingEntityRemovals.Clear();
        _pendingEntityRemovalHandles.Clear();
        _entityRemovalSweepRunning = false;
        PlayerManager.RemoveDuelArenaRadiusMarkers();
        PlayerManager.RemovePracticeBotZoneBoundaries();
        PlayerManager.RemovePracticeBotDoorwayLabel();
        PlayerManager.RemoveArenaScoreboards();
        base.OnUnload();
    }

    public override void OnEntitySpawned(EntitySpawnedEvent args)
    {
        if (
            !_entityCullingEnabled ||
            args.Entity is not CAI_BaseNPC entity ||
            !ShouldRemoveEnemyNpc(entity)
        )
            return;

        QueueEntityRemoval(entity.EntityHandle, _entityCullingGeneration);
    }

    private void QueueExistingEntityRemovalSweep(int generation)
    {
        if (
            !_entityCullingEnabled ||
            generation != _entityCullingGeneration ||
            _initialEntityRemovalSweepStarted
        )
            return;

        _initialEntityRemovalSweepStarted = true;
        DisableNpcSpawning("citadel_trooper_spawn_enabled");
        DisableNpcSpawning("citadel_npc_spawn_enabled");

        int queued = 0;
        foreach (var entity in Entities.ByClass<CAI_BaseNPC>())
        {
            if (
                ShouldRemoveEnemyNpc(entity) &&
                _pendingEntityRemovalHandles.Add(entity.EntityHandle)
            )
            {
                _pendingEntityRemovals.Enqueue(entity.EntityHandle);
                queued++;
            }
        }

        if (queued > 0)
            Console.WriteLine($"[aimLOCK] Queued {queued} enemy NPCs/objectives for staggered removal.");
        if (_pendingEntityRemovals.Count > 0)
        {
            if (!_entityRemovalSweepRunning)
            {
                _entityRemovalSweepRunning = true;
                Timer.NextTick(() => RemoveNextQueuedEntity(generation));
            }
        }
    }

    private void QueueEntityRemoval(uint entityHandle, int generation)
    {
        if (
            !_entityCullingEnabled ||
            generation != _entityCullingGeneration ||
            !_pendingEntityRemovalHandles.Add(entityHandle)
        )
            return;

        _pendingEntityRemovals.Enqueue(entityHandle);
        if (_entityRemovalSweepRunning)
            return;

        _entityRemovalSweepRunning = true;
        Timer.NextTick(() => RemoveNextQueuedEntity(generation));
    }

    private static bool ShouldRemoveEnemyNpc(CBaseEntity entity)
    {
        if (
            !entity.IsValid ||
            entity is CPointWorldText ||
            entity is CCitadelPlayerBotNPCBrain
        )
            return false;

        if (EntityDesignerNamesToRemove.Contains(entity.DesignerName))
            return true;

        if (entity is CAI_CitadelNPC citadelNpc)
            return !citadelNpc.IsMinion && citadelNpc.Summoner == null;

        return entity is CAI_BaseNPC;
    }

    private void RemoveNextQueuedEntity(int generation)
    {
        if (
            !_entityCullingEnabled ||
            generation != _entityCullingGeneration
        )
            return;

        if (!_pendingEntityRemovals.TryDequeue(out uint entityHandle))
            return;

        _pendingEntityRemovalHandles.Remove(entityHandle);
        var entity = CBaseEntity.FromHandle(entityHandle);
        if (entity != null && ShouldRemoveEnemyNpc(entity))
            entity.Remove();

        if (_pendingEntityRemovals.Count > 0)
            Timer.NextTick(() => RemoveNextQueuedEntity(generation));
        else
            _entityRemovalSweepRunning = false;
    }

    private void DisableNpcSpawning(string conVarName)
    {
        var conVar = ConVar.Find(conVarName);
        if (conVar == null)
        {
            if (_missingNpcSpawnConVarsLogged.Add(conVarName))
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not find {conVarName}; NPC spawning was not disabled."
                );
            }
            return;
        }

        if (conVar.GetInt() == 0)
            return;

        conVar.SetInt(0);
        Console.WriteLine($"[aimLOCK] Set {conVarName} to 0.");
    }

    private void DisableServerPausing()
    {
        string[] conVarNames =
        [
            "sv_pausable",
            "citadel_allow_pausing",
            "citadel_allow_pause_in_match",
        ];

        foreach (string conVarName in conVarNames)
        {
            var conVar = ConVar.Find(conVarName);
            if (conVar == null)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not find {conVarName}; server pausing may remain enabled."
                );
                continue;
            }

            conVar.SetInt(0);
            int actualValue = conVar.GetInt();
            if (actualValue != 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not disable {conVarName}; it remained at {actualValue}."
                );
                continue;
            }

            Console.WriteLine($"[aimLOCK] Disabled pausing via {conVarName}.");
        }
    }

    private void DisableHeroKillGoldRewards()
    {
        string[] conVarNames =
        [
            "citadel_player_gold_reward_min",
            "citadel_player_gold_reward_max",
            "citadel_player_gold_reward_first_kill_bonus",
        ];

        foreach (string conVarName in conVarNames)
        {
            var conVar = ConVar.Find(conVarName);
            if (conVar == null)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not find {conVarName}; hero-kill gold rewards may remain enabled."
                );
                continue;
            }

            conVar.SetInt(0);
            int actualValue = conVar.GetInt();
            if (actualValue != 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not disable {conVarName}; it remained at {actualValue}."
                );
                continue;
            }

            Console.WriteLine($"[aimLOCK] Disabled {conVarName}.");
        }
    }

    public void EnableMatchInfiniteAmmo()
    {
        var conVar = ConVar.Find("sv_infinite_ammo");
        if (conVar == null)
        {
            if (!_missingSvInfiniteAmmoLogged)
            {
                _missingSvInfiniteAmmoLogged = true;
                Console.WriteLine(
                    "[aimLOCK] Could not find sv_infinite_ammo; infinite ammo was not enabled."
                );
            }
            return;
        }

        _previousSvInfiniteAmmo ??= conVar.GetInt();
        if (conVar.GetInt() != 1)
            conVar.SetInt(1);
    }

    public void RestoreMatchInfiniteAmmoIfIdle()
    {
        foreach (var match in ActiveMatches.Values)
        {
            if (!match.Finished)
                return;
        }

        if (_previousSvInfiniteAmmo is not int previousValue)
            return;

        var conVar = ConVar.Find("sv_infinite_ammo");
        if (conVar == null)
        {
            Console.WriteLine(
                "[aimLOCK] Could not find sv_infinite_ammo; its previous value could not be restored."
            );
            return;
        }

        conVar.SetInt(previousValue);
        _previousSvInfiniteAmmo = null;
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
            e.Controller is CCitadelPlayerController chatPlayer &&
            (
                string.Equals(e.Command, "say", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Command, "say_team", StringComparison.OrdinalIgnoreCase)
            ) &&
            Penalties.GetActive(
                PenaltyType.Gag,
                chatPlayer.PlayerSteamId
            ) is { } activeGag
        )
        {
            Chat.PrintToChat(chatPlayer, BuildMuteNotice(activeGag));
            return HookResult.Stop;
        }

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

        Queue.OnPlayerDisconnect(controller);
        PlayerManager.OnPlayerDisconnect(controller);
        controller.RemoveWithPawns();
    }

    public override HookResult OnTakeDamage(TakeDamageEvent args)
    {
        return PlayerManager.BlockPracticeBotDamage(args)
            ? HookResult.Stop
            : PlayerManager.ApplyDuelBulletDamage(args);
    }

    public override void OnProcessUsercmds(ProcessUsercmdsEvent args)
    {
        PlayerManager.ProcessUsercmds(args);
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
            caller.Reply("Use /help 2 for stats and Elo, or /help 3 for duels, arenas, and other commands.");
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
            caller.Reply("/duel <player name> [arena name] - Request a duel; omit the arena for a random map.");
            caller.Reply("/accept - Accept a pending duel request.");
            caller.Reply("/bot [easy|medium|hard] - Start a bot match (default: hard).");
            caller.Reply("/botspeed <units/sec> - Set the practice bot's strafe speed (100-2500; default 2500).");
            caller.Reply("/arena [number|name] - List arenas or teleport to one.");
            caller.Reply("/spectate (/spec) list | <player name> - List matches or spectate one until it ends.");
            caller.Reply("/tp <player name|arena number|name> - Teleport to a player or arena (permission required).");
            caller.Reply("Arena names: clock, boxes, stocks, forest, railroad, bridge.");
            caller.Reply("More arenas: cola, tunnel, purple, sewer, front_door, clock2.");
            caller.Reply("/spawn - Return to spawn after a 2-second countdown; forfeits an active duel.");
            caller.Reply("/scoreboard <on|off> - Show or hide the server info UI.");
            caller.Reply("/timezone [IANA ID] - View or set your local time zone.");
            caller.Reply("/tag <tagname> - Equip a tag an admin granted you; /tag list shows your available tags.");
            caller.Reply("/tag give|revoke <player|SteamID64> <tagname> - Grant or take away tag access (aimlock.tags.manage).");
            caller.Reply("/tagoff - Hide your own overhead tag.");
            caller.Reply("/tagon - Show your overhead tag again.");
            caller.Reply("Moderation uses the Deadworks Admin plugin: /kick, /ban, /unban, /gag (text chat), /mute (voice), /who (permission required).");
            caller.Reply("/fixationmax - Fully upgrade Fixation with a 999-stack cap for all online players (permission required).");
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
        "fixationmax",
        Description = "Unlock and fully upgrade Fixation with a 999-stack cap in Signature3 for all online players.",
        Permission = "aimlock.fixation.max",
        SuppressChat = true
    )]
    public void FixationMaxCommand(Caller caller)
    {
        int updatedPlayers = PlayerManager.MaxFixationForOnlinePlayers();
        caller.Reply(
            $"Fully upgraded Fixation with a 999-stack cap for {updatedPlayers} online player(s)."
        );
    }

    [Command(
        "duel",
        Description = "Request a duel: duel <player name> [arena name].",
        SuppressChat = true
    )]
    public void DuelCommand(Caller caller, params string[] arguments)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to start a duel.");
            return;
        }

        string targetName = string.Join(" ", arguments).Trim();
        int? arenaIndex = null;
        if (
            arguments.Length > 1 &&
            TryParseDuelArenaName(arguments[^1], out int requestedArena)
        )
        {
            bool exactPlayerNameExists = Players.GetAll().Any(target =>
                !target.IsBot &&
                string.Equals(
                    target.PlayerName,
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (!exactPlayerNameExists)
            {
                targetName = string.Join(
                    " ",
                    arguments.Take(arguments.Length - 1)
                ).Trim();
                arenaIndex = requestedArena;
            }
        }

        Queue.RequestDuelMatch(
            player,
            targetName,
            arenaIndex,
            message => caller.Reply(message)
        );
    }

    [Command(
        "accept",
        Description = "Accept a pending duel request.",
        SuppressChat = true
    )]
    public void AcceptDuelCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to accept a duel request.");
            return;
        }

        Queue.AcceptDuelRequest(player, message => caller.Reply(message));
    }

    [Command(
        "spectate",
        "spec",
        Description = "List active matches or spectate a player: spectate <list|player name>.",
        SuppressChat = true
    )]
    public void SpectateCommand(Caller caller, params string[] arguments)
    {
        PlayerManager.SpectateCommand(caller, arguments);
    }

    private bool TryParseDuelArenaSelection(
        string selection,
        out int arenaIndex
    )
    {
        if (
            int.TryParse(selection, out int arenaNumber) &&
            arenaNumber >= 1 &&
            arenaNumber <= DuelLocations.Length
        )
        {
            arenaIndex = arenaNumber - 1;
            return true;
        }

        arenaIndex = Array.FindIndex(
            DuelArenaNames,
            name => string.Equals(
                name,
                selection.Trim(),
                StringComparison.OrdinalIgnoreCase
            )
        );
        return arenaIndex >= 0 && arenaIndex < DuelLocations.Length;
    }

    private bool TryParseDuelArenaName(
        string selection,
        out int arenaIndex
    )
    {
        arenaIndex = Array.FindIndex(
            DuelArenaNames,
            name => string.Equals(
                name,
                selection.Trim(),
                StringComparison.OrdinalIgnoreCase
            )
        );
        return arenaIndex >= 0 && arenaIndex < DuelLocations.Length;
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
        Description = "List arenas or teleport to one: arena [number|name].",
        SuppressChat = true
    )]
    public void ArenaCommand(Caller caller, string? arenaSelection = null)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to use /arena.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arenaSelection))
        {
            for (int i = 0; i < DuelLocations.Length; i++)
            {
                Vector3 arena = DuelLocations[i];
                caller.Reply(
                    $"Arena {i + 1} ({DuelArenaNames[i]}): " +
                    $"{arena.X:0}, {arena.Y:0}, {arena.Z:0}"
                );
            }
            caller.Reply("Use /arena <number|name> to teleport to an arena.");
            return;
        }

        if (
            !TryParseDuelArenaSelection(arenaSelection, out int arenaIndex)
        )
        {
            caller.Reply(
                $"Choose an arena number from 1 to {DuelLocations.Length} or a listed arena name."
            );
            return;
        }

        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true })
        {
            caller.Reply("Your hero is not available to teleport.");
            return;
        }

        Vector3 destination =
            PlayerManager.GetDuelArenaCenter(DuelLocations[arenaIndex]);
        pawn.Teleport(
            destination,
            null,
            null
        );
        PlayerManager.RegisterArenaBoundaryPosition(player, destination);
        caller.Reply(
            $"Teleported to arena {arenaIndex + 1} ({DuelArenaNames[arenaIndex]})."
        );
    }

    [Command(
        "tp",
        Description = "Teleport to a player or arena: tp <player name|arena number|name>.",
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
            caller.Reply(
                "Use /tp <player name> or /tp arena <number|name>."
            );
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
                !TryParseDuelArenaSelection(arguments[1], out int arenaIndex)
            )
            {
                caller.Reply(
                    $"Use /tp arena <number|name> (1-{DuelLocations.Length})."
                );
                return;
            }

            Vector3 destination =
                PlayerManager.GetDuelArenaCenter(DuelLocations[arenaIndex]);
            sourcePawn.Teleport(
                destination,
                null,
                null
            );
            PlayerManager.RegisterArenaBoundaryPosition(player, destination);
            caller.Reply(
                $"Teleported to arena {arenaIndex + 1} ({DuelArenaNames[arenaIndex]})."
            );
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
            {
                LeaderboardManager.Refresh();
                PlayerManager.RefreshArenaScoreboards();
                PlayerTagManager.RefreshGuildTags();
            }

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

    [Command(
        "handicap",
        Description = "Toggle a 10 percentage-point reduction to your duel lifesteal: handicap [on|off].",
        SuppressChat = true
    )]
    public void HandicapCommand(Caller caller, string? setting = null)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to change this setting.");
            return;
        }

        bool enabled;
        if (string.IsNullOrWhiteSpace(setting))
        {
            enabled = !PlayerManager.IsHandicapEnabled(player);
        }
        else if (string.Equals(setting, "on", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
        }
        else if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
        }
        else
        {
            caller.Reply("Use /handicap, /handicap on, or /handicap off.");
            return;
        }

        try
        {
            PlayerManager.SetHandicapEnabled(player, enabled);
            caller.Reply(
                enabled
                    ? "Handicap enabled. Your duel bullet lifesteal is reduced from 50% to 40% until you turn it off."
                    : "Handicap disabled. Your duel bullet lifesteal is back to 50%."
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to update handicap for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your handicap setting.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.WriteLine($"Invalid Steam ID for handicap setting: {ex}");
            caller.Reply("Your player account is not ready; try again shortly.");
        }
    }

    [Command("spawn", Description = "Return to spawn after a 2-second countdown or forfeit an active duel.")]
    public void SpawnCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to return to spawn.");
            return;
        }

        if (ActiveMatches.TryGetValue(player.PlayerName, out var activeMatch))
        {
            bool isBotMatch =
                PlayerManager.FindMatchParticipant(activeMatch.Player1)?.IsBot == true ||
                PlayerManager.FindMatchParticipant(activeMatch.Player2)?.IsBot == true;

            caller.Reply("Returning to spawn in 2...");
            ScheduleOnce(1.Seconds(), () =>
                caller.Reply("Returning to spawn in 1..."));
            ScheduleOnce(2.Seconds(), () =>
            {
                if (!player.IsValid)
                    return;

                if (
                    PlayerManager.ForfeitMatch(player.PlayerName)
                )
                {
                    UIManager.RestoreServerInfoAfterSpawn(player);
                    if (!isBotMatch)
                        caller.Reply("Forfeit recorded as a loss. The match has ended.");
                }
                else
                {
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

        caller.Reply("Returning to spawn in 2...");
        ScheduleOnce(1.Seconds(), () => caller.Reply("Returning to spawn in 1..."));
        ScheduleOnce(2.Seconds(), () =>
        {
            if (!player.IsValid || !pawn.IsValid)
                return;

            PlayerManager.StopSpectating(player);
            player.ChangeTeam(2);
            pawn.Teleport(MidtownSpawnPosition, null, null);
            UIManager.RestoreServerInfoAfterSpawn(player);
            caller.Reply("Returned to spawn.");
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
        Description = "Equip an admin-granted overhead tag or manage access: tag <tagname|list|give|revoke>.",
        SuppressChat = true
    )]
    public void PlayerTagCommand(Caller caller, params string[] arguments)
    {
        if (arguments.Length == 0)
        {
            caller.Reply(
                "Use /tag <tagname> or /tag list. Admins: /tag give|revoke <player|SteamID64> <tagname>."
            );
            return;
        }

        bool isGive = string.Equals(arguments[0], "give", StringComparison.OrdinalIgnoreCase);
        bool isRevoke = string.Equals(arguments[0], "revoke", StringComparison.OrdinalIgnoreCase);
        if (isGive || isRevoke)
        {
            if (!caller.HasPermission("aimlock.tags.manage"))
            {
                caller.Reply("Only admins can change overhead tag access.");
                return;
            }

            string targetName = arguments.Length < 3
                ? ""
                : string.Join(" ", arguments.Skip(1).Take(arguments.Length - 2)).Trim();
            if (string.IsNullOrWhiteSpace(targetName))
            {
                caller.Reply($"Use /tag {arguments[0].ToLowerInvariant()} <player|SteamID64> <tagname>.");
                return;
            }

            if (!PlayerTagManager.TryParseTag(arguments[^1], out PlayerTag accessTag))
            {
                caller.Reply(
                    "Choose a tag: YT, TTV, OWNER, STAFF, AIMLOCK, SUPPORTER, or GUILDTAG."
                );
                return;
            }

            if (!TryResolveTagTarget(targetName, out ulong targetId, out string targetLabel, out string error))
            {
                caller.Reply(error);
                return;
            }

            string token = PlayerTagManager.GetTagToken(accessTag);
            if (isGive)
            {
                if (TryChangeTags(caller, $"grant a tag to {targetLabel}", () =>
                    PlayerTagManager.GrantTag(targetId, accessTag)))
                {
                    caller.Reply(
                        $"Granted {token} tag access to {targetLabel}. " +
                        $"They can equip it with /tag {token}."
                    );
                }

                return;
            }

            bool revoked = false;
            if (TryChangeTags(caller, $"revoke a tag from {targetLabel}", () =>
                revoked = PlayerTagManager.RevokeTag(targetId, accessTag)))
            {
                caller.Reply(
                    revoked
                        ? $"Revoked {token} tag access from {targetLabel}."
                        : $"{targetLabel} does not have {token} tag access."
                );
            }

            return;
        }

        if (
            arguments.Length == 1 &&
            string.Equals(arguments[0], "list", StringComparison.OrdinalIgnoreCase)
        )
        {
            var player = caller.Player;
            if (player == null)
            {
                caller.Reply("Tag access lists are only available to players.");
                return;
            }

            caller.Reply(PlayerTagManager.GetGrantedTagList(player.PlayerSteamId));
            return;
        }

        if (arguments.Length == 1)
        {
            var player = caller.Player;
            if (player == null)
            {
                caller.Reply("Only players can equip their own overhead tags.");
                return;
            }

            if (string.Equals(arguments[0], "off", StringComparison.OrdinalIgnoreCase))
            {
                if (TryChangeTags(caller, $"remove the tag for {player.PlayerName}", () =>
                    PlayerTagManager.RemoveTag(player.PlayerSteamId)))
                {
                    caller.Reply("Your overhead tag was removed.");
                }

                return;
            }

            if (!PlayerTagManager.TryParseTag(arguments[0], out PlayerTag ownTag))
            {
                caller.Reply(
                    "Use /tag <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|GUILDTAG>, /tag list, or /tagoff."
                );
                return;
            }

            if (!PlayerTagManager.CanUseTag(player.PlayerSteamId, ownTag))
            {
                caller.Reply(
                    $"An admin must grant you {PlayerTagManager.GetTagToken(ownTag)} tag access first."
                );
                return;
            }

            if (
                ownTag == PlayerTag.GuildTag &&
                GuildManager.GetMemberTag(player.PlayerSteamId) == null
            )
            {
                caller.Reply("You must be in a guild to use your guild tag.");
                return;
            }

            if (TryChangeTags(caller, $"set the tag for {player.PlayerName}", () =>
                PlayerTagManager.SetTag(player.PlayerSteamId, ownTag)))
            {
                caller.Reply(
                    $"Your overhead tag is now {PlayerTagManager.GetTagToken(ownTag)}."
                );
            }

            return;
        }

        if (!caller.HasPermission("aimlock.tags.manage"))
        {
            caller.Reply(
                "Only admins can assign tags to other players. Use /tag <tagname> for yourself."
            );
            return;
        }

        string lookup = string.Join(
            " ",
            arguments.Take(arguments.Length - 1)
        ).Trim();
        string tag = arguments[^1];
        if (string.IsNullOrWhiteSpace(lookup))
        {
            caller.Reply("A player name or SteamID64 is required.");
            return;
        }

        bool removing = string.Equals(tag, "off", StringComparison.OrdinalIgnoreCase);
        PlayerTag playerTag = default;
        if (!removing && !PlayerTagManager.TryParseTag(tag, out playerTag))
        {
            caller.Reply(
                "Use /tag <player|SteamID64> <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|GUILDTAG|off>."
            );
            return;
        }

        if (!TryResolveTagTarget(lookup, out ulong steamId, out string label, out string resolveError))
        {
            caller.Reply(resolveError);
            return;
        }

        if (removing)
        {
            if (TryChangeTags(caller, $"remove the tag for {label}", () =>
                PlayerTagManager.RemoveTag(steamId)))
            {
                caller.Reply($"Removed the overhead tag for {label}.");
            }

            return;
        }

        if (TryChangeTags(caller, $"set the tag for {label}", () =>
            PlayerTagManager.SetTag(steamId, playerTag)))
        {
            string displayTag = playerTag == PlayerTag.Aimlock
                ? "aimlock.xyz"
                : playerTag == PlayerTag.Supporter
                    ? "[♥]"
                    : playerTag == PlayerTag.GuildTag
                        ? "guild tag"
                        : $"[{PlayerTagManager.GetTagToken(playerTag)}]";
            caller.Reply($"Set the overhead tag for {label} to {displayTag}.");
        }
    }

    // Exactly one online player by full name, or a SteamID64 for anyone else.
    private bool TryResolveTagTarget(
        string lookup,
        out ulong steamId,
        out string label,
        out string error
    )
    {
        steamId = 0;
        label = lookup;
        error = "";

        CCitadelPlayerController[] matches = Players.GetAll()
            .Where(player =>
                player.IsValid &&
                !player.IsBot &&
                string.Equals(
                    player.PlayerName?.Trim(),
                    lookup,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        if (matches.Length == 1)
        {
            steamId = matches[0].PlayerSteamId;
            label = matches[0].PlayerName;
            return true;
        }

        if (matches.Length > 1)
        {
            error = $"More than one online player is named \"{lookup}\"; use their SteamID64.";
            return false;
        }

        if (
            lookup.Length == 17 &&
            ulong.TryParse(
                lookup,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong parsedId
            ) &&
            parsedId >= 76561197960265728UL
        )
        {
            steamId = parsedId;
            label = PlayerRecords.TryGetValue(parsedId, out var record) &&
                !string.IsNullOrWhiteSpace(record.LastKnownName)
                    ? $"{record.LastKnownName} ({parsedId})"
                    : lookup;
            return true;
        }

        error = $"No online player is named \"{lookup}\". For offline players, use their SteamID64.";
        return false;
    }

    private static bool TryChangeTags(Caller caller, string action, Action change)
    {
        try
        {
            change();
            return true;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to {action}: {ex}");
            caller.Reply("Could not save the tag change.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied trying to {action}: {ex}");
            caller.Reply("Could not save the tag change.");
        }

        return false;
    }

    private static string GetPenaltyTimeLeft(Penalty penalty)
    {
        string text = penalty.DescribeRemaining(DateTime.UtcNow);
        return text.StartsWith("for ", StringComparison.OrdinalIgnoreCase)
            ? text[4..]
            : text;
    }

    private static string BuildMuteNotice(Penalty penalty)
    {
        string reason = string.IsNullOrWhiteSpace(penalty.Reason)
            ? "No reason provided."
            : penalty.Reason;
        return
            $"You are muted. Reason: {reason}. " +
            $"Time left: {GetPenaltyTimeLeft(penalty)}.";
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
    public string? ForfeitingPlayer;
    public MatchQueueType QueueType;
    public string Player1 = "";
    public string Player2 = "";
    public bool Player1HadBulletLifestealItem;
    public bool Player2HadBulletLifestealItem;
    public bool Player1HadBlitzBulletsItem;
    public bool Player2HadBlitzBulletsItem;
    public bool Player1HadOpeningRoundsItem;
    public bool Player2HadOpeningRoundsItem;
    public bool Player1HadFleetfootItem;
    public bool Player2HadFleetfootItem;
    public bool Player1HadTitanicMagazineItem;
    public bool Player2HadTitanicMagazineItem;
    public bool Player1HadCardioCalibratorItem;
    public bool Player2HadCardioCalibratorItem;
    public bool Player1HadBurstFireItem;
    public bool Player2HadBurstFireItem;
    public bool Player1HadFortitudeItem;
    public bool Player2HadFortitudeItem;
    public bool Player1HadHealthItem;
    public bool Player2HadHealthItem;
    public bool Player1HadGlassCannonItem;
    public bool Player2HadGlassCannonItem;
    public int Score1;
    public int Score2;
    public bool CombatActive;

    public int ArenaIndex;
    public Vector3 ArenaPosition;
}

public sealed class PlayerRecord
{
    public ulong SteamId { get; set; }
    public string LastKnownName { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    public bool PingBasedMatchmakingEnabled { get; set; } = true;
    public bool HandicapEnabled { get; set; }

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