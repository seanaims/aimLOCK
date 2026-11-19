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
    [Command(
        "leaderboardmove",
        Description = "Move the server leaderboard: leaderboardmove <x> <y> <z>.",
        Permission = "aimlock.leaderboard.move",
        SuppressChat = true
    )]
    public void MoveLeaderboardCommand(Caller caller, float x, float y, float z)
    {
        var position = new Vector3(x, y, z);
        try
        {
            bool moved = LeaderboardManager.MoveTo(position);
            caller.Reply(moved
                ? "Leaderboard moved and position saved."
                : "Leaderboard position saved, but its text entity is unavailable. Check the server log.");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to move the leaderboard: {ex}");
            caller.Reply("Could not save the leaderboard position.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            caller.Reply(ex.Message);
        }
    }

    [Command(
        "resetelo",
        Description = "Reset ranked Elo and wins/losses by exact name or unique name prefix.",
        Permission = "aimlock.records.reset",
        SuppressChat = true
    )]
    public void ResetEloCommand(Caller caller, string playerName)
    {
        if (!TryResolvePlayerSteamId(
            playerName,
            out ulong steamId,
            out string targetName,
            out string error
        ))
        {
            caller.Reply(error);
            return;
        }

        try
        {
            RankedElo.ResetRating(
                steamId.ToString(CultureInfo.InvariantCulture),
                targetName
            );
            LeaderboardManager.Refresh();
            caller.Reply(
                $"Reset {targetName}'s ranked Elo to {RankedEloManager.InitialRating} and ranked wins/losses."
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to reset Elo for {targetName}: {ex}");
            caller.Reply("Could not save the Elo reset.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied resetting Elo for {targetName}: {ex}");
            caller.Reply("Could not save the Elo reset.");
        }
    }

    [Command(
        "whois",
        Description = "Look up a player's SteamID64 by exact name or unique name prefix.",
        Permission = "aimlock.whois",
        SuppressChat = true
    )]
    public void WhoIsCommand(Caller caller, string playerName)
    {
        if (!TryResolvePlayerSteamId(
            playerName,
            out ulong steamId,
            out string targetName,
            out string error
        ))
        {
            caller.Reply(error);
            return;
        }

        bool online = Players.GetAll()
            .Any(player => !player.IsBot && player.PlayerSteamId == steamId);
        caller.Reply(
            $"{targetName}: {steamId.ToString(CultureInfo.InvariantCulture)} " +
            $"({(online ? "online" : "offline")})"
        );
    }

    [Command(
        "resetrankedrecord",
        "resetranked",
        Description = "Reset ranked wins/losses by exact name or unique name prefix.",
        Permission = "aimlock.records.reset",
        SuppressChat = true
    )]
    public void ResetRankedRecordCommand(Caller caller, string playerName)
    {
        if (!TryResolvePlayerSteamId(
            playerName,
            out ulong steamId,
            out string targetName,
            out string error
        ))
        {
            caller.Reply(error);
            return;
        }

        try
        {
            RankedElo.ResetRecord(
                steamId.ToString(CultureInfo.InvariantCulture),
                targetName
            );
            LeaderboardManager.Refresh();
            caller.Reply(
                $"Reset {targetName}'s ranked wins/losses; Elo was preserved."
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to reset ranked record for {targetName}: {ex}");
            caller.Reply("Could not save the ranked record reset.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied resetting ranked record for {targetName}: {ex}"
            );
            caller.Reply("Could not save the ranked record reset.");
        }
    }

    [Command(
        "resetunrankedrecord",
        "resetunranked",
        Description = "Reset unranked wins/losses by exact name or unique name prefix.",
        Permission = "aimlock.records.reset",
        SuppressChat = true
    )]
    public void ResetUnrankedRecordCommand(Caller caller, string playerName)
    {
        if (!TryResolvePlayerSteamId(
            playerName,
            out ulong steamId,
            out string targetName,
            out string error
        ))
        {
            caller.Reply(error);
            return;
        }

        try
        {
            PlayerManager.ResetUnrankedRecord(steamId, targetName);
            LeaderboardManager.Refresh();
            caller.Reply($"Reset {targetName}'s unranked wins/losses.");
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to reset unranked record for {targetName}: {ex}"
            );
            caller.Reply("Could not save the unranked record reset.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied resetting unranked record for {targetName}: {ex}"
            );
            caller.Reply("Could not save the unranked record reset.");
        }
    }

    internal bool TryResolvePlayerSteamId(
        string playerName,
        out ulong steamId,
        out string targetName,
        out string error)
    {
        steamId = 0;
        targetName = playerName?.Trim() ?? "";
        error = "";
        if (string.IsNullOrWhiteSpace(targetName))
        {
            error = "A player name is required.";
            return false;
        }

        string lookupName = targetName;
        var onlinePlayers = Players.GetAll()
            .Where(player => !player.IsBot)
            .ToArray();

        // A SteamID64 names one player exactly, whatever they are called now.
        if (lookupName.Length == 17 &&
            ulong.TryParse(
                lookupName,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong directSteamId
            ))
        {
            var onlinePlayer = onlinePlayers
                .FirstOrDefault(player => player.PlayerSteamId == directSteamId);
            if (onlinePlayer != null)
            {
                steamId = directSteamId;
                targetName = onlinePlayer.PlayerName;
                return true;
            }

            if (PlayerRecords.TryGetValue(directSteamId, out var recordById))
            {
                steamId = directSteamId;
                targetName = recordById.LastKnownName;
                return true;
            }
        }

        ulong[] matchingIds = FindMatchingIds(exactMatch: true);
        bool usedPrefixMatch = false;
        if (matchingIds.Length == 0)
        {
            matchingIds = FindMatchingIds(exactMatch: false);
            usedPrefixMatch = true;
        }

        if (matchingIds.Length == 1)
        {
            ulong resolvedSteamId = matchingIds[0];
            steamId = resolvedSteamId;
            targetName = onlinePlayers
                .FirstOrDefault(player => player.PlayerSteamId == resolvedSteamId)
                ?.PlayerName
                ?? (PlayerRecords.TryGetValue(resolvedSteamId, out var savedRecord)
                    ? savedRecord.LastKnownName
                    : lookupName);
            return true;
        }

        error = matchingIds.Length > 1
            ? usedPrefixMatch
                ? $"More than one player name starts with '{lookupName}'; use a more specific name."
                : $"More than one player is named '{lookupName}'."
            : $"No online player or saved record found for '{targetName}'.";
        return false;

        ulong[] FindMatchingIds(bool exactMatch)
        {
            bool Matches(string? name) =>
                !string.IsNullOrWhiteSpace(name) &&
                (exactMatch
                    ? string.Equals(name, lookupName, StringComparison.OrdinalIgnoreCase)
                    : name.StartsWith(lookupName, StringComparison.OrdinalIgnoreCase));

            IEnumerable<ulong> ids = onlinePlayers
                .Where(player => Matches(player.PlayerName))
                .Select(player => player.PlayerSteamId)
                .Concat(PlayerRecords.Values
                    .Where(record => Matches(record.LastKnownName))
                    .Select(record => record.SteamId));

            IEnumerable<string> rankedIds = exactMatch
                ? RankedElo.FindPlayerIdsByName(lookupName)
                : RankedElo.FindPlayerIdsStartingWith(lookupName);

            return ids
                .Concat(rankedIds.Select(id =>
                    ulong.TryParse(
                        id,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out ulong parsedId
                    ) ? parsedId : 0))
                .Where(id => id != 0 && id != ulong.MaxValue)
                .Distinct()
                .ToArray();
        }
    }
}
