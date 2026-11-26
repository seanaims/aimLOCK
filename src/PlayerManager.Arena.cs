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
    private const float DuelArenaCourtHalfSize = 250f;

    private static readonly Vector3 ArenaScoreboardAngles =
        new(180f, 90f, 270f);
    private const float DuelArenaCourtHeight = 200f;
    private const float DuelArenaPlayerMargin = 48f;
    private const float DuelArenaBarrierExpansion = 200f;
    private const float DuelArenaRadiusMarkerWidth = 2f;

    // 32 segments per circle (was 48): 416 beam entities for 13 arenas instead of 624,
    // which the server has to check for every player every tick. Still reads as round.
    private const int DuelArenaCircleSegments = 32;
    private const long FarArenaCheckIntervalMs = 100;

    private static readonly float DuelArenaSphereRadius = MathF.Sqrt(
        (2f * DuelArenaCourtHalfSize * DuelArenaCourtHalfSize) +
        (DuelArenaCourtHeight * DuelArenaCourtHeight / 4f)) + (DuelArenaPlayerMargin * 2f) + DuelArenaBarrierExpansion;

    private static readonly float PracticeBotDuelPlayerRadius =
        DuelArenaSphereRadius - DuelArenaPlayerMargin;

    private static readonly float PracticeBotMaximumHorizontalRadius =
        MathF.Sqrt(MathF.Max(
            0f,
            (PracticeBotDuelPlayerRadius * PracticeBotDuelPlayerRadius) -
                (DuelArenaCourtHeight * DuelArenaCourtHeight / 4f)));

    private static readonly float PracticeBotMaximumPossibleRange =
        2f * PracticeBotMaximumHorizontalRadius;

    private static readonly float PracticeBotMinimumRange =
        PracticeBotMaximumPossibleRange >= PracticeBotDuelMinRange
            ? PracticeBotDuelMinRange
            : PracticeBotMaximumPossibleRange * 0.7f;

    private static readonly float PracticeBotMaximumRange =
        Math.Min(
            PracticeBotDuelMaxRange,
            PracticeBotMaximumPossibleRange);

    private static readonly float PracticeBotMinimumRangeSquared =
        PracticeBotMinimumRange * PracticeBotMinimumRange;

    private static readonly float PracticeBotMaximumRangeSquared =
        PracticeBotMaximumRange * PracticeBotMaximumRange;

    private static readonly float DuelArenaOuterRadius =
        DuelArenaSphereRadius + DuelArenaPlayerMargin;

    private static readonly float DuelArenaOuterRadiusSquared =
        DuelArenaOuterRadius * DuelArenaOuterRadius;

    // Beyond this distance from every arena nobody can reach a wall within
    // FarArenaCheckIntervalMs, so the arena search pauses for that long.
    private static readonly float FarArenaRadius =
        DuelArenaOuterRadius + FarArenaSafetyDistance;

    private static readonly float FarArenaRadiusSquared =
        FarArenaRadius * FarArenaRadius;

    private static readonly Color DuelArenaCircleColor =
        Color.FromArgb(128, 255, 255, 255);

    // Larger than anyone can travel in FarArenaCheckIntervalMs (dashes included).
    private const float FarArenaSafetyDistance = 600f;

    // Arena boundary: players slightly outside are pushed back by velocity only (no
    // teleport, so lag compensation and interpolation are not broken). Only a player
    // this far outside (dash, knockback, teleport) is hard-teleported back in.
    private const float BoundaryHardCorrectionDistance = 96f;
    private const float BoundaryMinPushBackSpeed = 60f;
    private const float BoundaryMaxPushBackSpeed = 350f;

    private const float ArenaScoreboardFontSize = 150f;
    private const float ArenaScoreboardWorldUnitsPerPx = 0.3f;
    private const float ArenaScoreboardHeightAboveCourt = 80f;
    private const float ArenaScoreboardBackgroundDepthOffset = -1f;
    private const int ArenaScoreboardBackgroundPadding = 8;
    private const byte ArenaScoreboardBackgroundAlpha = 160;
    private const char ArenaScoreboardBackgroundGlyph = '\u2588';

    private readonly Dictionary<
        int,
        (DuelMatch Match, CPointWorldText Background, CPointWorldText Text)
    > arenaScoreboards = new();

    // Players nowhere near an arena only need the arena-entry check ~10 times a second.
    private readonly long[] nextFarArenaCheck = new long[MaxPlayerSlots];

    private readonly List<CBeam> duelArenaRadiusBeams = new();
    private readonly Vector3[] duelArenaSphereCenters;
    private readonly Dictionary<ulong, (int ArenaIndex, bool IsInside)>
        duelArenaBoundaryStates = new();

    public void ShowArenaScoreboard(DuelMatch match)
    {
        int arenaIndex = match.ArenaIndex;
        if (
            arenaIndex < 0 ||
            arenaIndex >= this.server.DuelLocations.Length ||
            this.server.DuelLocations[arenaIndex] != match.ArenaPosition)
        {
            Console.WriteLine(
                $"[aimLOCK] Cannot show scoreboard for invalid duel arena {arenaIndex}.");
            return;
        }

        if (
            this.arenaScoreboards.TryGetValue(arenaIndex, out var existing) &&
            ReferenceEquals(existing.Match, match))
        {
            this.UpdateArenaScoreboard(match);
            return;
        }

        this.RemoveArenaScoreboard(arenaIndex);

        string[] lines = this.BuildArenaScoreboardLines(match);
        int longestLine = lines.Max(line => line.Length);
        int backgroundGlyphCount = Math.Max(
            1,
            (int)Math.Ceiling(longestLine / 2d) +
                ArenaScoreboardBackgroundPadding);
        int backgroundLineCount = lines.Length;
        string backgroundLine = new(
            ArenaScoreboardBackgroundGlyph,
            backgroundGlyphCount);
        string backgroundMessage = string.Join(
            "\n",
            Enumerable.Repeat(backgroundLine, backgroundLineCount));
        Vector3 position = GetDuelArenaSphereCenter(match.ArenaPosition) +
            new Vector3(
                0f,
                0f,
                DuelArenaCourtHeight + ArenaScoreboardHeightAboveCourt);

        var background = CPointWorldText.Create(
            backgroundMessage,
            position,
            fontSize: ArenaScoreboardFontSize,
            worldUnitsPerPx: ArenaScoreboardWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1);
        if (background == null)
        {
            Console.WriteLine(
                $"[aimLOCK] Failed to create the scoreboard background for duel arena {arenaIndex + 1}.");
            return;
        }

        background.ColorABGR =
            (uint)ArenaScoreboardBackgroundAlpha << 24;
        background.Fullbright = true;
        background.DepthOffset = ArenaScoreboardBackgroundDepthOffset;
        background.JustifyHorizontal = HorizontalJustify.Center;
        background.Teleport(angles: ArenaScoreboardAngles);

        var text = CPointWorldText.Create(
            string.Join("\n", lines),
            position,
            fontSize: ArenaScoreboardFontSize,
            worldUnitsPerPx: ArenaScoreboardWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1);
        if (text == null)
        {
            background.Remove();
            Console.WriteLine(
                $"[aimLOCK] Failed to create the scoreboard for duel arena {arenaIndex + 1}.");
            return;
        }

        text.ColorABGR = 0xFFFFFFFF;
        text.Fullbright = true;
        text.JustifyHorizontal = HorizontalJustify.Center;
        text.Teleport(angles: ArenaScoreboardAngles);
        this.arenaScoreboards[arenaIndex] = (match, background, text);
    }

    public void UpdateArenaScoreboard(DuelMatch match)
    {
        if (
            !this.arenaScoreboards.TryGetValue(match.ArenaIndex, out var scoreboard) ||
            !ReferenceEquals(scoreboard.Match, match))
        {
            return;
        }

        string[] lines = this.BuildArenaScoreboardLines(match);
        if (
            scoreboard.Text is { IsValid: true } &&
            scoreboard.Background is { IsValid: true })
        {
            scoreboard.Text.SetMessage(string.Join("\n", lines));
            return;
        }

        this.RemoveArenaScoreboard(match.ArenaIndex);
        this.ShowArenaScoreboard(match);
    }

    public void RefreshArenaScoreboards()
    {
        DuelMatch[] activeMatches = this.arenaScoreboards.Values
            .Select(scoreboard => scoreboard.Match)
            .ToArray();
        foreach (DuelMatch match in activeMatches)
        {
            this.UpdateArenaScoreboard(match);
        }
    }

    public void RemoveArenaScoreboards()
    {
        foreach (int arenaIndex in this.arenaScoreboards.Keys.ToArray())
        {
            this.RemoveArenaScoreboard(arenaIndex);
        }
    }

    public void RegisterArenaBoundaryPosition(
        CCitadelPlayerController player,
        Vector3 position)
    {
        int nearestArenaIndex = -1;
        float nearestDistanceSquared = float.MaxValue;
        for (int i = 0; i < this.server.DuelLocations.Length; i++)
        {
            float distanceSquared = Vector3.DistanceSquared(
                position,
                GetDuelArenaSphereCenter(this.server.DuelLocations[i]));
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestArenaIndex = i;
                nearestDistanceSquared = distanceSquared;
            }
        }

        float arenaSphereRadius = DuelArenaSphereRadius;
        float detectionRadius = arenaSphereRadius + DuelArenaPlayerMargin;
        if (
            nearestArenaIndex < 0 ||
            nearestDistanceSquared > detectionRadius * detectionRadius)
        {
            this.duelArenaBoundaryStates.Remove(player.PlayerSteamId);
            return;
        }

        float interiorRadius = arenaSphereRadius - DuelArenaPlayerMargin;
        this.duelArenaBoundaryStates[player.PlayerSteamId] = (
            nearestArenaIndex,
            nearestDistanceSquared <= interiorRadius * interiorRadius);
    }

    public void CreateDuelArenaRadiusMarkers()
    {
        int expectedBeamCount =
            this.server.DuelLocations.Length * DuelArenaCircleSegments;
        if (this.duelArenaRadiusBeams.Count(beam => beam.IsValid) == expectedBeamCount)
        {
            return;
        }

        CBeam.RemoveAll(this.duelArenaRadiusBeams);

        for (int arenaIndex = 0; arenaIndex < this.server.DuelLocations.Length; arenaIndex++)
        {
            Vector3 arena = this.server.DuelLocations[arenaIndex];
            Vector3 sphereCenter = GetDuelArenaSphereCenter(arena);
            float radius = DuelArenaSphereRadius - DuelArenaPlayerMargin;
            var points = new Vector3[DuelArenaCircleSegments];
            for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
            {
                float angle =
                    pointIndex * (2f * MathF.PI / DuelArenaCircleSegments);
                points[pointIndex] = sphereCenter + new Vector3(
                    MathF.Cos(angle) * radius,
                    MathF.Sin(angle) * radius,
                    0f);
            }

            var circleBeams = CBeam.CreatePolyline(
                points,
                DuelArenaRadiusMarkerWidth,
                DuelArenaCircleColor,
                closed: true);
            if (circleBeams.Count != DuelArenaCircleSegments)
            {
                CBeam.RemoveAll(circleBeams);
                CBeam.RemoveAll(this.duelArenaRadiusBeams);
                Console.WriteLine("[aimLOCK] Failed to create a complete duel arena boundary circle.");
                return;
            }

            this.duelArenaRadiusBeams.AddRange(circleBeams);
        }

        Console.WriteLine($"[aimLOCK] Created {this.server.DuelLocations.Length} translucent duel arena boundary circles.");
    }

    private string[] BuildArenaScoreboardLines(DuelMatch match)
    {
        return new[]
        {
            this.GetParticipantDisplayName(match.Player1),
            $"{match.Score1}  -  {match.Score2}",
            this.GetParticipantDisplayName(match.Player2),
        };
    }

    private string GetParticipantDisplayName(string participantName)
    {
        var participant = this.FindMatchParticipant(participantName);
        return participant is { IsBot: true }
            ? GetPracticeBotDisplayName(participant) ?? participant.PlayerName
            : participant == null
                ? this.GetOfflineParticipantName(participantName)
                : this.server.GuildManager.FormatPlayerNameWithSelectedTag(participant);
    }

    private void ResetArenaScoreboardWhenParticipantsLeave(DuelMatch match)
    {
        if (
            !this.arenaScoreboards.TryGetValue(match.ArenaIndex, out var scoreboard) ||
            !ReferenceEquals(scoreboard.Match, match))
        {
            return;
        }

        var onlinePlayers = Players.GetAll().ToArray();
        bool player1InArena = IsMatchParticipantInArena(
            match.Player1,
            match.ArenaPosition,
            onlinePlayers);
        bool player2InArena = IsMatchParticipantInArena(
            match.Player2,
            match.ArenaPosition,
            onlinePlayers);
        if (!player1InArena && !player2InArena)
        {
            this.RemoveArenaScoreboard(match.ArenaIndex);
            return;
        }

        this.server.ScheduleOnce(0.25.Seconds(), () =>
            this.ResetArenaScoreboardWhenParticipantsLeave(match));
    }

    private bool IsMatchParticipantInArena(
        string participantName,
        Vector3 arenaPosition,
        IEnumerable<CCitadelPlayerController> onlinePlayers)
    {
        var participant = this.FindMatchParticipant(
            participantName,
            onlinePlayers);
        var pawn = participant?.GetHeroPawn();
        return pawn is { IsValid: true } &&
            Vector3.DistanceSquared(
                pawn.Position,
                GetDuelArenaSphereCenter(arenaPosition)) <= DuelArenaOuterRadiusSquared;
    }

    private void RemoveArenaScoreboard(int arenaIndex)
    {
        if (!this.arenaScoreboards.Remove(arenaIndex, out var scoreboard))
        {
            return;
        }

        if (scoreboard.Text is { IsValid: true })
        {
            scoreboard.Text.Remove();
        }

        if (scoreboard.Background is { IsValid: true })
        {
            scoreboard.Background.Remove();
        }
    }

    private DuelMatch? EnforceDuelArenaBounds(
        CBasePlayerController controller,
        CCitadelPlayerPawn pawn)
    {
        if (this.TryGetActiveMatch(controller, out var match))
        {
            if (match.Finished || !match.ArenaInitialized)
            {
                return match;
            }

            EnforceSphereBoundary(
                pawn,
                match.ArenaPosition,
                isInside: true,
                DuelArenaSphereRadius);
            return match;
        }

        ulong steamId = controller.PlayerSteamId;
        int slot = controller.Slot;
        bool hasSlot = (uint)slot < MaxPlayerSlots;
        bool hasBoundaryState =
            this.duelArenaBoundaryStates.TryGetValue(steamId, out var boundaryState);
        long now = Environment.TickCount64;
        if (!hasBoundaryState && hasSlot && now < this.nextFarArenaCheck[slot])
        {
            return null;
        }

        Vector3 position = pawn.Position;
        if (!hasBoundaryState)
        {
            int nearestArenaIndex = -1;
            float nearestDistanceSquared = float.MaxValue;
            for (int i = 0; i < this.server.DuelLocations.Length; i++)
            {
                Vector3 offset = position - this.duelArenaSphereCenters[i];
                if (
                    MathF.Abs(offset.X) > FarArenaRadius ||
                    MathF.Abs(offset.Y) > FarArenaRadius ||
                    MathF.Abs(offset.Z) > FarArenaRadius)
                {
                    continue;
                }

                float distanceSquared = offset.LengthSquared();
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestArenaIndex = i;
                    nearestDistanceSquared = distanceSquared;
                }
            }

            if (
                nearestArenaIndex < 0 ||
                nearestDistanceSquared > DuelArenaOuterRadiusSquared)
            {
                // Well clear of every arena: nobody can reach a wall within the next
                // interval, so skip the arena search until then.
                if (
                    hasSlot &&
                    (nearestArenaIndex < 0 || nearestDistanceSquared > FarArenaRadiusSquared))
                {
                    this.nextFarArenaCheck[slot] = now + FarArenaCheckIntervalMs;
                }

                return null;
            }

            float interiorRadius =
                DuelArenaSphereRadius - DuelArenaPlayerMargin;
            boundaryState = (
                nearestArenaIndex,
                nearestDistanceSquared <= interiorRadius * interiorRadius);
            this.duelArenaBoundaryStates[steamId] = boundaryState;
        }
        else if (
            Vector3.DistanceSquared(
                position,
                this.duelArenaSphereCenters[boundaryState.ArenaIndex]) > DuelArenaOuterRadiusSquared)
        {
            this.duelArenaBoundaryStates.Remove(steamId);
            return null;
        }

        EnforceSphereBoundary(
            pawn,
            this.server.DuelLocations[boundaryState.ArenaIndex],
            boundaryState.IsInside,
            DuelArenaSphereRadius);
        return null;
    }

    private static void EnforceSphereBoundary(
        CCitadelPlayerPawn pawn,
        Vector3 arenaPosition,
        bool isInside,
        float sphereRadius)
    {
        Vector3 sphereCenter = GetDuelArenaSphereCenter(arenaPosition);
        Vector3 offset = pawn.Position - sphereCenter;
        float distanceSquared = offset.LengthSquared();
        float boundaryRadius = isInside
            ? sphereRadius - DuelArenaPlayerMargin
            : sphereRadius;
        if (
            isInside
                ? distanceSquared <= boundaryRadius * boundaryRadius
                : distanceSquared >= boundaryRadius * boundaryRadius)
        {
            return;
        }

        float distance = MathF.Sqrt(distanceSquared);
        Vector3 outward = distance > 0f ? offset / distance : Vector3.UnitX;

        // Direction that leads back into the allowed area.
        Vector3 allowedDirection = isInside ? -outward : outward;
        float penetration = isInside
            ? distance - boundaryRadius
            : boundaryRadius - distance;

        Vector3 velocity = pawn.AbsVelocity;
        float speedTowardAllowed = Vector3.Dot(velocity, allowedDirection);

        if (penetration > BoundaryHardCorrectionDistance)
        {
            // Escaped far past the wall (dash, knockback, teleport): snap back once.
            if (speedTowardAllowed < 0f)
            {
                velocity -= allowedDirection * speedTowardAllowed;
            }

            pawn.Teleport(
                sphereCenter + (outward * boundaryRadius),
                null,
                velocity);
            return;
        }

        // Soft wall: cancel movement through the boundary and push back in, changing
        // velocity only. Teleporting the position here (the old behaviour) ran on every
        // usercmd batch while a player strafed against the wall, which broke
        // interpolation and lag-compensation history for that player and caused
        // rubber-banding and missed shots.
        float pushBackSpeed = Math.Clamp(
            penetration * 8f,
            BoundaryMinPushBackSpeed,
            BoundaryMaxPushBackSpeed);
        if (speedTowardAllowed >= pushBackSpeed)
        {
            return;
        }

        velocity += allowedDirection * (pushBackSpeed - speedTowardAllowed);
        pawn.Teleport(null, null, velocity);
    }

    public void RemoveDuelArenaRadiusMarkers() =>
        CBeam.RemoveAll(this.duelArenaRadiusBeams);

    /* Where a duelist stands at round start: one on each side, facing in */
    public (Vector3 Position, Vector3 Angles) GetDuelSpawn(DuelMatch match, bool isPlayer1)
    {
        Vector3 arenaCenter = GetDuelArenaSphereCenter(match.ArenaPosition);
        return isPlayer1
            ? (
                arenaCenter + new Vector3(-aimLOCK.DUELSPAWNOFFSET, 0f, 0f),
                new Vector3(0f, 45f, 0f))
            : (
                arenaCenter + new Vector3(aimLOCK.DUELSPAWNOFFSET, 0f, 0f),
                new Vector3(0f, 180f, 0f));
    }

    public Vector3 GetDuelArenaCenter(Vector3 arena) =>
        GetDuelArenaSphereCenter(arena);

    private static Vector3 GetDuelArenaSphereCenter(Vector3 arena) =>
        arena + new Vector3(0f, 0f, DuelArenaCourtHeight / 2f);
}
