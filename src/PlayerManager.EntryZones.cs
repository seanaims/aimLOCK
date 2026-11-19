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
    private const float PracticeBotDoorwayLabelFontSize = 68f;
    private const float PracticeBotDoorwayLabelWorldUnitsPerPx = 0.3f;
    private const float PracticeBotDoorwayLabelHorizontalPadding = 80f;
    private const float QueueEntryLabelHorizontalPadding = 180f;
    private static readonly Vector3[] RemovedBotsLabelPositions =
    {
        new(-745f, -9525f, 1265f),
        new(-821f, -9628f, 1244f),
        new(-788f, -9568f, 1248f)
    };
    private const byte PracticeBotDoorwayLabelBackgroundAlpha = 160;
    private const char PracticeBotDoorwayLabelBackgroundGlyph = '\u2588';
    private const float PracticeBotDoorwayLabelBackgroundDepthOffset = -1f;
    private const float PracticeBotDoorwayLabelNearbyDistanceSquared = 25f;
    private static readonly Vector3 PracticeBotDoorwayLabelAngles =
        new(180f, 90f, 270f);
    private static readonly PracticeBotZone[] PracticeBotZones =
    {
        new(
            new Vector3(-555f, -9208f, 1198f),
            140f,
            "easy",
            "Easy Bot",
            0xFF00FF00,
            new Vector3(-545f, -9244f, 1473f)
        ),
        new(
            new Vector3(-205f, -9100f, 1217f),
            120f,
            "medium",
            "Medium Bot",
            0xFF00A5FF,
            new Vector3(-175f, -9191f, 1392f)
        ),
        new(
            new Vector3(-65f, -8945f, 1258f),
            120f,
            "hard",
            "Hard Bot",
            0xFF0000FF,
            new Vector3(45f, -8931f, 1433f)
        )
    };
    private static readonly QueueEntryZone[] QueueEntryZones =
    {
        new(
            new Vector3(-2079f, -9270f, 1211f),
            100f,
            MatchQueueType.Unranked,
            "UNRANKED QUEUE",
            0xFF00FF00,
            new Vector3(-2079f, -9270f, 1411f)
        ),
        new(
            new Vector3(-2298f, -9224f, 1211f),
            100f,
            MatchQueueType.Ranked,
            "RANKED QUEUE",
            0xFFFF0000,
            new Vector3(-2353f, -9224f, 1411f)
        ),
        new(
            new Vector3(-2546f, -8967f, 1214f),
            100f,
            null,
            "LEAVE QUEUE",
            0xFF0000FF,
            new Vector3(-2571f, -8942f, 1414f)
        )
    };

    private const long PracticeBotZoneCheckIntervalMs = 100;

    private readonly record struct PracticeBotZone(
        Vector3 Center,
        float Radius,
        string Difficulty,
        string Label,
        uint LabelColorABGR,
        Vector3 LabelPosition
    )
    {
        public float RadiusSquared { get; } = Radius * Radius;
    }
    private readonly record struct QueueEntryZone(
        Vector3 Center,
        float Radius,
        MatchQueueType? QueueType,
        string Label,
        uint LabelColorABGR,
        Vector3 LabelPosition
    )
    {
        public float RadiusSquared { get; } = Radius * Radius;
    }

    // Indexed by player slot * zone count + zone index. Avoids building a string key
    // for every zone on every usercmd batch.
    private readonly bool[] playersInsidePracticeBotZones =
        new bool[MaxPlayerSlots * PracticeBotZones.Length];
    private readonly bool[] playersInsideQueueEntryZones =
        new bool[MaxPlayerSlots * QueueEntryZones.Length];
    private readonly long[] nextPracticeBotZoneCheck = new long[MaxPlayerSlots];

    private readonly List<CPointWorldText> entryZoneLabelEntities = new();

    private void ProcessPracticeBotDoorwayZone(
        CCitadelPlayerController player,
        CCitadelPlayerPawn pawn
    )
    {
        int slot = player.Slot;
        if ((uint)slot >= MaxPlayerSlots)
            return;

        // Zone entry doesn't need tick precision; check ~10 times a second. Indexed by
        // slot instead of a tuple set lookup per zone per usercmd.
        long now = Environment.TickCount64;
        if (now < nextPracticeBotZoneCheck[slot])
            return;
        nextPracticeBotZoneCheck[slot] = now + PracticeBotZoneCheckIntervalMs;

        Vector3 position = pawn.Position;
        for (int zoneIndex = 0; zoneIndex < PracticeBotZones.Length; zoneIndex++)
        {
            PracticeBotZone zone = PracticeBotZones[zoneIndex];
            int entryIndex = slot * PracticeBotZones.Length + zoneIndex;
            bool isInsideZone =
                Vector3.DistanceSquared(position, zone.Center) <=
                zone.RadiusSquared;

            if (!isInsideZone)
            {
                playersInsidePracticeBotZones[entryIndex] = false;
                continue;
            }

            if (playersInsidePracticeBotZones[entryIndex])
                continue;

            playersInsidePracticeBotZones[entryIndex] = true;
            TryStartPracticeBotMatch(
                player,
                zone.Difficulty,
                message => Chat.PrintToChat(player, message)
            );
        }

        for (int zoneIndex = 0; zoneIndex < QueueEntryZones.Length; zoneIndex++)
        {
            QueueEntryZone zone = QueueEntryZones[zoneIndex];
            int entryIndex = slot * QueueEntryZones.Length + zoneIndex;
            bool isInsideZone =
                Vector3.DistanceSquared(position, zone.Center) <=
                zone.RadiusSquared;

            if (!isInsideZone)
            {
                playersInsideQueueEntryZones[entryIndex] = false;
                continue;
            }

            if (playersInsideQueueEntryZones[entryIndex])
                continue;

            playersInsideQueueEntryZones[entryIndex] = true;
            if (zone.QueueType is MatchQueueType queueType)
                server.Queue.JoinQueueFromZone(player, queueType);
            else
                server.Queue.LeaveQueue(Caller.Of(player));
        }
    }

    private void ClearPracticeBotZoneState(int slot)
    {
        if ((uint)slot >= MaxPlayerSlots)
            return;

        Array.Clear(
            playersInsidePracticeBotZones,
            slot * PracticeBotZones.Length,
            PracticeBotZones.Length
        );
        Array.Clear(
            playersInsideQueueEntryZones,
            slot * QueueEntryZones.Length,
            QueueEntryZones.Length
        );
        nextPracticeBotZoneCheck[slot] = 0;
    }

    public void CreateEntryZoneLabels()
    {
        // One pass over the map for all zones instead of one full scan per zone.
        foreach (var entity in Entities.All)
        {
            if (!entity.Is<CPointWorldText>())
                continue;

            Vector3 entityPosition = entity.Position;
            bool isExistingZoneLabel = false;
            foreach (Vector3 removedLabelPosition in RemovedBotsLabelPositions)
            {
                if (
                    Vector3.DistanceSquared(entityPosition, removedLabelPosition) <=
                    PracticeBotDoorwayLabelNearbyDistanceSquared
                )
                {
                    isExistingZoneLabel = true;
                    break;
                }
            }

            foreach (var zone in PracticeBotZones)
            {
                if (isExistingZoneLabel)
                    break;

                if (
                    Vector3.DistanceSquared(entityPosition, zone.LabelPosition) <=
                        PracticeBotDoorwayLabelNearbyDistanceSquared
                )
                {
                    isExistingZoneLabel = true;
                    break;
                }
            }

            if (!isExistingZoneLabel)
            {
                foreach (var zone in QueueEntryZones)
                {
                    if (
                        Vector3.DistanceSquared(entityPosition, zone.LabelPosition) <=
                            PracticeBotDoorwayLabelNearbyDistanceSquared
                        || (
                            zone.QueueType == MatchQueueType.Ranked &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(30f, 0f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                        || (
                            zone.QueueType == MatchQueueType.Ranked &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(55f, 0f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                        || (
                            zone.QueueType == null &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(-100f, 0f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                        || (
                            zone.QueueType == null &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(0f, -25f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                        || (
                            zone.QueueType == null &&
                            Vector3.DistanceSquared(
                                entityPosition,
                                zone.LabelPosition + new Vector3(25f, 0f, 0f)
                            ) <= PracticeBotDoorwayLabelNearbyDistanceSquared
                        )
                    )
                    {
                        isExistingZoneLabel = true;
                        break;
                    }
                }
            }

            if (isExistingZoneLabel)
                entity.Remove();
        }

        foreach (var zone in PracticeBotZones)
            CreateZoneLabel(zone.Label, zone.LabelColorABGR, zone.LabelPosition);
        foreach (var zone in QueueEntryZones)
        {
            CreateZoneLabel(
                zone.Label,
                zone.LabelColorABGR,
                zone.LabelPosition,
                QueueEntryLabelHorizontalPadding
            );
        }
    }

    private void CreateZoneLabel(
        string label,
        uint labelColorABGR,
        Vector3 labelPosition,
        float horizontalPadding = PracticeBotDoorwayLabelHorizontalPadding
    )
    {
        float characterWidth =
            PracticeBotDoorwayLabelFontSize *
            PracticeBotDoorwayLabelWorldUnitsPerPx *
            0.41f;
        float glyphWidth =
            PracticeBotDoorwayLabelFontSize *
            PracticeBotDoorwayLabelWorldUnitsPerPx;
        int glyphCount = Math.Max(
            1,
            (int)Math.Ceiling(
                (
                    label.Length * characterWidth +
                    horizontalPadding
                ) / glyphWidth
            )
        );
        string backgroundMessage = new(
            PracticeBotDoorwayLabelBackgroundGlyph,
            glyphCount
        );

        var backgroundText = CPointWorldText.Create(
            backgroundMessage,
            labelPosition,
            fontSize: PracticeBotDoorwayLabelFontSize,
            worldUnitsPerPx: PracticeBotDoorwayLabelWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1
        );
        if (backgroundText == null)
        {
            Console.WriteLine(
                $"[aimLOCK] Failed to create the {label} zone label background."
            );
            return;
        }

        backgroundText.ColorABGR =
            (uint)PracticeBotDoorwayLabelBackgroundAlpha << 24;
        backgroundText.Fullbright = true;
        backgroundText.DepthOffset =
            PracticeBotDoorwayLabelBackgroundDepthOffset;
        backgroundText.JustifyHorizontal = HorizontalJustify.Center;
        backgroundText.Teleport(angles: PracticeBotDoorwayLabelAngles);
        entryZoneLabelEntities.Add(backgroundText);

        var labelText = CPointWorldText.Create(
            label,
            labelPosition,
            fontSize: PracticeBotDoorwayLabelFontSize,
            worldUnitsPerPx: PracticeBotDoorwayLabelWorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1
        );
        if (labelText == null)
        {
            backgroundText.Remove();
            entryZoneLabelEntities.Remove(backgroundText);
            Console.WriteLine(
                $"[aimLOCK] Failed to create the {label} zone label."
            );
            return;
        }

        labelText.ColorABGR = labelColorABGR;
        labelText.Fullbright = true;
        labelText.JustifyHorizontal = HorizontalJustify.Center;
        labelText.Teleport(angles: PracticeBotDoorwayLabelAngles);
        entryZoneLabelEntities.Add(labelText);
    }

    public void RemoveEntryZoneLabels()
    {
        foreach (var labelEntity in entryZoneLabelEntities)
        {
            if (labelEntity is { IsValid: true })
                labelEntity.Remove();
        }

        entryZoneLabelEntities.Clear();
    }
}
