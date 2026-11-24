namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using DeadworksManaged.Api;

/* Queue Data */

public enum MatchQueueType
{
    Ranked,
    Unranked,
}

/* Match Data */

public sealed class DuelMatch
{
    public bool CountdownActive;
    public int CountdownGeneration;
    public bool ArenaInitialized;
    public bool Finished;
    public string? ForfeitingPlayer;
    public MatchQueueType QueueType;
    public string Player1 = string.Empty;
    public string Player2 = string.Empty;

    // Duel items each player owned before the match; these are kept when it ends.
    public HashSet<string> Player1ItemsBeforeMatch = new();
    public HashSet<string> Player2ItemsBeforeMatch = new();
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
    public string LastKnownName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public bool PingBasedMatchmakingEnabled { get; set; } = true;
    public bool HandicapEnabled { get; set; }

    // The player's own duel lifesteal percent from /handicap. Null with HandicapEnabled
    // set is an old on/off handicap (10 points under the duel value).
    public float? HandicapLifestealPercent { get; set; }

    public int Wins { get; set; }
    public int Losses { get; set; }
}

public sealed record QueueEntry(ulong SteamId)
{
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
}
