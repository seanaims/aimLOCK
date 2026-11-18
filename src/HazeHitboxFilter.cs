/*
Server-side workaround for Haze's oversized hitbox on her right side.

Hitboxes come from the model file, so the real fix is a Haze model with that hitbox
shrunk or removed, mounted server-side (see ContentAddons in aimLOCK.cs). Until then,
this filter can ignore bullet hits that land on a specific hit group of a Haze pawn.

The hit group ID of the bad box isn't known ahead of time, so:
  1. /hitdebug (aimlock.debug) - while on, every bullet hit on a Haze pawn prints the
     hit group ID and the hit position relative to the victim (x forward, y left, z up).
  2. Shoot only the bad box; note its hit group ID. Then shoot the head/chest/arms and
     confirm that ID does NOT appear for real body parts.
  3. If the ID is unique to the box, /hazehitgroup <id> blocks it live for testing, then
     put it in BlockedHitGroups below so it's on after restarts.
If the box shares an ID with a real body part, this filter can't separate them and the
model edit is the only fix.
*/
using System;
using System.Collections.Generic;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

public sealed class HazeHitboxFilter
{
    // Hit group IDs to ignore on Haze. Empty until calibrated with /hitdebug.
    private static readonly int[] DefaultBlockedHitGroups = { };

    private static readonly SchemaAccessor<Vector3> DamagePosition =
        new("CTakeDamageInfo"u8, "m_vecDamagePosition"u8);
    private static readonly SchemaAccessor<int> HitGroupId =
        new("CTakeDamageInfo"u8, "m_iHitGroupId"u8);

    private readonly HashSet<int> blockedHitGroups = new(DefaultBlockedHitGroups);
    private readonly HashSet<int> debugSlots = new();

    public IReadOnlyCollection<int> BlockedHitGroups => blockedHitGroups;

    /// <summary>Returns true when this hit should be ignored.</summary>
    public bool ShouldBlock(TakeDamageEvent args)
    {
        if (blockedHitGroups.Count == 0 && debugSlots.Count == 0)
            return false;

        var info = args.Info;
        if (info.CitadelDamageType != ECitadelDamageType.Bullet)
            return false;

        var victim = args.Entity.As<CCitadelPlayerPawn>();
        if (victim is not { IsValid: true } || victim.HeroID != Heroes.Haze)
            return false;

        int hitGroup = HitGroupId.Get(info.Handle);
        bool block = blockedHitGroups.Contains(hitGroup);

        if (debugSlots.Count > 0)
            PrintDebug(victim, info, hitGroup, block);

        return block;
    }

    public bool ToggleDebug(CCitadelPlayerController player)
    {
        if (debugSlots.Remove(player.Slot))
            return false;

        debugSlots.Add(player.Slot);
        return true;
    }

    public void RemoveDebugSubscriber(CCitadelPlayerController player) =>
        debugSlots.Remove(player.Slot);

    /// <summary>Toggles a hit group on or off. Returns true if it is now blocked.</summary>
    public bool ToggleBlockedHitGroup(int hitGroup)
    {
        if (blockedHitGroups.Remove(hitGroup))
            return false;

        blockedHitGroups.Add(hitGroup);
        return true;
    }

    public void ClearBlockedHitGroups() => blockedHitGroups.Clear();

    private void PrintDebug(
        CCitadelPlayerPawn victim,
        CTakeDamageInfo info,
        int hitGroup,
        bool blocked)
    {
        // Position relative to the victim's feet, rotated into the victim's facing.
        // Approximate: the victim's current position isn't lag-compensated, so expect
        // a few units of drift while they're moving.
        Vector3 offset = DamagePosition.Get(info.Handle) - victim.Position;
        float yaw = victim.Rotation.Y * MathF.PI / 180f;
        float cos = MathF.Cos(yaw);
        float sin = MathF.Sin(yaw);
        float forward = offset.X * cos + offset.Y * sin;
        float left = -offset.X * sin + offset.Y * cos;

        string message =
            $"[HITDEBUG] Haze hit group {hitGroup} at fwd {forward:0}, left {left:0}, up {offset.Z:0}" +
            (blocked ? " - BLOCKED" : "");

        foreach (var player in Players.GetAll())
        {
            if (debugSlots.Contains(player.Slot))
                Chat.PrintToChat(player, message);
        }
    }
}
