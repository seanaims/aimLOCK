using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

public partial class aimLOCK
{
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
                if (!connectedPlayer.IsValid)
                    return;

                foreach (string message in WelcomeMessages)
                    Chat.PrintToChat(connectedPlayer, message);
                if (connectedPlayer.IsBot)
                    return;

                ulong steamId = connectedPlayer.PlayerSteamId;
                PlayerTagManager.TryAutoEquipBestTag(steamId);
                if (PlayerTagManager.GetTagHint(steamId) is { } tagHint)
                    Chat.PrintToChat(connectedPlayer, tagHint);
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
}
