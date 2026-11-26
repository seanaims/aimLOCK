/*
This file handels player activity and matches
forces haze, bot spawning, player connections, score tracking, match completion, respawning
*/

namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DeadworksManaged.Api;
using DeadworksManaged.Game;

public partial class PlayerManager
{
    // The Haze model players use. Practice bots were spawning with an old model path
    // (models/heroes_wip/haze/haze.vmdl) that no longer exists in the game; clients
    // logged it as missing and then crashed in client.dll while updating the bot.
    public const string HazeModel = "models/heroes_staging/haze/haze.vmdl";

    // Hero briefly selected on connect before swapping to Haze.
    public const string TemporaryConnectHeroName = "hero_inferno";

    // Precached at map load (see aimLOCK.OnPrecacheResources).
    public static readonly string[] PrecachedParticles =
    {
        MatchTeleportEffect,
    };

    public static readonly string[] PrecachedModels =
    {
        HazeModel,
    };

    private const int MaxPlayerSlots = 64;

    private const long PlayerNameCacheLifetimeMs = 1000;

    private const string MatchTeleportEffect =
        "particles/items/gold_received_coins_player.vpcf";

    // Only used by the /bottest fx debug option.
    private const string RespawnTeleportEffect =
        "particles/generic/player_respawn_deploy.vpcf";

    /// <summary>
    /// ChangeTeam only when the team actually differs. A team change re-networks the
    /// player and their hero; doing it when nothing changes (every /spawn, match end)
    /// was pure overhead on each teleport.
    /// </summary>
    private const string DisarmedModifierName = "modifier_citadel_disarmed";

    private readonly aimLOCK server;

    // PlayerName is read from native memory and allocates a new string on every read.
    // Hot paths (usercmds, damage, HUD clock) use this per-slot cache instead.
    private readonly string?[] cachedPlayerNames = new string?[MaxPlayerSlots];
    private readonly uint[] cachedPlayerNameHandles = new uint[MaxPlayerSlots];
    private readonly long[] cachedPlayerNameExpiry = new long[MaxPlayerSlots];

    private bool duelLoopStarted;

    /* Burst Fire always on */

    // Burst Fire never goes on cooldown because the server sets
    // citadel_ability_cooldown_max (see aimLOCK.ApplyServerConVars), the same way other
    // 1v1 servers do it; its buff is also applied permanently in duels
    // (ApplyPermanentBurstFire). Both use the game's own values, so players' games
    // predict the same fire rate as the server. Nothing runs per hit or on a timer.
    private int duelLoopTick;

    public PlayerManager(aimLOCK server)
    {
        this.server = server;
        this.duelArenaSphereCenters = new Vector3[server.DuelLocations.Length];
        for (int i = 0; i < this.duelArenaSphereCenters.Length; i++)
        {
            this.duelArenaSphereCenters[i] =
                GetDuelArenaSphereCenter(server.DuelLocations[i]);
        }
    }

    public static void RemoveAllDisarms(CCitadelPlayerPawn pawn)
    {
        for (int i = 0; i < 8 && pawn.RemoveModifier(DisarmedModifierName); i++)
        {
        }
    }

    public static void EnsureTeam(CCitadelPlayerController? controller, int team)
    {
        if (controller is { IsValid: true } && controller.TeamNum != team)
        {
            controller.ChangeTeam(team);
        }
    }

    /// <summary>Debug only: attaches the respawn effect to any pawn, bots included.</summary>
    public static CParticleSystem? CreateDebugEffect(CCitadelPlayerPawn pawn) =>
        CParticleSystem.Create(RespawnTeleportEffect)
            .AtPosition(pawn.Position)
            .WithControlPoint(0, pawn)
            .AttachedTo(pawn)
            .Spawn();

    /// <summary>
    /// The one repeating duel loop (20 times a second, one pass over the players, and
    /// nothing at all while no duel is running). Per duelist it caps health at max
    /// health (every pass) and raises the Fixation stack limit (every 10th pass).
    /// Burst Fire is not polled here: see citadel_ability_cooldown_max.
    /// </summary>
    public void StartDuelLoop()
    {
        if (this.duelLoopStarted)
        {
            return;
        }

        this.duelLoopStarted = true;
        this.server.ScheduleOnce(DuelHealthCapCheckIntervalSeconds.Seconds(), DuelLoopTick);
    }

    public void PlayMatchTeleportEffect(CCitadelPlayerPawn pawn)
    {
        this.SpawnAttachedEffect(pawn, MatchTeleportEffect);
    }

