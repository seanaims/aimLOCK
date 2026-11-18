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

    private static readonly string[] StartupForceKillCommands =
    [
        "citadel_force_kill walkers",
        "citadel_force_kill shrines",
        "citadel_force_kill guardians",
        "citadel_force_kill troopers",
        "citadel_force_kill haunts"
    ];
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
    private const string WelcomeMessage =
        "[aimLOCK] Welcome! This server runs 1v1 duels. Join /join ranked to gain Elo or /join unranked for casual matches; use /leave to exit a queue. See /help 1 for queue commands.";

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

    // Server-side lifesteal (see PlayerManager.ApplyServerLifesteal): 100% by default,
    // 85% in duels and bot matches (/lifesteal), or a player's own /handicap value.
    // true = bullet damage only; false = all damage types except Fixation's.
    public const bool DUEL_LIFESTEAL_BULLETS_ONLY = true;

    // Duel bullet damage: bullets deal DUEL_BULLET_DAMAGE / BASE_DUEL_BULLET_DAMAGE
    // times their normal damage during a duel (see PlayerManager.ApplyDuelBulletDamage).
    public const float BASE_DUEL_BULLET_DAMAGE = 25f;
    public const float DUEL_BULLET_DAMAGE = 25f; // 25 = normal damage (the 40 change is off)

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
    public MapCleaner MapCleaner { get; private set; } = null!;
    public ShopZoneFixer ShopZoneFixer { get; private set; } = null!;
    public HazeHitboxFilter HazeHitboxFilter { get; } = new();

    // Content addons mounted server-side and sent to clients, e.g. an edited Haze model
    // with the right-side hitbox fixed. Put <name>.vpk in
    // game/citadel/deadworks_server_addons/vpks/ and add "<name>" here.
    private static readonly string[] ServerContentAddons = { };
    public override IReadOnlyList<string> ContentAddons => ServerContentAddons;

    /* Server Settings */

    /// <summary>
    /// The game resets its convars when the server hibernates (empty server), which
    /// undid instant respawns, duplicate heroes and the NPC/breakable spawn switches.
    /// Re-applied on startup and whenever a player joins; each one is only written
    /// when it differs, so this is cheap to call.
    /// </summary>
    private void ApplyServerConVars(bool logChanges)
    {
        // Stay awake when empty: waking from hibernation cost an 18 s server frame in
        // testing and resets every convar above.
        SetConVarInt("sv_hibernate_when_empty", 0, logChanges);
        MapCleaner.DisableSpawnConVars();
        SetConVarFloat("citadel_player_spawn_time_max_respawn_time", -2f, logChanges);
        SetConVarInt("citadel_allow_duplicate_heroes", 1, logChanges);
        // Ability and item cooldowns capped at almost nothing, as other 1v1 servers do:
        // Burst Fire is ready again on the next hit, so it never drops out. Only
        // Fixation and items exist here.
        SetConVarFloat("citadel_ability_cooldown_max", AbilityCooldownMax, logChanges);
        // Copied from other 1v1 servers: no reloading, no comeback or difficulty
        // gold, no slow-think limit, and bots never take over for players who leave.
        SetConVarInt("sv_infinite_ammo", 1, logChanges);
        SetConVarFloat("citadel_player_gold_comeback_multiplier", 0f, logChanges);
        SetConVarFloat("citadel_player_gold_difficulty_multiplier", 0f, logChanges);
        SetConVarFloat("think_limit", 0f, logChanges);
        SetConVarFloat("citadel_bot_takeover_time", 0f, logChanges);
    }

    private const float AbilityCooldownMax = 0.001f;

    private static void SetConVarInt(string name, int value, bool log)
    {
        var conVar = ConVar.Find(name);
        if (conVar == null)
        {
            if (log)
                Console.WriteLine($"[aimLOCK] Could not find {name}; skipped.");
            return;
        }

        if (conVar.GetInt() == value)
            return;

        conVar.SetInt(value);
        if (log)
            Console.WriteLine($"[aimLOCK] Set {name} to {value}.");
    }

    private static void SetConVarFloat(string name, float value, bool log)
    {
        var conVar = ConVar.Find(name);
        if (conVar == null)
        {
            if (log)
                Console.WriteLine($"[aimLOCK] Could not find {name}; skipped.");
            return;
        }

        if (conVar.GetFloat() == value)
            return;

        conVar.SetFloat(value);
        if (log)
            Console.WriteLine($"[aimLOCK] Set {name} to {value}.");
    }

    /* Precache */

    public override void OnPrecacheResources()
    {
        // Load these at map load instead of the first time they're used. Without this
        // the server loads the hero (on every player's connect hero swap) and the
        // teleport/respawn particles (on every match start, respawn and /spawn) from
        // disk mid-game, which is what spiked the server frame on load-in and teleports.
        Precache.AddHero(Heroes.Haze);
        if (HeroTypeExtensions.TryParse(PlayerManager.TemporaryConnectHeroName, out var temporaryHero))
            Precache.AddHero(temporaryHero);

        foreach (string particle in PlayerManager.PrecachedParticles)
            Precache.AddResource(particle);
        foreach (string model in PlayerManager.PrecachedModels)
            Precache.AddResource(model);
    }

    public override void OnLoad(bool isReload)
    {
        Console.WriteLine("aimLOCKED and loaded.");

        JsonStore.ResetLocks();
        MigrateLegacyDataFiles();

        MapCleaner = new MapCleaner(this);
        ShopZoneFixer = new ShopZoneFixer(this);
        Queue = new QueueManager(this);
        PlayerManager = new PlayerManager(this);
        PlayerManager.StartDuelLoop();
        PlayerManager.RemoveExistingBots();
        PlayerManager.ClearInjectedSoulsFromConnectedPlayers();
        UIManager = new UIManager(this);
        RankedElo = new RankedEloManager();
        PingManager = new PingManager(this);
        PlayerTagManager = new PlayerTagManager(this);
        GuildManager = new GuildManager(RankedElo, PlayerTagManager);
        PlayerManager.LoadRecords();
        LeaderboardManager = new LeaderboardManager(this);
        UIManager.RefreshHud();
        Timer.Once(2.Seconds(), PlayerTagManager.Initialize);

        // Hot reload: the map is already loaded, so clean it up now.
        if (isReload)
        {
            MapCleaner.DisableSpawnConVars();
            Timer.Once(1.Seconds(), MapCleaner.StartSweep);
            Timer.Once(2.Seconds(), ShopZoneFixer.Start);
        }
    }

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
        modifierSlowKinds.Clear();
        fixationAbilityHandles.Clear();
        PlayerManager?.ClearModifierTypeCache();
        DisableServerPausing();
        DisableHeroKillGoldRewards();
        Timer.Once(3.Seconds(), () => RunStartupForceKillCommand(0));

        ApplyServerConVars(logChanges: true);

        Timer.Once(2.Seconds(), PlayerManager.StartInitialSpawnRecoverySweep);
        Timer.Once(1.Seconds(), MapCleaner.StartSweep);
        Timer.Once(2.Seconds(), ShopZoneFixer.Start);
        Timer.Once(1.Seconds(), PlayerManager.CreateDuelArenaRadiusMarkers);
        Timer.Once(1.Seconds(), PlayerManager.CreateEntryZoneLabels);
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
        PlayerManager.RemoveDuelArenaRadiusMarkers();
        PlayerManager.RemoveEntryZoneLabels();
        PlayerManager.RemoveArenaScoreboards();
        BackgroundFileWriter.Flush(TimeSpan.FromSeconds(5));
        base.OnUnload();
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

    public void ScheduleNextTick(Action callback)
    {
        Timer.NextTick(callback);
    }

    /* Map Cleanup, Server-Side Lifesteal, Fixation */

    // Fixation's slow (applied to the target every 20 stacks) is turned off; the
    // spirit damage proc stays.
    private const string FixationSlowModifierName = "ability_stacking_damage/slow";
    private const string BurstFireBuffModifierName = "upgrade_burst_fire/modifier_burst_fire_buff";

    // Modifier VData is shared per modifier type, so the name check is done once per
    // type and cached by its native pointer. Reading the name allocated a new string on
    // every modifier added on the server (every hit, buff and item proc).
    // 0 = nothing to do, 1 = some slow, 2 = Fixation's slow, 3 = Burst Fire's buff.
    // Cleared on map start.
    private readonly Dictionary<nint, byte> modifierSlowKinds = new();

    public override HookResult OnAddModifier(AddModifierEvent args)
    {
        nint vdata = args.ModifierVData.Handle;
        if (!modifierSlowKinds.TryGetValue(vdata, out byte slowKind))
        {
            string name = args.ModifierVData.Name;
            slowKind = name == FixationSlowModifierName
                ? (byte)2
                : name == BurstFireBuffModifierName
                    ? (byte)3
                    : name.Contains("slow", StringComparison.Ordinal) ? (byte)1 : (byte)0;
            modifierSlowKinds[vdata] = slowKind;
        }

        if (slowKind == 0)
            return HookResult.Continue;

        if (slowKind == 3)
            return HookResult.Continue;

        if (slowKind == 2)
            return HookResult.Stop;

        // Some other slow: block it only if it comes from Fixation. Cached per ability
        // handle so the ability's name isn't read again on every slow it applies.
        uint abilityHandle = args.AbilityHandle;
        if (abilityHandle == 0xFFFFFFFF)
            return HookResult.Continue;

        return IsFixationAbility(abilityHandle) ? HookResult.Stop : HookResult.Continue;
    }

    /// <summary>Whether an ability handle is a Fixation ability. Cached per handle.</summary>
    public bool IsFixationAbility(uint abilityHandle)
    {
        if (fixationAbilityHandles.TryGetValue(abilityHandle, out bool isFixation))
            return isFixation;

        if (fixationAbilityHandles.Count > 512)
            fixationAbilityHandles.Clear();

        isFixation =
            CBaseEntity.FromHandle(abilityHandle)?.As<CCitadelBaseAbility>()?.AbilityName ==
            "ability_stacking_damage";
        fixationAbilityHandles[abilityHandle] = isFixation;
        return isFixation;
    }

    // Ability handle -> is it Fixation. Handles include a serial number, so a reused
    // entity slot gets a new handle and never reads a stale answer.
    private readonly Dictionary<uint, bool> fixationAbilityHandles = new();

    public override void OnEntitySpawned(EntitySpawnedEvent args)
    {
        MapCleaner.OnEntitySpawned(args.Entity);
    }

    public override void OnModifierEvent(ModifierEvent args)
    {
        // Fires for every modifier event on the server; bail out before touching anything else.
        if (args.Event == EModifierEvent.DamageTaken)
            PlayerManager.ApplyServerLifesteal(args);
        else if (args.Event == EModifierEvent.HealthTaken && ActiveMatches.Count != 0)
            PlayerManager.BlockOtherDuelHealing(args);
        else if (args.Event == EModifierEvent.GunFired)
            PlayerManager.OnGunFired(args);
    }

    public override void OnPawnHeroInitialized(CCitadelPlayerPawn pawn)
    {
        // Bots: force the current Haze model right here, synchronously, so the hero
        // setup can't leave the bot on the missing legacy model before the next
        // network update goes out.
        if (pawn.IsValid && pawn.HeroID == Heroes.Haze && pawn.Controller is not { IsBot: false })
        {
            pawn.SetModel(PlayerManager.HazeModel);
            PlayerManager.OnPracticeBotHeroInitialized(pawn);
        }

        // Abilities were just (re)populated (spawn, respawn, hero reset): re-apply the loadout
        // on the next tick, once the game has finished setting the pawn up.
        uint pawnHandle = pawn.EntityHandle;
        Timer.NextTick(() =>
        {
            var currentPawn = CBaseEntity.FromHandle<CCitadelPlayerPawn>(pawnHandle);
            // Skip the temporary hero used during the connect swap; only Haze gets
            // the loadout here (the other call sites cover Haze regardless).
            if (
                currentPawn is { IsValid: true } &&
                currentPawn.HeroID == Heroes.Haze &&
                currentPawn.Controller is { IsBot: false }
            )
                PlayerManager.ApplyHazeLoadout(currentPawn, force: true);
        });
    }

    [Command(
        "hitdebug",
        Description = "Toggle printing hit group and position for bullet hits on Haze.",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void HitDebugCommand(Caller caller)
    {
        if (caller.Player is not { } player)
        {
            caller.Reply("You must be a player to use /hitdebug.");
            return;
        }

        caller.Reply(HazeHitboxFilter.ToggleDebug(player)
            ? "Hit debug on: shoot a Haze to see hit groups."
            : "Hit debug off.");
    }

    [Command(
        "lsdebug",
        Description = "Toggle a once-a-second lifesteal report in chat (per-hit details in the server log).",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void LifestealDebugCommand(Caller caller)
    {
        if (caller.Player is not { } player)
        {
            caller.Reply("You must be a player to use /lsdebug.");
            return;
        }

        caller.Reply(PlayerManager.ToggleLifestealDebug(player)
            ? "Lifesteal debug on: a report prints every second while you deal or take damage."
            : "Lifesteal debug off.");
    }

    [Command(
        "hazehitgroup",
        Description = "Toggle ignoring bullet hits on a Haze hit group: hazehitgroup <id|clear>.",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void HazeHitGroupCommand(Caller caller, string? hitGroup = null)
    {
        if (string.Equals(hitGroup, "clear", StringComparison.OrdinalIgnoreCase))
        {
            HazeHitboxFilter.ClearBlockedHitGroups();
            caller.Reply("No Haze hit groups are blocked.");
            return;
        }

        if (!int.TryParse(hitGroup, out int id))
        {
            caller.Reply(
                "Blocked Haze hit groups: " +
                (HazeHitboxFilter.BlockedHitGroups.Count == 0
                    ? "none"
                    : string.Join(", ", HazeHitboxFilter.BlockedHitGroups)) +
                ". Use /hazehitgroup <id> or /hazehitgroup clear."
            );
            return;
        }

        caller.Reply(HazeHitboxFilter.ToggleBlockedHitGroup(id)
            ? $"Bullet hits on Haze hit group {id} are now ignored."
            : $"Haze hit group {id} is no longer ignored.");
    }

    [Command(
        "bottest",
        Description = "Debug: spawn a bare bot: bottest [hero] [lane] [ai] [fx] [rename] [arena] [playerfx].",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void BotTestCommand(Caller caller, params string[] arguments)
    {
        // Bare Deadworks bot: no match, rename, teleport, items or effects. Used to
        // find out whether spawning a bot by itself crashes clients.
        var pawn = caller.Player?.GetHeroPawn();
        if (pawn is not { IsValid: true })
        {
            caller.Reply("You need a living hero to use /bottest.");
            return;
        }

        string heroArgument = arguments.Length > 0 ? arguments[0].ToLowerInvariant() : "haze";
        string heroName = heroArgument.StartsWith("hero_", StringComparison.Ordinal)
            ? heroArgument
            : "hero_" + heroArgument;
        if (!HeroTypeExtensions.TryParse(heroName, out var hero))
        {
            caller.Reply($"Unknown hero \"{heroName}\". Try haze or inferno.");
            return;
        }

        bool assignLane = arguments.Any(a => a.Equals("lane", StringComparison.OrdinalIgnoreCase));
        bool keepAi = arguments.Any(a => a.Equals("ai", StringComparison.OrdinalIgnoreCase));
        if (!keepAi && Bots.AiEnabled)
            Bots.SetAiEnabled(false);

        bool Has(string option) =>
            arguments.Any(a => a.Equals(option, StringComparison.OrdinalIgnoreCase));

        // "arena": spawn where /bot puts the bot (an arena far from you) instead of
        // next to you.
        Vector3 spawnPosition = pawn.Position + new Vector3(300f, 0f, 0f);
        if (Has("arena"))
        {
            Vector3 arena = DuelLocations[random.Next(DuelLocations.Length)];
            spawnPosition = PlayerManager.GetDuelArenaCenter(arena) +
                new Vector3(DUEL_SPAWN_OFFSET, 0f, 0f);
        }

        // "playerfx": put the respawn effect on you, like /bot does at match start.
        if (Has("playerfx"))
            PlayerManager.CreateDebugEffect(pawn);

        var bot = Bots.Add(
            hero,
            3,
            spawnPosition,
            BotDifficulty.Medium,
            assignLane
        );
        if (bot is { IsValid: true })
        {
            if (arguments.Any(a => a.Equals("fx", StringComparison.OrdinalIgnoreCase)))
                PlayerManager.CreateDebugEffect(bot);
            if (
                arguments.Any(a => a.Equals("rename", StringComparison.OrdinalIgnoreCase)) &&
                bot.Controller is { } botController
            )
                botController.PlayerName = botController.PlayerName + " (Test)";
        }

        caller.Reply(bot == null
            ? "Bot spawn failed."
            : $"Spawned test bot {heroName} (lane {(assignLane ? "on" : "off")}, AI {(keepAi ? "on" : "off")}). Use /botclear to remove it.");
    }

    [Command(
        "botclear",
        Description = "Debug: kick every bot.",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void BotClearCommand(Caller caller)
    {
        PlayerManager.RemoveExistingBots();
        caller.Reply("All bots kicked.");
    }

    [Command(
        "fixdebug",
        Description = "List Fixation modifiers (stacks and limit) on you and your duel opponent.",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void FixationDebugCommand(Caller caller) => PlayerManager.FixationDebugCommand(caller);

    [Command(
        "dumpents",
        Description = "Write every entity designer name on the map to aimlock_entities.txt.",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void DumpEntitiesCommand(Caller caller)
    {
        try
        {
            caller.Reply($"Entity list written to {MapCleaner.DumpEntities()}.");
        }
        catch (IOException ex)
        {
            caller.Reply("Could not write the entity list.");
            Console.WriteLine($"[aimLOCK] /dumpents failed: {ex}");
        }
    }

    /* Force Haze, Skip Team Selection */

    public override void OnClientFullConnect(ClientFullConnectEvent args)
    {
        // The practice bot is spawned as Haze directly. It needs none of the player
        // connect work (hero swap, HUD panels, overhead tag, guild name update).
        if (args.Controller is { IsBot: true })
            return;

        ApplyServerConVars(logChanges: true);
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

            ScheduleOnce(2.Seconds(), () =>
            {
                if (connectedPlayer.IsValid)
                    Chat.PrintToChat(connectedPlayer, WelcomeMessage);
            });
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
        // A player is joining: clear out leftover bots before their game is sent
        // anything (a bot that already exists when a player joins crashes that player).
        if (!args.IsBot)
        {
            PlayerManager.RemoveOrphanPracticeBots();
            PlayerManager.RemoveUntrackedBots();
        }

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
        HazeHitboxFilter.RemoveDebugSubscriber(controller);
        controller.RemoveWithPawns();
    }

    public override HookResult OnTakeDamage(TakeDamageEvent args)
    {
        if (PlayerManager.BlockPracticeBotDamage(args) || HazeHitboxFilter.ShouldBlock(args))
            return HookResult.Stop;

        PlayerManager.ApplyDuelBulletDamage(args);
        return HookResult.Continue;
    }

    public override HookResult OnModifyCurrency(ModifyCurrencyEvent args)
    {
        if (args.CurrencyType == ECurrencyType.EGold)
            return PlayerManager.OnSoulsChanged(args);

        return HookResult.Continue;
    }

    public override void OnCheckTransmit(CheckTransmitEvent args)
    {
        PlayerManager.HideOtherPlayersBots(args);
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
            caller.Reply("This is a 1v1 duel server: ranked tracks Elo; unranked is casual.");
            caller.Reply("/join <ranked|unranked> (alias: /queue) - Join that queue.");
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
            caller.Reply("/spectate (/spec) list | <player name> - List active matches or spectate one until it ends; the Midtown wall shows arena, queue, players, and score.");
            caller.Reply("/tp <player name|arena number|name> - Teleport to a player or arena (permission required).");
            caller.Reply("Arena names: clock, boxes, stocks, forest, railroad, bridge.");
            caller.Reply("More arenas: cola, tunnel, purple, sewer, front_door, clock2.");
            caller.Reply("/spawn - Return to spawn after a 2-second countdown; forfeits an active duel.");
            caller.Reply("/scoreboard <on|off> - Show or hide the server info UI.");
            caller.Reply("/timezone [IANA ID] - View or set your local time zone.");
            caller.Reply("/tag <tagname|clear> - Equip or clear your tag; /tag list shows your available tags.");
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
        "botmirror",
        Description = "Toggle whether the practice bot copies the items you buy during a bot match (on by default).",
        SuppressChat = true
    )]
    public void BotMirrorCommand(Caller caller)
    {
        if (caller.Player is not { } player)
        {
            caller.Reply("You must be a player to use /botmirror.");
            return;
        }

        caller.Reply(PlayerManager.ToggleBotMirror(player)
            ? "Bot mirror on: the bot buys every item you buy in a bot match."
            : "Bot mirror off: the bot keeps its items when you buy.");
    }

    [Command(
        "ab",
        "allowbuy",
        Description = "Allow buying items during your duels: ab <on|off> (off by default). Both players need it on.",
        SuppressChat = true
    )]
    public void AllowBuyCommand(Caller caller, string? setting = null)
    {
        if (caller.Player is not { } player)
        {
            caller.Reply("You must be a player to use /ab.");
            return;
        }

        bool allow;
        if (string.Equals(setting, "on", StringComparison.OrdinalIgnoreCase))
            allow = true;
        else if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
            allow = false;
        else
        {
            caller.Reply(
                $"Use /ab on or /ab off. It is {(PlayerManager.AllowsDuelBuying(player) ? "on" : "off")} for you."
            );
            return;
        }

        PlayerManager.SetAllowDuelBuying(player, allow);
        caller.Reply(
            (allow
                ? "Buying in duels on. It applies when both players have it on."
                : "Buying in duels off.") +
            (PlayerManager.TryGetActiveMatch(player, out _) ? " This takes effect from your next duel." : "")
        );
    }

    [Command(
        "hidebots",
        Description = "Test: hidebots <off|hero|all> hides practice bots from players outside their match (off is the default).",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void HideBotsCommand(Caller caller, string? setting = null)
    {
        PlayerManager.BotHideMode mode;
        switch (setting?.Trim().ToLowerInvariant())
        {
            case "off": mode = PlayerManager.BotHideMode.Off; break;
            case "hero": mode = PlayerManager.BotHideMode.Hero; break;
            case "all": mode = PlayerManager.BotHideMode.All; break;
            default:
                caller.Reply(
                    $"Use /hidebots off, /hidebots hero or /hidebots all. It is {PlayerManager.PracticeBotHideMode.ToString().ToLowerInvariant()}."
                );
                return;
        }

        PlayerManager.SetPracticeBotHideMode(mode);
        caller.Reply($"Practice bot hiding: {setting!.Trim().ToLowerInvariant()}. Resets to off on server restart.");
    }

    [Command(
        "hidepassive",
        Description = "Crash test: hidepassive <on|off> keeps the Fixation passive off the clients (on, the default) or sends it (off).",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void HidePassiveCommand(Caller caller, string? setting = null)
    {
        bool hide;
        if (string.Equals(setting, "on", StringComparison.OrdinalIgnoreCase))
            hide = true;
        else if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
            hide = false;
        else
        {
            caller.Reply(
                $"Use /hidepassive on or /hidepassive off. It is {(PlayerManager.HideFixationPassive ? "on" : "off")}."
            );
            return;
        }

        int updated = PlayerManager.SetHideFixationPassive(hide);
        caller.Reply(
            (hide
                ? "Fixation passive hidden again (no HUD icon)"
                : "Fixation passive now sent to clients (the HUD icon comes back)") +
            $"; Fixation redone for {updated} hero(es). Resets on server restart."
        );
    }

    [Command(
        "duel",
        Description = "Request a duel: duel <player name> [arena name] [ls <0-100>]. In a duel: duel ls <0-100> to propose a lifesteal.",
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

        // "ls <number>" (or "lifesteal <number>") anywhere in the command sets the
        // duel's lifesteal; the rest is the player name and arena as before.
        float? lifesteal = null;
        var rest = new List<string>(arguments.Length);
        for (int i = 0; i < arguments.Length; i++)
        {
            bool isLifestealWord =
                string.Equals(arguments[i], "ls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arguments[i], "lifesteal", StringComparison.OrdinalIgnoreCase);
            if (isLifestealWord && i + 1 < arguments.Length)
            {
                if (!TryParsePercent(arguments[i + 1], out float percent))
                {
                    caller.Reply("The lifesteal must be a number from 0 to 100, for example /duel ls 100.");
                    return;
                }

                lifesteal = percent;
                i++;
                continue;
            }

            if (isLifestealWord)
            {
                caller.Reply("Add a number after ls, for example /duel ls 100.");
                return;
            }

            rest.Add(arguments[i]);
        }

        arguments = rest.ToArray();
        string targetName = string.Join(" ", arguments).Trim();

        // No player named: a lifesteal proposal for the duel the player is in.
        if (targetName.Length == 0 && lifesteal is { } proposal)
        {
            Queue.ProposeMatchLifesteal(player, proposal, message => caller.Reply(message));
            return;
        }

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
            message => caller.Reply(message),
            lifesteal
        );
    }

    [Command(
        "accept",
        Description = "Accept a pending duel request or a lifesteal proposal from your opponent.",
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

        if (!CanTeleportOutOfDuel(caller, player))
            return;

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

    private bool CanTeleportOutOfDuel(Caller caller, CCitadelPlayerController player)
    {
        if (PlayerManager.TryGetActiveMatch(player, out _))
        {
            caller.Reply("You cannot teleport during a match. Use /spawn to forfeit.");
            return false;
        }

        if (PlayerManager.IsSpectating(player))
        {
            caller.Reply("You cannot teleport while spectating. Use /spawn to stop.");
            return false;
        }

        return true;
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

        if (!CanTeleportOutOfDuel(caller, player))
            return;

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
        "handi",
        Description = "Set your own duel lifesteal percent: handicap <number> (handicap off to clear).",
        SuppressChat = true
    )]
    public void HandicapCommand(Caller caller, string? value = null)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to change this setting.");
            return;
        }

        float duelPercent = PlayerManager.DuelLifestealPercent;
        float? percent;
        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            percent = null;
        }
        else if (TryParsePercent(value, out float parsed) && parsed <= duelPercent)
        {
            percent = parsed;
        }
        else
        {
            float? current = PlayerManager.GetHandicapLifestealPercent(player);
            caller.Reply(
                $"Use /handicap <number from 0 to {duelPercent:0.#}>, for example /handi {duelPercent:0.#}, " +
                "or /handicap off. " +
                (current is { } currentPercent
                    ? $"Your duel lifesteal is set to {MathF.Min(currentPercent, duelPercent):0.#}%."
                    : $"You have no handicap; your duel lifesteal is {duelPercent:0.#}%.")
            );
            return;
        }

        try
        {
            PlayerManager.SetHandicapLifestealPercent(player, percent);
            caller.Reply(
                percent is { } saved
                    ? $"Handicap set. Your duel lifesteal is {saved:0.#}% until you change it."
                    : $"Handicap off. Your duel lifesteal is back to {duelPercent:0.#}%."
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

    [Command(
        "lifesteal",
        "ls",
        Description = "Set the lifesteal percent for duels and bot matches: lifesteal <number>.",
        Permission = "aimlock.lifesteal",
        SuppressChat = true
    )]
    public void LifestealCommand(Caller caller, string? value = null)
    {
        if (!TryParsePercent(value, out float percent))
        {
            caller.Reply(
                "Use /lifesteal <number from 0 to 100>, for example /ls 85. " +
                $"Duel and bot lifesteal is {PlayerManager.DuelLifestealPercent:0.#}%."
            );
            return;
        }

        PlayerManager.SetDuelLifestealPercent(percent);
        caller.Reply(
            $"Duel and bot lifesteal set to {percent:0.#}% until the server restarts. " +
            "Lifesteal outside duels stays at 100%."
        );
    }

    private static bool TryParsePercent(string? value, out float percent)
    {
        percent = 0f;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return float.TryParse(
                value.Trim().TrimEnd('%'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out percent
            ) &&
            percent >= 0f &&
            percent <= 100f;
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

        if (PlayerManager.TryGetActiveMatch(player, out var activeMatch))
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
                    PlayerManager.TryGetActiveMatch(player, out var currentMatch) &&
                    ReferenceEquals(currentMatch, activeMatch) &&
                    PlayerManager.ForfeitMatch(
                        PlayerManager.GetMatchParticipantName(player)
                    )
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

            if (PlayerManager.TryGetActiveMatch(player, out _))
            {
                caller.Reply("You joined a match; /spawn was cancelled.");
                return;
            }

            PlayerManager.StopSpectating(player);
            PlayerManager.EnsureTeam(player, 2);
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
        Description = "Equip or clear an admin-granted overhead tag or manage access: tag <tagname|clear|list|give|revoke>.",
        SuppressChat = true
    )]
    public void PlayerTagCommand(Caller caller, params string[] arguments)
    {
        if (arguments.Length == 0)
        {
            caller.Reply(
                "Use /tag <tagname|clear> or /tag list. Admins: /tag give|revoke <player name|SteamID64> <tagname>."
            );
            return;
        }

        if (string.Equals(arguments[0], "give", StringComparison.OrdinalIgnoreCase))
        {
            if (!caller.HasPermission("aimlock.tags.manage"))
            {
                caller.Reply("Only admins can grant overhead tag access.");
                return;
            }

            if (
                arguments.Length < 3 ||
                string.IsNullOrWhiteSpace(string.Join(" ", arguments.Skip(1).Take(arguments.Length - 2)))
            )
            {
                caller.Reply("Use /tag give <player name|SteamID64> <tagname>.");
                return;
            }

            string targetName = string.Join(
                " ",
                arguments.Skip(1).Take(arguments.Length - 2)
            ).Trim();
            if (!PlayerTagManager.TryParseTag(arguments[^1], out PlayerTag grantedTag))
            {
                caller.Reply(
                    "Choose a tag: YT, TTV, OWNER, STAFF, AIMLOCK, SUPPORTER, or GUILDTAG."
                );
                return;
            }

            if (
                !TryResolveTagTarget(
                    targetName,
                    out ulong targetSteamId,
                    out string error
                )
            )
            {
                caller.Reply(error);
                return;
            }

            try
            {
                PlayerTagManager.GrantTag(targetSteamId, grantedTag);
                caller.Reply(
                    $"Granted {PlayerTagManager.GetTagToken(grantedTag)} tag access to {targetName}. " +
                    $"They can equip it with /tag {PlayerTagManager.GetTagToken(grantedTag)}."
                );
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Failed to grant a tag to {targetName}: {ex}");
                caller.Reply("Could not save the tag grant.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Access denied granting a tag to {targetName}: {ex}");
                caller.Reply("Could not save the tag grant.");
            }

            return;
        }

        if (string.Equals(arguments[0], "revoke", StringComparison.OrdinalIgnoreCase))
        {
            if (!caller.HasPermission("aimlock.tags.manage"))
            {
                caller.Reply("Only admins can revoke overhead tag access.");
                return;
            }

            if (
                arguments.Length < 3 ||
                string.IsNullOrWhiteSpace(string.Join(" ", arguments.Skip(1).Take(arguments.Length - 2)))
            )
            {
                caller.Reply("Use /tag revoke <player name|SteamID64> <tagname>.");
                return;
            }

            string targetName = string.Join(
                " ",
                arguments.Skip(1).Take(arguments.Length - 2)
            ).Trim();
            if (!PlayerTagManager.TryParseTag(arguments[^1], out PlayerTag revokedTag))
            {
                caller.Reply(
                    "Choose a tag: YT, TTV, OWNER, STAFF, AIMLOCK, SUPPORTER, or GUILDTAG."
                );
                return;
            }

            if (
                !TryResolveTagTarget(
                    targetName,
                    out ulong targetSteamId,
                    out string error
                )
            )
            {
                caller.Reply(error);
                return;
            }

            try
            {
                if (!PlayerTagManager.RevokeTag(targetSteamId, revokedTag))
                {
                    caller.Reply(
                        $"{targetName} does not have {PlayerTagManager.GetTagToken(revokedTag)} tag access."
                    );
                    return;
                }

                caller.Reply(
                    $"Revoked {PlayerTagManager.GetTagToken(revokedTag)} tag access from {targetName}."
                );
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Failed to revoke a tag from {targetName}: {ex}");
                caller.Reply("Could not save the tag change.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Access denied revoking a tag from {targetName}: {ex}");
                caller.Reply("Could not save the tag change.");
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

            if (string.Equals(arguments[0], "clear", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    PlayerTagManager.RemoveTag(player.PlayerSteamId);
                    caller.Reply("Your overhead tag was removed.");
                }
                catch (IOException ex)
                {
                    Console.WriteLine(
                        $"Failed to remove tag for {player.PlayerName}: {ex}"
                    );
                    caller.Reply("Could not save the tag change.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine(
                        $"Access denied removing tag for {player.PlayerName}: {ex}"
                    );
                    caller.Reply("Could not save the tag change.");
                }

                return;
            }

            if (!PlayerTagManager.TryParseTag(arguments[0], out PlayerTag ownTag))
            {
                caller.Reply(
                    "Use /tag <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|GUILDTAG>, /tag clear, or /tagoff."
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

            try
            {
                PlayerTagManager.SetTag(player.PlayerSteamId, ownTag);
                caller.Reply(
                    $"Your overhead tag is now {PlayerTagManager.GetTagToken(ownTag)}."
                );
            }
            catch (IOException ex)
            {
                Console.WriteLine(
                    $"Failed to set tag for {player.PlayerName}: {ex}"
                );
                caller.Reply("Could not save the tag change.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine(
                    $"Access denied setting tag for {player.PlayerName}: {ex}"
                );
                caller.Reply("Could not save the tag change.");
            }

            return;
        }

        if (!caller.HasPermission("aimlock.tags.manage"))
        {
            caller.Reply(
                "Only admins can assign tags to other players. Use /tag <tagname|clear> for yourself."
            );
            return;
        }

        string normalizedName = string.Join(
            " ",
            arguments.Take(arguments.Length - 1)
        ).Trim();
        string tag = arguments[^1];
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            caller.Reply("A player name is required.");
            return;
        }

        if (
            !TryResolveTagTarget(
                normalizedName,
                out ulong steamId,
                out string resolveError
            )
        )
        {
            caller.Reply(resolveError);
            return;
        }

        if (string.Equals(tag, "clear", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                PlayerTagManager.RemoveTag(steamId);
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

        if (!PlayerTagManager.TryParseTag(tag, out PlayerTag playerTag))
        {
            caller.Reply(
                "Use /tag <player name|SteamID64> <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|GUILDTAG|clear>."
            );
            return;
        }

        try
        {
            PlayerTagManager.SetTag(steamId, playerTag);
            string displayTag = playerTag == PlayerTag.Aimlock
                ? "aimlock.xyz"
                : playerTag == PlayerTag.Supporter
                    ? "[♥]"
                    : playerTag == PlayerTag.GuildTag
                        ? "guild tag"
                        : $"[{PlayerTagManager.GetTagToken(playerTag)}]";
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

    private bool TryResolveTagTarget(
        string playerName,
        out ulong steamId,
        out string error
    )
    {
        steamId = 0;
        error = "";

        CCitadelPlayerController[] matches = Players.GetAll()
            .Where(player =>
                player.IsValid &&
                !player.IsBot &&
                string.Equals(
                    player.PlayerName?.Trim(),
                    playerName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        if (matches.Length == 1)
        {
            steamId = matches[0].PlayerSteamId;
            return true;
        }

        if (matches.Length > 1)
        {
            error = $"More than one online player is named {playerName}; use their SteamID64.";
            return false;
        }

        if (
            playerName.Length == 17 &&
            ulong.TryParse(
                playerName,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out steamId
            )
        )
        {
            return true;
        }

        steamId = 0;
        error = $"No online player is named {playerName}. Use their SteamID64 if they are offline.";
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
    public bool Player1HadHighVelocityRoundsItem;
    public bool Player2HadHighVelocityRoundsItem;
    public bool Player1HadFleetfootItem;
    public bool Player2HadFleetfootItem;
    public bool Player1HadTitanicMagazineItem;
    public bool Player2HadTitanicMagazineItem;
    public bool Player1HadCardioCalibratorItem;
    public bool Player2HadCardioCalibratorItem;
    public bool Player1HadBurstFireItem;
    public bool Player2HadBurstFireItem;
    public bool Player1HadHealthItem;
    public bool Player2HadHealthItem;
    public int Score1;
    public int Score2;
    public bool CombatActive;

    public int ArenaIndex;
    // Lifesteal both players agreed on for this duel (/duel ... ls N); null = the
    // server's duel lifesteal.
    public float? LifestealPercent;
    // Whether players may buy items during this duel (both had /ab on when it started).
    // Bot matches always allow it.
    public bool BuyingAllowed;
    public Vector3 ArenaPosition;
}

public sealed class PlayerRecord
{
    public ulong SteamId { get; set; }
    public string LastKnownName { get; set; } = "";
    public string TimeZoneId { get; set; } = "UTC";
    public bool PingBasedMatchmakingEnabled { get; set; } = true;
    public bool HandicapEnabled { get; set; }
    // The player's own duel lifesteal percent from /handicap. Null with HandicapEnabled
    // set is an old on/off handicap (10 points under the duel value).
    public float? HandicapLifestealPercent { get; set; }

    public int Wins { get; set; }
    public int Losses { get; set; }
}

/* Queue Data */

public enum MatchQueueType
{
    Ranked,
    Unranked
}

public sealed record QueueEntry(ulong SteamId)
{
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
}