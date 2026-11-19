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
    private readonly Dictionary<ulong, DuelMatch> spectatingMatches = new();

    public bool IsSpectating(CCitadelPlayerController player) =>
        spectatingMatches.ContainsKey(player.PlayerSteamId);

    public void SpectateCommand(Caller caller, params string[] arguments)
    {
        var matches = server.ActiveMatches.Values
            .Distinct()
            .Where(match => !match.Finished)
            .ToArray();

        if (
            arguments.Length == 1 &&
            string.Equals(arguments[0], "list", StringComparison.OrdinalIgnoreCase)
        )
        {
            if (matches.Length == 0)
            {
                caller.Reply("There are no active matches.");
                return;
            }

            foreach (var match in matches)
            {
                string state = match.ArenaInitialized
                    ? ""
                    : " | initializing";
                caller.Reply(
                    $"{GetMatchParticipantDisplayName(match.Player1)} vs " +
                    $"{GetMatchParticipantDisplayName(match.Player2)} | " +
                    $"{match.Score1}-{match.Score2}{state}"
                );
            }
            return;
        }

        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to spectate a match.");
            return;
        }

        if (arguments.Length == 0)
        {
            caller.Reply("Use /spectate list or /spectate <player name>.");
            return;
        }

        if (TryGetActiveMatch(player, out _))
        {
            caller.Reply("You cannot spectate while you are in a match.");
            return;
        }

        if (
            server.RankedQueue.Concat(server.UnrankedQueue).Any(entry =>
                entry.SteamId == player.PlayerSteamId
            )
        )
        {
            caller.Reply("Leave your queue before spectating a match.");
            return;
        }

        string targetName = string.Join(" ", arguments).Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            caller.Reply("Use /spectate list or /spectate <player name>.");
            return;
        }

        bool MatchesParticipantExactly(string matchKey) =>
            string.Equals(
                matchKey,
                targetName,
                StringComparison.OrdinalIgnoreCase
            ) ||
            string.Equals(
                GetMatchParticipantDisplayName(matchKey),
                targetName,
                StringComparison.OrdinalIgnoreCase
            );
        bool MatchesParticipantPrefix(string matchKey)
        {
            string displayName = GetMatchParticipantDisplayName(matchKey);
            return matchKey.StartsWith(
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                displayName.StartsWith(
                    targetName,
                    StringComparison.OrdinalIgnoreCase
                );
        }

        var exactMatches = matches
            .Where(match =>
                MatchesParticipantExactly(match.Player1) ||
                MatchesParticipantExactly(match.Player2)
            )
            .ToArray();
        var matchingMatches = exactMatches.Length > 0
            ? exactMatches
            : matches
                .Where(match =>
                    MatchesParticipantPrefix(match.Player1) ||
                    MatchesParticipantPrefix(match.Player2)
                )
                .ToArray();

        if (matchingMatches.Length == 0)
        {
            caller.Reply(
                $"No active match contains a player matching \"{targetName}\". " +
                "Use /spectate list to see active matches."
            );
            return;
        }

        if (matchingMatches.Length > 1)
        {
            caller.Reply(
                "That name matches multiple active matches; use a full player name."
            );
            return;
        }

        var matchToSpectate = matchingMatches[0];
        if (!matchToSpectate.ArenaInitialized)
        {
            caller.Reply("That match is still initializing; try again shortly.");
            return;
        }

        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            caller.Reply("Your hero is not available to spectate.");
            return;
        }

        var (position, angles) = GetMatchSpectatorView(matchToSpectate);
        pawn.Teleport(position, angles, null);
        spectatingMatches[player.PlayerSteamId] = matchToSpectate;
        botViewerMasksDirty = true;
        pawn.AddModifier("modifier_citadel_disarmed");
        caller.Reply(
            $"Spectating {GetMatchParticipantDisplayName(matchToSpectate.Player1)} " +
            $"vs {GetMatchParticipantDisplayName(matchToSpectate.Player2)}."
        );
    }

    public void StopSpectating(CCitadelPlayerController player)
    {
        spectatingMatches.Remove(player.PlayerSteamId);
        botViewerMasksDirty = true;
        if (player.GetHeroPawn() is { } disarmedPawn)
            RemoveAllDisarms(disarmedPawn);
    }

    private string GetMatchParticipantDisplayName(string participantName)
    {
        var participant = FindMatchParticipant(participantName);
        if (participant == null)
            return GetOfflineParticipantName(participantName);

        return GetPracticeBotDisplayName(participant) ??
            server.GuildManager.FormatPlayerNameWithSelectedTag(participant);
    }

    private (Vector3 Position, Vector3 Angles) GetMatchSpectatorView(
        DuelMatch match
    )
    {
        Vector3 center = GetDuelArenaSphereCenter(match.ArenaPosition);
        float radius = DuelArenaSphereRadius;
        Vector3 position = center + new Vector3(0f, -radius - 128f, 180f);
        Vector3 direction = center - position;
        float horizontalDistance = MathF.Sqrt(
            direction.X * direction.X + direction.Y * direction.Y
        );
        float pitch = MathF.Atan2(
            -direction.Z,
            horizontalDistance
        ) * (180f / MathF.PI);
        float yaw = MathF.Atan2(
            direction.Y,
            direction.X
        ) * (180f / MathF.PI);

        return (position, new Vector3(pitch, yaw, 0f));
    }

    public void ReturnMatchSpectators(DuelMatch match)
    {
        ulong[] spectatorIds = spectatingMatches
            .Where(entry => ReferenceEquals(entry.Value, match))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (ulong steamId in spectatorIds)
        {
            spectatingMatches.Remove(steamId);
            botViewerMasksDirty = true;
            ReturnSpectatorToSpawn(steamId);
        }
    }

    private void ReturnSpectatorToSpawn(
        ulong steamId,
        int retriesRemaining = 20,
        CCitadelPlayerController? cachedPlayer = null
    )
    {
        var player = cachedPlayer is { IsValid: true } &&
            cachedPlayer.PlayerSteamId == steamId
                ? cachedPlayer
                : Players.GetAll().FirstOrDefault(controller =>
                    controller.PlayerSteamId == steamId
                );
        if (player is not { IsValid: true })
            return;

        if (player.GetHeroPawn() is { } disarmedPawn)
            RemoveAllDisarms(disarmedPawn);
        EnsureTeam(player, 2);
        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            if (retriesRemaining <= 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not return spectator {player.PlayerName} to spawn."
                );
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                ReturnSpectatorToSpawn(
                    steamId,
                    retriesRemaining - 1,
                    player
                ));
            return;
        }

        pawn.Teleport(aimLOCK.MidtownSpawnPosition, null, null);
        PlayMatchTeleportEffect(pawn);
        server.UIManager.RestoreServerInfoAfterSpawn(player);
        Chat.PrintToChat(player, "The match ended; you have been returned to spawn.");
    }
}