    public void ProcessUsercmds(ProcessUsercmdsEvent args)
    {
        // Runs for every player's usercmd batch (roughly every tick per player): one
        // controller/pawn lookup shared by every handler.
        if (
            args.Paused ||
            args.Controller is not { IsValid: true } controller)
        {
            return;
        }

        var pawn = controller.GetHeroPawn();
        if (pawn is not { IsValid: true })
        {
            return;
        }

        DuelMatch? activeMatch = EnforceDuelArenaBounds(controller, pawn);
        if (controller is not CCitadelPlayerController player)
        {
            return;
        }

        if (player.IsBot)
        {
            ProcessPracticeBotUsercmds(args, controller, pawn, activeMatch);
        }
        else if (activeMatch == null)
        {
            ProcessPracticeBotDoorwayZone(player, pawn);
        }
    }

    /// <summary>
    /// The controller's player name, cached per slot for up to a second. Keyed by entity
    /// handle too, so a new player taking over a slot never sees the previous name.
    /// </summary>
    public string GetCachedPlayerName(CBasePlayerController player)
    {
        int slot = player.Slot;
        if ((uint)slot >= MaxPlayerSlots)
        {
            return player.PlayerName;
        }

        long now = Environment.TickCount64;
        uint handle = player.EntityHandle;
        if (
            this.cachedPlayerNames[slot] is { } cachedName &&
            this.cachedPlayerNameHandles[slot] == handle &&
            now < this.cachedPlayerNameExpiry[slot])
        {
            return cachedName;
        }

        string name = player.PlayerName;
        this.cachedPlayerNames[slot] = name;
        this.cachedPlayerNameHandles[slot] = handle;
        this.cachedPlayerNameExpiry[slot] = now + PlayerNameCacheLifetimeMs;
        return name;
    }

    public void InvalidateCachedPlayerName(CBasePlayerController player)
    {
        int slot = player.Slot;
        if ((uint)slot < MaxPlayerSlots)
        {
            this.cachedPlayerNames[slot] = null;
        }
    }

    public void OnPlayerConnect(ClientFullConnectEvent args)
    {
        if (args.Controller is not { } controller)
        {
            return;
        }

        Console.WriteLine(
            $"Forcing Haze for {controller.PlayerName}");

        this.RemoveOrphanPracticeBots();
        this.botViewerMasksDirty = true;

        uint controllerHandle = controller.EntityHandle;
        if (!this.initialHeroSelectionCompleted.Contains(controllerHandle))
        {
            MarkInitialHeroSelectionPending(controller);
            if (this.initialHeroSelectionIssued.Contains(controllerHandle))
            {
                controller.ChangeTeam(2);
                if (this.initialHeroHazeSelectionStarted.Contains(controllerHandle))
                {
                    RequestInitialHazeSelection(controller);
                }
                else
                {
                    RequestInitialSwapHero(controller);
                }
            }
            else
            {
                BeginInitialHazeSpawn(controller);
            }
        }

        this.server.UIManager.SetServerInfoVisibility(controller, true);
        this.server.PlayerTagManager.RefreshPlayerTag(controller);
    }

    public void OnPlayerPutInServer(CCitadelPlayerController controller)
    {
        uint controllerHandle = controller.EntityHandle;
        this.initialHeroSelectionPending.Remove(controllerHandle);
        this.initialHeroSelectionIssued.Remove(controllerHandle);
        this.initialHeroSelectionCompleted.Remove(controllerHandle);
        this.initialHeroHazeSelectionStarted.Remove(controllerHandle);
        this.initialAbilityCurrenciesGranted.Remove(controllerHandle);
        this.initialHeroPawnInitializationHandles.Remove(controllerHandle);
        this.MarkInitialHeroSelectionPending(controller);
        this.server.ScheduleOnce(0.5.Seconds(), () =>
        {
            if (!controller.IsValid)
            {
                return;
            }

            this.MarkInitialHeroSelectionPending(controller);
            this.BeginInitialHazeSpawn(controller);
        });
    }

