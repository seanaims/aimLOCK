using System;
using System.Collections.Generic;
using System.Linq;

using DeadworksManaged.Api;

namespace aimLOCK;

public static class PlayerLookup
{
    /* Exact name matches win; otherwise names starting with the text */
    public static CCitadelPlayerController[] FindByName(
        IEnumerable<CCitadelPlayerController> candidates,
        string name
    )
    {
        var players = candidates.ToArray();
        var exactMatches = players
            .Where(player => string.Equals(
                player.PlayerName,
                name,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
        if (exactMatches.Length > 0)
            return exactMatches;

        return players
            .Where(player => player.PlayerName.StartsWith(
                name,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
    }
}
