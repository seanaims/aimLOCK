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
        "tag",
        Description = "Equip or clear an admin-granted overhead tag or manage access: tag <tagname|clear|list|give|revoke>.",
        SuppressChat = true
    )]
    public void PlayerTagCommand(Caller caller, params string[] arguments)
    {
        if (arguments.Length == 0)
        {
            caller.Reply(
                "Use /tag <tagname|clear> or /tag list. Admins: /tag give|revoke <player name|SteamID64> <tagname>."
            );
            return;
        }

        if (string.Equals(arguments[0], "give", StringComparison.OrdinalIgnoreCase))
        {
            if (!caller.HasPermission("aimlock.tags.manage"))
            {
                caller.Reply("Only admins can grant overhead tag access.");
                return;
            }

            if (
                arguments.Length < 3 ||
                string.IsNullOrWhiteSpace(string.Join(" ", arguments.Skip(1).Take(arguments.Length - 2)))
            )
            {
                caller.Reply("Use /tag give <player name|SteamID64> <tagname>.");
                return;
            }

            string targetName = string.Join(
                " ",
                arguments.Skip(1).Take(arguments.Length - 2)
            ).Trim();
            if (!PlayerTagManager.TryParseTag(arguments[^1], out PlayerTag grantedTag))
            {
                caller.Reply(
                    "Choose a tag: YT, TTV, OWNER, STAFF, AIMLOCK, SUPPORTER, or GUILDTAG."
                );
                return;
            }

            if (
                !TryResolveTagTarget(
                    targetName,
                    out ulong targetSteamId,
                    out string error
                )
            )
            {
                caller.Reply(error);
                return;
            }

            try
            {
                PlayerTagManager.GrantTag(targetSteamId, grantedTag);
                caller.Reply(
                    $"Granted {PlayerTagManager.GetTagToken(grantedTag)} tag access to {targetName}. " +
                    $"They can equip it with /tag {PlayerTagManager.GetTagToken(grantedTag)}."
                );
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Failed to grant a tag to {targetName}: {ex}");
                caller.Reply("Could not save the tag grant.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Access denied granting a tag to {targetName}: {ex}");
                caller.Reply("Could not save the tag grant.");
            }

            return;
        }

        if (string.Equals(arguments[0], "revoke", StringComparison.OrdinalIgnoreCase))
        {
            if (!caller.HasPermission("aimlock.tags.manage"))
            {
                caller.Reply("Only admins can revoke overhead tag access.");
                return;
            }

            if (
                arguments.Length < 3 ||
                string.IsNullOrWhiteSpace(string.Join(" ", arguments.Skip(1).Take(arguments.Length - 2)))
            )
            {
                caller.Reply("Use /tag revoke <player name|SteamID64> <tagname>.");
                return;
            }

            string targetName = string.Join(
                " ",
                arguments.Skip(1).Take(arguments.Length - 2)
            ).Trim();
            if (!PlayerTagManager.TryParseTag(arguments[^1], out PlayerTag revokedTag))
            {
                caller.Reply(
                    "Choose a tag: YT, TTV, OWNER, STAFF, AIMLOCK, SUPPORTER, or GUILDTAG."
                );
                return;
            }

            if (
                !TryResolveTagTarget(
                    targetName,
                    out ulong targetSteamId,
                    out string error
                )
            )
            {
                caller.Reply(error);
                return;
            }

            try
            {
                if (!PlayerTagManager.RevokeTag(targetSteamId, revokedTag))
                {
                    caller.Reply(
                        $"{targetName} does not have {PlayerTagManager.GetTagToken(revokedTag)} tag access."
                    );
                    return;
                }

                caller.Reply(
                    $"Revoked {PlayerTagManager.GetTagToken(revokedTag)} tag access from {targetName}."
                );
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Failed to revoke a tag from {targetName}: {ex}");
                caller.Reply("Could not save the tag change.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Access denied revoking a tag from {targetName}: {ex}");
                caller.Reply("Could not save the tag change.");
            }

            return;
        }

        if (
            arguments.Length == 1 &&
            string.Equals(arguments[0], "list", StringComparison.OrdinalIgnoreCase)
        )
        {
            var player = caller.Player;
            if (player == null)
            {
                caller.Reply("Tag access lists are only available to players.");
                return;
            }

            caller.Reply(PlayerTagManager.GetGrantedTagList(player.PlayerSteamId));
            return;
        }

        if (arguments.Length == 1)
        {
            var player = caller.Player;
            if (player == null)
            {
                caller.Reply("Only players can equip their own overhead tags.");
                return;
            }

            if (string.Equals(arguments[0], "clear", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    PlayerTagManager.RemoveTag(player.PlayerSteamId);
                    caller.Reply("Your overhead tag was removed.");
                }
                catch (IOException ex)
                {
                    Console.WriteLine(
                        $"Failed to remove tag for {player.PlayerName}: {ex}"
                    );
                    caller.Reply("Could not save the tag change.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine(
                        $"Access denied removing tag for {player.PlayerName}: {ex}"
                    );
                    caller.Reply("Could not save the tag change.");
                }

                return;
            }

            if (!PlayerTagManager.TryParseTag(arguments[0], out PlayerTag ownTag))
            {
                caller.Reply(
                    "Use /tag <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|GUILDTAG>, /tag clear, or /tagoff."
                );
                return;
            }

            if (!PlayerTagManager.CanUseTag(player.PlayerSteamId, ownTag))
            {
                caller.Reply(
                    $"An admin must grant you {PlayerTagManager.GetTagToken(ownTag)} tag access first."
                );
                return;
            }

            if (
                ownTag == PlayerTag.GuildTag &&
                GuildManager.GetMemberTag(player.PlayerSteamId) == null
            )
            {
                caller.Reply("You must be in a guild to use your guild tag.");
                return;
            }

            try
            {
                PlayerTagManager.SetTag(player.PlayerSteamId, ownTag);
                caller.Reply(
                    $"Your overhead tag is now {PlayerTagManager.GetTagToken(ownTag)}."
                );
            }
            catch (IOException ex)
            {
                Console.WriteLine(
                    $"Failed to set tag for {player.PlayerName}: {ex}"
                );
                caller.Reply("Could not save the tag change.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine(
                    $"Access denied setting tag for {player.PlayerName}: {ex}"
                );
                caller.Reply("Could not save the tag change.");
            }

            return;
        }

        if (!caller.HasPermission("aimlock.tags.manage"))
        {
            caller.Reply(
                "Only admins can assign tags to other players. Use /tag <tagname|clear> for yourself."
            );
            return;
        }

        string normalizedName = string.Join(
            " ",
            arguments.Take(arguments.Length - 1)
        ).Trim();
        string tag = arguments[^1];
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            caller.Reply("A player name is required.");
            return;
        }

        if (
            !TryResolveTagTarget(
                normalizedName,
                out ulong steamId,
                out string resolveError
            )
        )
        {
            caller.Reply(resolveError);
            return;
        }

        if (string.Equals(tag, "clear", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                PlayerTagManager.RemoveTag(steamId);
                caller.Reply($"Removed the overhead tag for {normalizedName}.");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Failed to remove tag for {normalizedName}: {ex}");
                caller.Reply("Could not save the tag change.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Access denied removing tag for {normalizedName}: {ex}");
                caller.Reply("Could not save the tag change.");
            }

            return;
        }

        if (!PlayerTagManager.TryParseTag(tag, out PlayerTag playerTag))
        {
            caller.Reply(
                "Use /tag <player name|SteamID64> <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|GUILDTAG|clear>."
            );
            return;
        }

        try
        {
            PlayerTagManager.SetTag(steamId, playerTag);
            string displayTag = playerTag == PlayerTag.Aimlock
                ? "aimlock.xyz"
                : playerTag == PlayerTag.Supporter
                    ? "[♥]"
                    : playerTag == PlayerTag.GuildTag
                        ? "guild tag"
                        : $"[{PlayerTagManager.GetTagToken(playerTag)}]";
            caller.Reply($"Set the overhead tag for {normalizedName} to {displayTag}.");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to set tag for {normalizedName}: {ex}");
            caller.Reply("Could not save the tag change.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied setting tag for {normalizedName}: {ex}");
            caller.Reply("Could not save the tag change.");
        }
    }

    private bool TryResolveTagTarget(
        string playerName,
        out ulong steamId,
        out string error
    )
    {
        steamId = 0;
        error = "";

        CCitadelPlayerController[] matches = Players.GetAll()
            .Where(player =>
                player.IsValid &&
                !player.IsBot &&
                string.Equals(
                    player.PlayerName?.Trim(),
                    playerName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        if (matches.Length == 1)
        {
            steamId = matches[0].PlayerSteamId;
            return true;
        }

        if (matches.Length > 1)
        {
            error = $"More than one online player is named {playerName}; use their SteamID64.";
            return false;
        }

        if (
            playerName.Length == 17 &&
            ulong.TryParse(
                playerName,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out steamId
            )
        )
        {
            return true;
        }

        steamId = 0;
        error = $"No online player is named {playerName}. Use their SteamID64 if they are offline.";
        return false;
    }

    private static string GetPenaltyTimeLeft(Penalty penalty)
    {
        string text = penalty.DescribeRemaining(DateTime.UtcNow);
        return text.StartsWith("for ", StringComparison.OrdinalIgnoreCase)
            ? text[4..]
            : text;
    }

    private static string BuildMuteNotice(Penalty penalty)
    {
        string reason = string.IsNullOrWhiteSpace(penalty.Reason)
            ? "No reason provided."
            : penalty.Reason;
        return
            $"You are muted. Reason: {reason}. " +
            $"Time left: {GetPenaltyTimeLeft(penalty)}.";
    }

    [Command(
        "tagoff",
        Description = "Turn off your own overhead tag.",
        SuppressChat = true
    )]
    public void PlayerTagOffCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to turn off your overhead tag.");
            return;
        }

        try
        {
            PlayerTagManager.SetTagVisibility(player, false);
            caller.Reply("Your overhead tag has been turned off.");
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to persist tag visibility for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your tag setting.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied saving tag visibility for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your tag setting.");
        }
    }

    [Command(
        "tagon",
        Description = "Show your overhead tag again.",
        SuppressChat = true
    )]
    public void PlayerTagOnCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to show your overhead tag.");
            return;
        }

        try
        {
            PlayerTagManager.SetTagVisibility(player, true);
            caller.Reply("Your overhead tag is on.");
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to persist tag visibility for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your tag setting.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied saving tag visibility for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your tag setting.");
        }
    }
}
