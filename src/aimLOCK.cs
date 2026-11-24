/*
Entry point and main server file

Call all the other files from here, handel some basic server stuff as well

*IMPORTANT* Since deadworks is silly, we have to expose all the api methods here to access them in other files!!!
*/
namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using DeadworksManaged.Api;

[DeclarePermission(
    "aimlock.tags.manage",
    Description = "Grant tag access and assign overhead tags to players.")]
public partial class aimLOCK : DeadworksPluginBase
{
    public const int WINSCORE = 6;

    public const float DUELSPAWNOFFSET = 65f;
    // Server-side lifesteal (see PlayerManager.ApplyServerLifesteal): 100% by default,
    // 85% in duels and bot matches (/lifesteal), or a player's own /handicap value.
    // true = bullet damage only; false = all damage types except Fixation's.
    public const bool DUELLIFESTEALBULLETSONLY = true;

    // Duel bullet damage: bullets deal DUEL_BULLET_DAMAGE / BASE_DUEL_BULLET_DAMAGE
    // times their normal damage during a duel (see PlayerManager.ApplyDuelBulletDamage).
    public const float BASEDUELBULLETDAMAGE = 25f;

    public const float DUELBULLETDAMAGE = 25f; // 25 = normal damage (the 40 change is off)
    public const float DUELBULLETVELOCITY = 100f;

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

    public readonly Dictionary<string, DuelMatch> ActiveMatches = new();

