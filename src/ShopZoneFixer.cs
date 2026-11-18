/*
Clears a stuck "in shop zone" flag.

The game marks a hero as being in a shop's buying zone when it enters a
trigger_item_shop, and clears it when it leaves. Teleports (arena, /spawn, matches,
respawns) move the hero out without a "left the zone" event, so the flag stayed on
and the "Curiosity Shop / Press B to shop" prompt stayed on screen everywhere.

Twice a second this checks every hero that is flagged as in a shop zone, and clears
the flag if the hero isn't actually inside any shop trigger. Heroes really standing
in a shop keep the prompt.
*/
using System;
using System.Collections.Generic;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

public sealed class ShopZoneFixer
{
    private const double CheckIntervalSeconds = 0.5;
    // A little slack so standing right on a trigger's edge doesn't flicker the prompt.
    private const float ZoneMargin = 32f;

    private static readonly SchemaAccessor<bool> InItemShopZone =
        new("CCitadelPlayerPawn"u8, "m_bInItemShopZone"u8);

    private readonly aimLOCK server;
    private readonly List<(Vector3 Min, Vector3 Max)> shopZones = new();
    private bool started;

    public ShopZoneFixer(aimLOCK server)
    {
        this.server = server;
    }

    /// <summary>Call after the map has loaded. Shop triggers don't move, so they are read once.</summary>
    public void Start()
    {
        shopZones.Clear();
        foreach (var entity in Entities.All)
        {
            if (!entity.IsValid || entity.DesignerName != "trigger_item_shop")
                continue;

            var collision = entity.Collision;
            if (collision == null)
                continue;

            var margin = new Vector3(ZoneMargin);
            shopZones.Add((collision.WorldMins - margin, collision.WorldMaxs + margin));
        }

        Console.WriteLine($"[aimLOCK] Shop zone check: {shopZones.Count} shop zones found.");
        if (shopZones.Count == 0 || started)
            return;

        started = true;
        server.ScheduleOnce(CheckIntervalSeconds.Seconds(), Check);
    }

    private void Check()
    {
        server.ScheduleOnce(CheckIntervalSeconds.Seconds(), Check);

        foreach (var player in Players.GetAll())
        {
            if (player.IsBot)
                continue;

            var pawn = player.GetHeroPawn();
            if (pawn is not { IsValid: true } || !InItemShopZone.Get(pawn.Handle))
                continue;

            if (!IsInsideAnyShop(pawn.Position))
                InItemShopZone.Set(pawn.Handle, false);
        }
    }

    private bool IsInsideAnyShop(Vector3 position)
    {
        foreach (var (min, max) in shopZones)
        {
            if (
                position.X >= min.X && position.X <= max.X &&
                position.Y >= min.Y && position.Y <= max.Y &&
                position.Z >= min.Z && position.Z <= max.Z
            )
                return true;
        }

        return false;
    }
}
