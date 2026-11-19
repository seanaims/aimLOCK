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
        "duel",
        Description = "Request a duel: duel <player name> [arena name] [ls <0-100>]. In a duel: duel ls <0-100> to propose a lifesteal.",
        SuppressChat = true
    )]
    public void DuelCommand(Caller caller, params string[] arguments)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to start a duel.");
            return;
        }

        // "ls <number>" (or "lifesteal <number>") anywhere in the command sets the
        // duel's lifesteal; the rest is the player name and arena as before.
        float? lifesteal = null;
        var rest = new List<string>(arguments.Length);
        for (int i = 0; i < arguments.Length; i++)
        {
            bool isLifestealWord =
                string.Equals(arguments[i], "ls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arguments[i], "lifesteal", StringComparison.OrdinalIgnoreCase);
            if (isLifestealWord && i + 1 < arguments.Length)
            {
                if (!TryParsePercent(arguments[i + 1], out float percent))
                {
                    caller.Reply("The lifesteal must be a number from 0 to 100, for example /duel ls 100.");
                    return;
                }

                lifesteal = percent;
                i++;
                continue;
            }

            if (isLifestealWord)
            {
                caller.Reply("Add a number after ls, for example /duel ls 100.");
                return;
            }

            rest.Add(arguments[i]);
        }

        arguments = rest.ToArray();
        string targetName = string.Join(" ", arguments).Trim();

        // No player named: a lifesteal proposal for the duel the player is in.
        if (targetName.Length == 0 && lifesteal is { } proposal)
        {
            Queue.ProposeMatchLifesteal(player, proposal, message => caller.Reply(message));
            return;
        }

        int? arenaIndex = null;
        if (
            arguments.Length > 1 &&
            TryParseDuelArenaName(arguments[^1], out int requestedArena)
        )
        {
            bool exactPlayerNameExists = Players.GetAll().Any(target =>
                !target.IsBot &&
                string.Equals(
                    target.PlayerName,
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (!exactPlayerNameExists)
            {
                targetName = string.Join(
                    " ",
                    arguments.Take(arguments.Length - 1)
                ).Trim();
                arenaIndex = requestedArena;
            }
        }

        Queue.RequestDuelMatch(
            player,
            targetName,
            arenaIndex,
            message => caller.Reply(message),
            lifesteal
        );
    }

    [Command(
        "accept",
        Description = "Accept a pending duel request or a lifesteal proposal from your opponent.",
        SuppressChat = true
    )]
    public void AcceptDuelCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to accept a duel request.");
            return;
        }

        Queue.AcceptDuelRequest(player, message => caller.Reply(message));
    }

    [Command(
        "spectate",
        "spec",
        Description = "List active matches or spectate a player: spectate <list|player name>.",
        SuppressChat = true
    )]
    public void SpectateCommand(Caller caller, params string[] arguments)
    {
        PlayerManager.SpectateCommand(caller, arguments);
    }

    private bool TryParseDuelArenaSelection(
        string selection,
        out int arenaIndex
    )
    {
        if (
            int.TryParse(selection, out int arenaNumber) &&
            arenaNumber >= 1 &&
            arenaNumber <= DuelLocations.Length
        )
        {
            arenaIndex = arenaNumber - 1;
            return true;
        }

        return TryParseDuelArenaName(selection, out arenaIndex);
    }

    private bool TryParseDuelArenaName(
        string selection,
        out int arenaIndex
    )
    {
        arenaIndex = Array.FindIndex(
            DuelArenaNames,
            name => string.Equals(
                name,
                selection.Trim(),
                StringComparison.OrdinalIgnoreCase
            )
        );
        return arenaIndex >= 0 && arenaIndex < DuelLocations.Length;
    }

    [Command(
        "arena",
        Description = "List arenas or teleport to one: arena [number|name].",
        SuppressChat = true
    )]
    public void ArenaCommand(Caller caller, string? arenaSelection = null)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to use /arena.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arenaSelection))
        {
            for (int i = 0; i < DuelLocations.Length; i++)
            {
                Vector3 arena = DuelLocations[i];
                caller.Reply(
                    $"Arena {i + 1} ({DuelArenaNames[i]}): " +
                    $"{arena.X:0}, {arena.Y:0}, {arena.Z:0}"
                );
            }
            caller.Reply("Use /arena <number|name> to teleport to an arena.");
            return;
        }

        if (
            !TryParseDuelArenaSelection(arenaSelection, out int arenaIndex)
        )
        {
            caller.Reply(
                $"Choose an arena number from 1 to {DuelLocations.Length} or a listed arena name."
            );
            return;
        }

        if (!CanTeleportOutOfDuel(caller, player))
            return;

        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true })
        {
            caller.Reply("Your hero is not available to teleport.");
            return;
        }

        TeleportToArena(caller, player, pawn, arenaIndex);
    }

    private void TeleportToArena(
        Caller caller,
        CCitadelPlayerController player,
        CCitadelPlayerPawn pawn,
        int arenaIndex
    )
    {
        Vector3 destination =
            PlayerManager.GetDuelArenaCenter(DuelLocations[arenaIndex]);
        pawn.Teleport(
            destination,
            null,
            null
        );
        PlayerManager.RegisterArenaBoundaryPosition(player, destination);
        caller.Reply(
            $"Teleported to arena {arenaIndex + 1} ({DuelArenaNames[arenaIndex]})."
        );
    }

    private bool CanTeleportOutOfDuel(Caller caller, CCitadelPlayerController player)
    {
        if (PlayerManager.TryGetActiveMatch(player, out _))
        {
            caller.Reply("You cannot teleport during a match. Use /spawn to forfeit.");
            return false;
        }

        if (PlayerManager.IsSpectating(player))
        {
            caller.Reply("You cannot teleport while spectating. Use /spawn to stop.");
            return false;
        }

        return true;
    }

    [Command(
        "tp",
        Description = "Teleport to a player or arena: tp <player name|arena number|name>.",
        Permission = "aimlock.teleport",
        SuppressChat = true
    )]
    public void TeleportCommand(Caller caller, params string[] arguments)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to use /tp.");
            return;
        }

        if (arguments.Length == 0)
        {
            caller.Reply(
                "Use /tp <player name> or /tp arena <number|name>."
            );
            return;
        }

        if (!CanTeleportOutOfDuel(caller, player))
            return;

        var sourcePawn = player.GetHeroPawn();
        if (sourcePawn is not { IsValid: true })
        {
            caller.Reply("Your hero is not available to teleport.");
            return;
        }

        if (string.Equals(arguments[0], "arena", StringComparison.OrdinalIgnoreCase))
        {
            if (
                arguments.Length != 2 ||
                !TryParseDuelArenaSelection(arguments[1], out int arenaIndex)
            )
            {
                caller.Reply(
                    $"Use /tp arena <number|name> (1-{DuelLocations.Length})."
                );
                return;
            }

            TeleportToArena(caller, player, sourcePawn, arenaIndex);
            return;
        }

        string targetName = string.Join(" ", arguments).Trim();
        var matches = FindTeleportTargets(player, targetName);

        if (matches.Length == 0)
        {
            caller.Reply($"No online player matches \"{targetName}\".");
            return;
        }

        if (matches.Length > 1)
        {
            caller.Reply("That player name is ambiguous; use the full player name.");
            return;
        }

        var targetPawn = matches[0].GetHeroPawn();
        if (targetPawn is not { IsValid: true })
        {
            caller.Reply("That player's hero is not available.");
            return;
        }

        Vector3 playerDestination =
            targetPawn.Position + new Vector3(64f, 0f, 0f);
        sourcePawn.Teleport(
            playerDestination,
            null,
            null
        );
        PlayerManager.RegisterArenaBoundaryPosition(player, playerDestination);
        caller.Reply($"Teleported to {matches[0].PlayerName}.");
    }

    /* Other human players by name: exact matches win, otherwise names starting with the text */
    private static CCitadelPlayerController[] FindTeleportTargets(
        CCitadelPlayerController player,
        string targetName
    )
    {
        var onlinePlayers = Players.GetAll()
            .Where(target =>
                !target.IsBot &&
                !string.Equals(
                    target.PlayerName,
                    player.PlayerName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        var exactMatches = onlinePlayers
            .Where(target => string.Equals(
                target.PlayerName,
                targetName,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
        if (exactMatches.Length > 0)
            return exactMatches;

        return onlinePlayers
            .Where(target => target.PlayerName.StartsWith(
                targetName,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
    }
}
