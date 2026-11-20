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
    private const string TagManagePermission = "aimlock.tags.manage";

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

        if (IsTagKeyword(arguments[0], "give"))
            GrantTagAccess(caller, arguments);
        else if (IsTagKeyword(arguments[0], "revoke"))
            RevokeTagAccess(caller, arguments);
        else if (arguments.Length > 1)
            SetOtherPlayerTag(caller, arguments);
        else if (IsTagKeyword(arguments[0], "list"))
            ListOwnTagAccess(caller);
        else
            SetOwnTag(caller, arguments[0]);
    }

    private static bool IsTagKeyword(string argument, string keyword) =>
        string.Equals(argument, keyword, StringComparison.OrdinalIgnoreCase);

    /* /tag give <player name|SteamID64> <tagname> */
    private void GrantTagAccess(Caller caller, string[] arguments)
    {
        if (
            !TryParseTagAccessArguments(
                caller,
                arguments,
                "give",
                "grant",
                out string targetName,
                out ulong targetSteamId,
                out PlayerTag tag
            )
        )
        {
            return;
        }

        bool saved = TrySaveTagChange(
            caller,
            () => PlayerTagManager.GrantTag(targetSteamId, tag),
            $"grant a tag to {targetName}",
            "Could not save the tag grant."
        );
        if (!saved)
            return;

        string token = PlayerTagManager.GetTagToken(tag);
        PlayerTagManager.TryAutoEquipBestTag(targetSteamId);
        bool wearing = PlayerTagManager.IsWearing(targetSteamId, tag);
        caller.Reply(
            $"Granted {token} tag access to {targetName}. " +
            (wearing
                ? "It is now their overhead tag."
                : $"They can equip it with /tag {token}.")
        );

        // Tell the player too; until now only the admin was told.
        var target = Players.GetAll().FirstOrDefault(player =>
            player.IsValid && !player.IsBot && player.PlayerSteamId == targetSteamId
        );
        if (target != null)
        {
            Chat.PrintToChat(
                target,
                $"[aimLOCK] You were granted the {token} tag. " +
                (wearing
                    ? "It is now your overhead tag."
                    : $"Equip it with /tag {token}.")
            );
        }
    }

    /* /tag revoke <player name|SteamID64> <tagname> */
    private void RevokeTagAccess(Caller caller, string[] arguments)
    {
        if (
            !TryParseTagAccessArguments(
                caller,
                arguments,
                "revoke",
                "revoke",
                out string targetName,
                out ulong targetSteamId,
                out PlayerTag tag
            )
        )
        {
            return;
        }

        bool hadAccess = false;
        bool saved = TrySaveTagChange(
            caller,
            () => hadAccess = PlayerTagManager.RevokeTag(targetSteamId, tag),
            $"revoke a tag from {targetName}"
        );
        if (!saved)
            return;

        string token = PlayerTagManager.GetTagToken(tag);
        caller.Reply(
            hadAccess
                ? $"Revoked {token} tag access from {targetName}."
                : $"{targetName} does not have {token} tag access."
        );
    }

    private bool TryParseTagAccessArguments(
        Caller caller,
        string[] arguments,
        string subcommand,
        string verb,
        out string targetName,
        out ulong targetSteamId,
        out PlayerTag tag
    )
    {
        targetName = "";
        targetSteamId = 0;
        tag = default;

        if (!caller.HasPermission(TagManagePermission))
        {
            caller.Reply($"Only admins can {verb} overhead tag access.");
            return false;
        }

        // Everything between the subcommand and the tag is the player name.
        if (arguments.Length >= 3)
            targetName = string.Join(" ", arguments[1..^1]).Trim();
        if (targetName.Length == 0)
        {
            caller.Reply($"Use /tag {subcommand} <player name|SteamID64> <tagname>.");
            return false;
        }

        if (!PlayerTagManager.TryParseTag(arguments[^1], out tag))
        {
            caller.Reply(
                "Choose a tag: YT, TTV, OWNER, STAFF, AIMLOCK, SUPPORTER, or GUILDTAG."
            );
            return false;
        }

        if (!TryResolveTagTarget(targetName, out targetSteamId, out string error))
        {
            caller.Reply(error);
            return false;
        }

        return true;
    }

    /* /tag list */
    private void ListOwnTagAccess(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("Tag access lists are only available to players.");
            return;
        }

        caller.Reply(PlayerTagManager.GetGrantedTagList(player.PlayerSteamId));
    }

    /* /tag <tagname|clear> */
    private void SetOwnTag(Caller caller, string tagName)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("Only players can equip their own overhead tags.");
            return;
        }

        ulong steamId = player.PlayerSteamId;
        string playerName = player.PlayerName;
        if (IsTagKeyword(tagName, "clear"))
        {
            bool removed = TrySaveTagChange(
                caller,
                () => PlayerTagManager.ClearTag(steamId),
                $"remove tag for {playerName}"
            );
            if (removed)
                caller.Reply("Your overhead tag was removed.");
            return;
        }

        if (!PlayerTagManager.TryParseTag(tagName, out PlayerTag tag))
        {
            caller.Reply(
                "Use /tag <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|GUILDTAG>, /tag clear, or /tagoff."
            );
            return;
        }

        string token = PlayerTagManager.GetTagToken(tag);
        if (!PlayerTagManager.CanUseTag(steamId, tag))
        {
            caller.Reply($"An admin must grant you {token} tag access first.");
            return;
        }

        if (tag == PlayerTag.GuildTag && GuildManager.GetMemberTag(steamId) == null)
        {
            caller.Reply("You must be in a guild to use your guild tag.");
            return;
        }

        bool saved = TrySaveTagChange(
            caller,
            () => PlayerTagManager.SetTag(steamId, tag),
            $"set tag for {playerName}"
        );
        if (saved)
            caller.Reply($"Your overhead tag is now {token}.");
    }

    /* /tag <player name|SteamID64> <tagname|clear> */
    private void SetOtherPlayerTag(Caller caller, string[] arguments)
    {
        if (!caller.HasPermission(TagManagePermission))
        {
            caller.Reply(
                "Only admins can assign tags to other players. Use /tag <tagname|clear> for yourself."
            );
            return;
        }

        string targetName = string.Join(" ", arguments[..^1]).Trim();
        string tagName = arguments[^1];
        if (targetName.Length == 0)
        {
            caller.Reply("A player name is required.");
            return;
        }

        if (!TryResolveTagTarget(targetName, out ulong steamId, out string error))
        {
            caller.Reply(error);
            return;
        }

        if (IsTagKeyword(tagName, "clear"))
        {
            bool removed = TrySaveTagChange(
                caller,
                () => PlayerTagManager.ClearTag(steamId),
                $"remove tag for {targetName}"
            );
            if (removed)
                caller.Reply($"Removed the overhead tag for {targetName}.");
            return;
        }

        if (!PlayerTagManager.TryParseTag(tagName, out PlayerTag tag))
        {
            caller.Reply(
                "Use /tag <player name|SteamID64> <YT|TTV|OWNER|STAFF|AIMLOCK|SUPPORTER|GUILDTAG|clear>."
            );
            return;
        }

        bool saved = TrySaveTagChange(
            caller,
            () => PlayerTagManager.SetTag(steamId, tag),
            $"set tag for {targetName}"
        );
        if (saved)
            caller.Reply($"Set the overhead tag for {targetName} to {GetTagDisplayText(tag)}.");
    }

    private static string GetTagDisplayText(PlayerTag tag) =>
        tag switch
        {
            PlayerTag.Aimlock => "aimlock.xyz",
            PlayerTag.Supporter => "[♥]",
            PlayerTag.GuildTag => "guild tag",
            _ => $"[{PlayerTagManager.GetTagToken(tag)}]",
        };

    /* Runs a tag change that saves to disk; replies and returns false when saving fails */
    private static bool TrySaveTagChange(
        Caller caller,
        Action change,
        string description,
        string failureReply = "Could not save the tag change."
    )
    {
        try
        {
            change();
            return true;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to {description}: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Failed to {description}: {ex}");
        }

        caller.Reply(failureReply);
        return false;
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
