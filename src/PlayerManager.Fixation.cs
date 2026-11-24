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
    private const string FixationAbilityName = "ability_stacking_damage";
    private const string FixationModifierName =
        "ability_stacking_damage/modifier_passive_haze_stacking_damage";

    private const short FixationMaxStacks = 999;

    /* Fixation stack cap */

    // The Fixation stacks on the person being shot are their own modifier with their
    // own stack limit (80 with Fixation fully upgraded), separate from the passive whose
    // MaxStacks is set to 999. Raise that limit on the stack modifier itself.
    private const short FixationTargetMaxStacks = 999;
    private const string FixationModifierNamePrefix = "ability_stacking_damage/";
    private static readonly SchemaAccessor<short> ModifierMaxStackCount =
        new("CBaseModifier"u8, "m_iMaxStackCount"u8);

    private static readonly SchemaAccessor<short> ModifierStackCount =
        new("CBaseModifier"u8, "m_iStackCount"u8);

    /* Fixation reset on kill */

    private static readonly SchemaAccessor<uint> ModifierAbilityHandle =
        new("CBaseModifier"u8, "m_hAbility"u8);

    // The re-added passive has no link to the ability, so the game client showed it as
    // a permanent status icon on the HUD. The passive only does work on the server (it
    // puts the stacks on whoever gets shot; those still show on the target as normal),
    // so it is no longer sent to clients at all.
    private static readonly SchemaAccessor<bool> ModifierSuppressSend =
        new("CBaseModifier"u8, "m_bSuppressSendModifier"u8);

    private static bool loggedSuppressSendFailure;

    private readonly List<CBaseModifier> fixationModifierScratch = new();

    // Modifier type (VData pointer) -> is it one of Fixation's own modifiers
    // ("ability_stacking_damage/..."). Cached so the name is read once per type.
    private readonly Dictionary<nint, bool> fixationModifierTypes = new();

    // Crash test switch (/hidepassive): whether the Fixation passive is kept off the
    // clients. Hiding it is one of the few things every Haze on the server (players and
    // bots alike) has that the original plugin didn't, so it is a suspect for the crash
    // when someone joins while another Haze is already on the server.
    public bool HideFixationPassive { get; private set; } = true;

    /// <summary>
    /// /fixdebug: lists every modifier on you and your duel opponent that came from a
    /// Fixation ability, with its stack count and limit, to the chat and server log.
    /// </summary>
    public void FixationDebugCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            return;
        }

        var targets = new List<CCitadelPlayerController> { player };
        if (TryGetActiveMatch(player, out var match))
        {
            foreach (var participant in this.GetMatchParticipants(match))
            {
                if (participant.EntityHandle != player.EntityHandle)
                {
                    targets.Add(participant);
                }
            }
        }

        foreach (var target in targets)
        {
            var pawn = target.GetHeroPawn();
            var modifiers = pawn?.ModifierProp?.Modifiers;
            if (pawn == null || modifiers == null)
            {
                continue;
            }

            string who = GetCachedPlayerName(target);
            int found = 0;
            foreach (var modifier in modifiers)
            {
                uint abilityHandle = ModifierAbilityHandle.Get(modifier.Handle);
                string name = modifier.SubclassVData?.Name ?? "?";
                bool fromFixation = abilityHandle != 0 && abilityHandle != 0xFFFFFFFF &&
                    this.server.IsFixationAbility(abilityHandle);
                if (!fromFixation && !name.Contains("stacking", StringComparison.Ordinal))
                {
                    continue;
                }

                found++;
                string line =
                    $"[fixdebug] {who}: {name} stacks={ModifierStackCount.Get(modifier.Handle)} " +
                    $"max={ModifierMaxStackCount.Get(modifier.Handle)} fromFixation={fromFixation}";
                caller.Reply(line);
                Console.WriteLine($"[aimLOCK] {line}");
            }

            if (found == 0)
            {
                caller.Reply($"[fixdebug] {who}: no Fixation modifiers.");
            }
        }
    }

    public void ClearModifierTypeCache() => this.fixationModifierTypes.Clear();

    public int MaxFixationForOnlinePlayers()
    {
        int updatedPlayers = 0;
        foreach (var controller in Players.GetAll())
        {
            if (!controller.IsValid)
            {
                continue;
            }

            var pawn = controller.GetHeroPawn();
            if (pawn is { IsValid: true } && ApplyFixation(pawn))
            {
                updatedPlayers++;
            }
        }

        return updatedPlayers;
    }

    /// <summary>Turns hiding the Fixation passive on or off and redoes Fixation for everyone.</summary>
    public int SetHideFixationPassive(bool hide)
    {
        this.HideFixationPassive = hide;
        return this.MaxFixationForOnlinePlayers();
    }

    public void ResetMatchFixationStacks(DuelMatch match)
    {
        foreach (var participant in this.GetMatchParticipants(match))
        {
            this.ResetFixationStacks(participant);
        }
    }

    private static void HideFromClients(CBaseModifier modifier)
    {
        nint handle = modifier.Handle;
        if (handle == 0)
        {
            return;
        }

        nint address = ModifierSuppressSend.GetAddress(handle);

        // A real field sits past the VData pointer at 0x10; anything else means the
        // field was not found, so don't write.
        if (address - handle <= 0x10 || address - handle > 0x1000)
        {
            if (!loggedSuppressSendFailure)
            {
                loggedSuppressSendFailure = true;
                Console.WriteLine("[aimLOCK] Could not find m_bSuppressSendModifier; Fixation HUD icon stays.");
            }

            return;
        }

        System.Runtime.InteropServices.Marshal.WriteByte(address, 1);
    }

    private void RaiseFixationStackCap(CCitadelPlayerPawn pawn)
    {
        var modifiers = pawn.ModifierProp?.Modifiers;
        if (modifiers == null)
        {
            return;
        }

        foreach (var modifier in modifiers)
        {
            if (!IsFixationModifier(modifier))
            {
                continue;
            }

            short max = ModifierMaxStackCount.Get(modifier.Handle);
            if (max <= 0 || max >= FixationTargetMaxStacks)
            {
                continue;
            }

            // Plain write, no network notify: modifiers are not entities, and only
            // the server's stack logic needs the new limit.
            nint address = ModifierMaxStackCount.GetAddress(modifier.Handle);
            if (address - modifier.Handle <= 0x10 || address - modifier.Handle > 0x1000)
            {
                return;
            }

            System.Runtime.InteropServices.Marshal.WriteInt16(address, FixationTargetMaxStacks);
        }
    }

    /// <summary>
    /// Removes every modifier that came from a Fixation ability (the stacks on the
    /// person who was shot, and anything they applied). Used on every kill so each round
    /// starts from zero.
    /// </summary>
    private void RemoveFixationModifiers(CCitadelPlayerPawn pawn)
    {
        var modifiers = pawn.ModifierProp?.Modifiers;
        if (modifiers == null || modifiers.Count == 0)
        {
            return;
        }

        // Collected first, then removed: removing while walking the list could skip
        // the modifier that slides into the removed one's place.
        this.fixationModifierScratch.Clear();
        foreach (var modifier in modifiers)
        {
            if (IsFixationModifier(modifier))
            {
                this.fixationModifierScratch.Add(modifier);
            }
        }

        foreach (var modifier in this.fixationModifierScratch)
        {
            pawn.RemoveModifier(modifier);
        }

        this.fixationModifierScratch.Clear();
    }

    /// <summary>
    /// A modifier is Fixation's when it came from a Fixation ability, or when it is one
    /// of Fixation's own modifier types. The second check catches stacks that have no
    /// ability attached (those were left behind before).
    /// </summary>
    private bool IsFixationModifier(CBaseModifier modifier)
    {
        uint abilityHandle = ModifierAbilityHandle.Get(modifier.Handle);
        if (
            abilityHandle != 0 &&
            abilityHandle != 0xFFFFFFFF &&
            this.server.IsFixationAbility(abilityHandle))
        {
            return true;
        }

        var vdata = modifier.SubclassVData;
        if (vdata == null)
        {
            return false;
        }

        nint type = vdata.Handle;
        if (!this.fixationModifierTypes.TryGetValue(type, out bool isFixation))
        {
            if (this.fixationModifierTypes.Count > 2048)
            {
                this.fixationModifierTypes.Clear();
            }

            isFixation =
                vdata.Name?.StartsWith(FixationModifierNamePrefix, StringComparison.Ordinal) == true;
            this.fixationModifierTypes[type] = isFixation;
        }

        return isFixation;
    }

    private bool ApplyFixation(CCitadelPlayerPawn pawn)
    {
        if (!pawn.IsValid)
        {
            return false;
        }

        var fixation = pawn.AbilityComponent?.FindAbilityByName(FixationAbilityName);
        var signature3Ability = pawn.GetAbilityBySlot(EAbilitySlot.Signature3);
        if (fixation == null || fixation.EntityHandle != signature3Ability?.EntityHandle)
        {
            if (
                fixation != null
                && !pawn.RemoveAbility(fixation))
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not move Fixation to Signature3 for {pawn.Controller?.PlayerName ?? "player"}.");
                return false;
            }

            var addedFixation = pawn.AddAbility(
                FixationAbilityName,
                (ushort)EAbilitySlot.Signature3);
            if (addedFixation == null || !addedFixation.IsValid)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not add Fixation to {pawn.Controller?.PlayerName ?? "player"}.");
                return false;
            }

            fixation = pawn.AbilityComponent?.FindAbilityByName(FixationAbilityName);
            if (fixation == null || !fixation.IsValid)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not find Fixation after adding it to {pawn.Controller?.PlayerName ?? "player"}.");
                return false;
            }
        }

        // Networked field: only write it when it actually changes.
        if (fixation.UpgradeBits != FullyUpgradedAbilityBits)
        {
            fixation.UpgradeBits = FullyUpgradedAbilityBits;
        }

        if (fixation.UpgradeBits != FullyUpgradedAbilityBits)
        {
            Console.WriteLine(
                $"[aimLOCK] Could not fully upgrade Fixation for {pawn.Controller?.PlayerName ?? "player"}.");
            return false;
        }

        pawn.RemoveModifier(FixationModifierName);
        var fixationModifier = pawn.AddModifier(
            FixationModifierName,
            abilityValues: new()
            {
                ["MaxStacks"] = FixationMaxStacks,
            });
        if (fixationModifier is not { IsValid: true })
        {
            Console.WriteLine(
                $"[aimLOCK] Could not add the Fixation stack modifier to {pawn.Controller?.PlayerName ?? "player"}.");
            return false;
        }

        var modifierSchema = fixationModifier.AsSchema<Schema.CBaseModifier>();
        modifierSchema.m_iMaxStackCount = FixationMaxStacks;
        if (modifierSchema.m_iMaxStackCount != FixationMaxStacks)
        {
            Console.WriteLine(
                $"[aimLOCK] Could not set Fixation's runtime stack cap to {FixationMaxStacks} for {pawn.Controller?.PlayerName ?? "player"}.");
            return false;
        }

        if (this.HideFixationPassive)
        {
            HideFromClients(fixationModifier);
        }

        return true;
    }

    private void ResetFixationStacks(CCitadelPlayerController player)
    {
        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true })
        {
            return;
        }

        // The stacks live on the person who was shot (a modifier from the shooter's
        // Fixation), not on the shooter's passive: remove those too. The player who
        // died gets a fresh passive with the respawn loadout.
        RemoveFixationModifiers(pawn);
        if (!pawn.IsAlive)
        {
            return;
        }

        var fixation = pawn.AbilityComponent?.FindAbilityByName(
            FixationAbilityName);
        if (fixation is not { IsValid: true })
        {
            ApplyFixation(pawn);
            return;
        }

        pawn.RemoveModifier(FixationModifierName);
        var fixationModifier = pawn.AddModifier(
            FixationModifierName,
            abilityValues: new()
            {
                ["MaxStacks"] = FixationMaxStacks,
            });
        if (fixationModifier is not { IsValid: true })
        {
            Console.WriteLine(
                $"[aimLOCK] Could not reset Fixation stacks for {player.PlayerName ?? "player"}.");
            return;
        }

        if (this.HideFixationPassive)
        {
            HideFromClients(fixationModifier);
        }

        var modifierSchema = fixationModifier.AsSchema<Schema.CBaseModifier>();
        modifierSchema.m_iMaxStackCount = FixationMaxStacks;
        modifierSchema.m_iStackCount = 0;
        if (
            modifierSchema.m_iMaxStackCount != FixationMaxStacks ||
            modifierSchema.m_iStackCount != 0)
        {
            Console.WriteLine(
                $"[aimLOCK] Could not reset Fixation stacks for {player.PlayerName ?? "player"}.");
        }
    }
}