    public readonly List<QueueEntry> RankedQueue = new();
    public readonly List<QueueEntry> UnrankedQueue = new();
    public readonly Dictionary<ulong, PlayerRecord> PlayerRecords = new();
    public readonly Dictionary<string, DuelMatch> PendingRespawns = new();
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
        new Vector3(-1387f, 6881f, 599f),
    };

    public readonly Random Random = new();

    private const float AbilityCooldownMax = 0.001f;

    private static readonly string[] StartupForceKillCommands =
    [
        "citadel_force_kill walkers",
        "citadel_force_kill shrines",
        "citadel_force_kill guardians",
        "citadel_force_kill troopers",
        "citadel_force_kill haunts",
    ];

    // Short lines: the game shrinks a long chat message until it is unreadable.
    private static readonly string[] WelcomeMessages =
    {
        "[aimLOCK] Welcome! This server runs 1v1 duels.",
        "/join ranked for Elo, /join unranked for casual. More: /help",
    };

    private static readonly Vector3 RutgerForceFieldPosition =
        new(-1238f, -3606f, -472f);

    // Content addons mounted server-side and sent to clients, e.g. an edited Haze model
    // with the right-side hitbox fixed. Put <name>.vpk in
    // game/citadel/deadworks_server_addons/vpks/ and add "<name>" here.
    private static readonly string[] ServerContentAddons = { };

    private bool rutgerForceFieldCastScheduled;

    private bool rutgerForceFieldCastAttempted;
    private int? previousSvInfiniteAmmo;
    private bool missingSvInfiniteAmmoLogged;
    public override string Name => "aimLOCK";

    // Managers
    public QueueManager Queue { get; private set; } = null!;
    public PlayerManager PlayerManager { get; private set; } = null!;
    public UIManager UIManager { get; private set; } = null!;
    public RankedEloManager RankedElo { get; private set; } = null!;
    public LeaderboardManager LeaderboardManager { get; private set; } = null!;
    public PingManager PingManager { get; private set; } = null!;
    public PlayerTagManager PlayerTagManager { get; private set; } = null!;
    public GuildManager GuildManager { get; private set; } = null!;
    public MapCleaner MapCleaner { get; private set; } = null!;
    public ShopZoneFixer ShopZoneFixer { get; private set; } = null!;
    public HazeHitboxFilter HazeHitboxFilter { get; } = new();

    public override IReadOnlyList<string> ContentAddons => ServerContentAddons;

    /* Precache */

    public override void OnPrecacheResources()
    {
        // Load these at map load instead of the first time they're used. Without this
        // the server loads the hero (on every player's connect hero swap) and the
        // teleport/respawn particles (on every match start, respawn and /spawn) from
        // disk mid-game, which is what spiked the server frame on load-in and teleports.
        Precache.AddHero(Heroes.Haze);
        if (HeroTypeExtensions.TryParse(PlayerManager.TemporaryConnectHeroName, out var temporaryHero))
        {
            Precache.AddHero(temporaryHero);
        }

        foreach (string particle in PlayerManager.PrecachedParticles)
        {
            Precache.AddResource(particle);
        }

        foreach (string model in PlayerManager.PrecachedModels)
        {
            Precache.AddResource(model);
        }
    }

    public override void OnLoad(bool isReload)
    {
        Console.WriteLine("aimLOCKED and loaded.");

        JsonStore.ResetLocks();
        MigrateLegacyDataFiles();

        this.MapCleaner = new MapCleaner(this);
        this.ShopZoneFixer = new ShopZoneFixer(this);
        this.Queue = new QueueManager(this);
        this.PlayerManager = new PlayerManager(this);
        this.PlayerManager.StartDuelLoop();
        this.PlayerManager.RemoveExistingBots();
        this.PlayerManager.ClearInjectedSoulsFromConnectedPlayers();
        this.UIManager = new UIManager(this);
        this.RankedElo = new RankedEloManager();
        this.PingManager = new PingManager(this);
        this.PlayerTagManager = new PlayerTagManager(this);
        this.GuildManager = new GuildManager(this.RankedElo, this.PlayerTagManager);
        this.PlayerManager.LoadRecords();
        this.LeaderboardManager = new LeaderboardManager(this);
        this.UIManager.RefreshHud();
        Timer.Once(2.Seconds(), this.PlayerTagManager.Initialize);

        // Hot reload: the map is already loaded, so clean it up now.
        if (isReload)
        {
            this.MapCleaner.DisableSpawnConVars();
            Timer.Once(1.Seconds(), this.MapCleaner.StartSweep);
            Timer.Once(2.Seconds(), this.ShopZoneFixer.Start);
        }
    }

    public override void OnStartupServer()
    {
        this.modifierSlowKinds.Clear();
        this.fixationAbilityHandles.Clear();
        this.PlayerManager?.ClearModifierTypeCache();
        this.DisableServerPausing();
        this.DisableHeroKillGoldRewards();
        Timer.Once(3.Seconds(), () => this.RunStartupForceKillCommand(0));

        this.ApplyServerConVars(logChanges: true);

        Timer.Once(2.Seconds(), this.PlayerManager.StartInitialSpawnRecoverySweep);
        Timer.Once(1.Seconds(), this.MapCleaner.StartSweep);
        Timer.Once(2.Seconds(), this.ShopZoneFixer.Start);
        Timer.Once(1.Seconds(), this.PlayerManager.CreateDuelArenaRadiusMarkers);
        Timer.Once(1.Seconds(), this.PlayerManager.CreateEntryZoneLabels);
        Timer.Once(2.Seconds(), this.LeaderboardManager.Initialize);
        this.PingManager.Start();
    }

    public override void OnUnload()
    {
        this.PlayerManager.RemoveDuelArenaRadiusMarkers();
        this.PlayerManager.RemoveEntryZoneLabels();
        this.PlayerManager.RemoveArenaScoreboards();
        this.PlayerTagManager.RemoveAllTagEntities();
        BackgroundFileWriter.Flush(TimeSpan.FromSeconds(5));
        base.OnUnload();
    }

    public void EnableMatchInfiniteAmmo()
    {
        var conVar = ConVar.Find("sv_infinite_ammo");
        if (conVar == null)
        {
            if (!this.missingSvInfiniteAmmoLogged)
            {
                this.missingSvInfiniteAmmoLogged = true;
                Console.WriteLine(
                    "[aimLOCK] Could not find sv_infinite_ammo; infinite ammo was not enabled.");
            }

            return;
        }

        this.previousSvInfiniteAmmo ??= conVar.GetInt();
        if (conVar.GetInt() != 1)
        {
            conVar.SetInt(1);
        }
    }

    public void RestoreMatchInfiniteAmmoIfIdle()
    {
        foreach (var match in this.ActiveMatches.Values)
        {
            if (!match.Finished)
            {
                return;
            }
        }

        if (this.previousSvInfiniteAmmo is not int previousValue)
        {
            return;
        }

        var conVar = ConVar.Find("sv_infinite_ammo");
        if (conVar == null)
        {
            Console.WriteLine(
                "[aimLOCK] Could not find sv_infinite_ammo; its previous value could not be restored.");
            return;
        }

        conVar.SetInt(previousValue);
        this.previousSvInfiniteAmmo = null;
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

    private static void SetConVarInt(string name, int value, bool log)
    {
        var conVar = ConVar.Find(name);
        if (conVar == null)
        {
            if (log)
            {
                Console.WriteLine($"[aimLOCK] Could not find {name}; skipped.");
            }

            return;
        }

        if (conVar.GetInt() == value)
        {
            return;
        }

        conVar.SetInt(value);
        if (log)
        {
            Console.WriteLine($"[aimLOCK] Set {name} to {value}.");
        }
    }

    private static void SetConVarFloat(string name, float value, bool log)
    {
        var conVar = ConVar.Find(name);
        if (conVar == null)
        {
            if (log)
            {
                Console.WriteLine($"[aimLOCK] Could not find {name}; skipped.");
            }

            return;
        }

        if (conVar.GetFloat() == value)
        {
            return;
        }

        conVar.SetFloat(value);
        if (log)
        {
            Console.WriteLine($"[aimLOCK] Set {name} to {value}.");
        }
    }

    private static void MigrateLegacyDataFiles()
    {
        string baseDirectory = AppContext.BaseDirectory;

        JsonStore.MigrateLegacyFile(
            PlayerManager.RecordsFileName,
            "aimlock_records.json",
            Path.Combine(baseDirectory, "aimlock_records.json"));
        JsonStore.MigrateLegacyFile(
            GuildManager.DataFileName,
            Path.Combine(baseDirectory, "aimlock_guilds.json"));
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.TagsFileName,
            Path.Combine(baseDirectory, "aimlock_player_tags.json"));
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.StableTagsFileName,
            Path.Combine(baseDirectory, "aimlock_player_tags_by_id.json"));
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.GrantedTagsFileName,
            Path.Combine(baseDirectory, "aimlock_player_tag_grants.json"));
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.VisibilityFileName,
            Path.Combine(baseDirectory, "aimlock_player_tag_visibility.json"));
        JsonStore.MigrateLegacyFile(
            PlayerTagManager.StableVisibilityFileName,
            Path.Combine(baseDirectory, "aimlock_player_tag_visibility_by_id.json"));
    }

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
        this.MapCleaner.DisableSpawnConVars();
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

    private void RunStartupForceKillCommand(int commandIndex)
    {
        if (commandIndex >= StartupForceKillCommands.Length)
        {
            return;
        }

        string command = StartupForceKillCommands[commandIndex];
        Console.WriteLine($"[aimLOCK] Running startup cleanup command: {command}");
        Server.ExecuteCommand(command, output =>
        {
            string result = string.IsNullOrWhiteSpace(output)
                ? "<no console output>"
                : output.Trim();
            Console.WriteLine(
                $"[aimLOCK] Startup cleanup result for {command}: {result}");
        });

        if (commandIndex + 1 < StartupForceKillCommands.Length)
        {
            Timer.Once(
                0.5.Seconds(),
                () => this.RunStartupForceKillCommand(commandIndex + 1));
        }
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
                    $"[aimLOCK] Could not find {conVarName}; server pausing may remain enabled.");
                continue;
            }

            conVar.SetInt(0);
            int actualValue = conVar.GetInt();
            if (actualValue != 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not disable {conVarName}; it remained at {actualValue}.");
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
                    $"[aimLOCK] Could not find {conVarName}; hero-kill gold rewards may remain enabled.");
                continue;
            }

            conVar.SetInt(0);
            int actualValue = conVar.GetInt();
            if (actualValue != 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not disable {conVarName}; it remained at {actualValue}.");
                continue;
            }

            Console.WriteLine($"[aimLOCK] Disabled {conVarName}.");
        }
    }
}
