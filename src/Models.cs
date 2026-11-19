using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

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
