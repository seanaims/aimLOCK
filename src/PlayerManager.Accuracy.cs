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

namespace aimLOCK;

public partial class PlayerManager
{
    /* Round accuracy */

    // Per player, for the current duel round only: shots fired and bullet hits on the
    // opponent, split into headshots and body shots. Counted only while the round is
    // live (after FIGHT, before the kill), read out to each player when a round is
    // scored, and cleared at the start of every round.
    private sealed class RoundAccuracy
    {
        public int Shots;
        public int HeadHits;
        public int BodyHits;
    }

    private readonly Dictionary<uint, RoundAccuracy> roundAccuracy = new();
    private bool loggedGunFiredFields;

    private static readonly SchemaAccessor<float> DamageCritDamage =
        new("CTakeDamageInfo"u8, "m_flCritDamage"u8);
    private static readonly SchemaAccessor<int> DamageHitGroup =
        new("CTakeDamageInfo"u8, "m_iHitGroupId"u8);
    private const int HeadHitGroup = 1;

    private RoundAccuracy? GetLiveRoundAccuracy(CCitadelPlayerPawn? pawn)
    {
        if (
            server.ActiveMatches.Count == 0 ||
            pawn is not { IsValid: true } ||
            pawn.Controller is not { } controller ||
            !TryGetActiveMatch(controller, out var match) ||
            match.Finished ||
            !match.CombatActive
        )
            return null;

        uint handle = controller.EntityHandle;
        if (!roundAccuracy.TryGetValue(handle, out var stats))
            roundAccuracy[handle] = stats = new RoundAccuracy();
        return stats;
    }

    /// <summary>A shot fired (the GunFired modifier event).</summary>
    public void OnGunFired(ModifierEvent args)
    {
        if (server.ActiveMatches.Count == 0)
            return;

        if (!loggedGunFiredFields)
        {
            loggedGunFiredFields = true;
            Console.WriteLine(
                "[aimLOCK] Accuracy: first shot seen. Caster: " +
                (args.Caster?.DesignerName ?? "none") + ", target: " +
                (args.Target?.DesignerName ?? "none") + "."
            );
        }

        var shooter = args.Caster?.As<CCitadelPlayerPawn>();
        if (shooter is not { IsValid: true, Controller: not null })
            shooter = args.Target?.As<CCitadelPlayerPawn>();

        var stats = GetLiveRoundAccuracy(shooter);
        if (stats != null)
            stats.Shots++;
    }

    /// <summary>A bullet hit (from DamageTaken), counted as a headshot or a body shot.</summary>
    private void CountAccuracyHit(
        CCitadelPlayerPawn attacker,
        CCitadelPlayerPawn victim,
        CTakeDamageInfo info
    )
    {
        var stats = GetLiveRoundAccuracy(attacker);
        if (stats == null)
            return;

        bool headshot =
            DamageCritDamage.Get(info.Handle) > 0f ||
            DamageHitGroup.Get(info.Handle) == HeadHitGroup;
        if (headshot)
            stats.HeadHits++;
        else
            stats.BodyHits++;
    }

    private void ResetRoundAccuracy(DuelMatch match)
    {
        foreach (var participant in GetMatchParticipants(match))
            roundAccuracy.Remove(participant.EntityHandle);
    }

    private void ReportRoundAccuracy(DuelMatch match)
    {
        foreach (var participant in GetMatchParticipants(match))
        {
            if (!roundAccuracy.Remove(participant.EntityHandle, out var stats))
                stats = new RoundAccuracy();
            if (participant.IsBot)
                continue;

            int shots = Math.Max(stats.Shots, stats.HeadHits + stats.BodyHits);
            if (shots == 0)
            {
                Chat.PrintToChat(participant, "Round accuracy: no shots fired.");
                continue;
            }

            Chat.PrintToChat(
                participant,
                $"Headshot accuracy: {Percent(stats.HeadHits, shots)} | " +
                $"Overall accuracy: {Percent(stats.HeadHits + stats.BodyHits, shots)} | " +
                $"{shots} shots"
            );
        }
    }

    private static string Percent(int hits, int shots) =>
        $"{Math.Round(hits * 100.0 / shots):0}% ({hits}/{shots})";
}