    public void OnPlayerDisconnect(CCitadelPlayerController controller)
    {
        string playerName = GetMatchParticipantName(controller);
        bool queueChanged =
            this.server.RankedQueue.RemoveAll(entry =>
                entry.SteamId == controller.PlayerSteamId) > 0;
        queueChanged |=
            this.server.UnrankedQueue.RemoveAll(entry =>
                entry.SteamId == controller.PlayerSteamId) > 0;

        this.server.PendingRespawns.Remove(playerName);
        this.respawnRecoveryInProgress.Remove(playerName);
        this.StopSpectating(controller);

        uint controllerHandle = controller.EntityHandle;
        this.initialHeroSelectionPending.Remove(controllerHandle);
        this.initialHeroSelectionIssued.Remove(controllerHandle);
        this.initialHeroSelectionCompleted.Remove(controllerHandle);
        this.initialHeroHazeSelectionStarted.Remove(controllerHandle);
        this.initialAbilityCurrenciesGranted.Remove(controllerHandle);
        this.initialHeroPawnInitializationHandles.Remove(controllerHandle);
        this.practiceBotStrafeSpeedOverrides.Remove(controllerHandle);
        ClearPracticeBotZoneState(controller.Slot);
        InvalidateCachedPlayerName(controller);
        this.duelArenaBoundaryStates.Remove(controller.PlayerSteamId);

        if (
            TryGetActiveMatch(controller, out var match) &&
            !match.Finished)
        {
            this.ForfeitMatch(playerName);
        }

        // A practice bot that left on its own (or was kicked by /botclear) must not
        // stay registered, or no new bot match could start.
        if (this.practiceBots.Count != 0 && this.practiceBots.Remove(controllerHandle))
        {
            this.CancelPendingBotMatch(controllerHandle);
            this.RestorePracticeBotAiIfIdle();
        }

        // The leaving player's own bots go with them, whether or not the forfeit above
        // already removed them: a bot left behind crashed the player when they rejoined.
        this.RemoveLifestealDebug(controller);
        this.RemoveOrphanPracticeBots(controllerHandle);
        if (!controller.IsBot)
        {
            this.RemoveUntrackedBots();
        }

        this.botViewerMasksDirty = true;

        if (queueChanged)
        {
            this.server.UIManager.RefreshHud();
        }
    }

    private static CParticleSystem? CreateAttachedEffect(
        CCitadelPlayerPawn pawn,
        string effectName)
    {
        // No attached effects on bots. Attaching a particle to a bot spawned a moment
        // earlier (hero model not loaded yet on the client) is the likely cause of the
        // client crash at the start of /bot matches: a bare bot never crashed, the bot
        // match did. The effects are cosmetic; players still get theirs.
        if (!pawn.IsValid || pawn.Controller is { IsBot: true })
        {
            return null;
        }

        var effect = CParticleSystem.Create(effectName)
            .AtPosition(pawn.Position)
            .WithControlPoint(0, pawn)
            .AttachedTo(pawn)
            .Spawn();

        if (effect == null)
        {
            Console.WriteLine(
                $"[aimLOCK] Failed to create particle effect '{effectName}'.");
            return null;
        }

        return effect;
    }

    private void DuelLoopTick()
    {
        this.server.ScheduleOnce(DuelHealthCapCheckIntervalSeconds.Seconds(), DuelLoopTick);
        ++this.duelLoopTick;

        // Souls: once a second, put every player back at the max in case the game
        // changed them in a way the currency hook didn't catch (hero setup, respawn).
        if (this.duelLoopTick % 20 == 0)
        {
            foreach (var player in Players.GetAll())
            {
                if (player.IsBot)
                {
                    continue;
                }

                var soulsPawn = player.GetHeroPawn();
                if (soulsPawn is { IsValid: true })
                {
                    TopUpSouls(soulsPawn);
                }
            }
        }

        if (this.server.ActiveMatches.Count == 0)
        {
            return;
        }

        bool checkStackCaps = this.duelLoopTick % 10 == 0;

        foreach (var player in Players.GetAll())
        {
            if (
                !TryGetActiveMatch(player, out var match) ||
                match.Finished ||
                !match.ArenaInitialized)
            {
                continue;
            }

            var pawn = player.GetHeroPawn();
            if (pawn is not { IsValid: true, IsAlive: true })
            {
                continue;
            }

            int maxHealth = pawn.GetMaxHealth();
            if (pawn.Health > maxHealth)
            {
                pawn.Health = maxHealth;
            }

            CheckDuelHealthLedger(pawn, match);

            if (checkStackCaps)
            {
                RaiseFixationStackCap(pawn);
            }
        }
    }

    private void SpawnAttachedEffect(CCitadelPlayerPawn pawn, string effectName)
    {
        var effect = CreateAttachedEffect(pawn, effectName);
        if (effect == null)
        {
            return;
        }

        this.server.ScheduleOnce(3.Seconds(), () =>
        {
            if (effect.IsValid)
            {
                effect.Destroy();
            }
        });
    }
}
