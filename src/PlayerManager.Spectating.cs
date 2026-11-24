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
    private readonly Dictionary<ulong, DuelMatch> spectatingMatches = new();

    public bool IsSpectating(CCitadelPlayerController player) =>
        this.spectatingMatches.ContainsKey(player.PlayerSteamId);

    public void SpectateCommand(Caller caller, params string[] arguments)
    {
        var matches = this.server.ActiveMatches.Values
            .Distinct()
            .Where(match => !match.Finished)
            .ToArray();

        if (
            arguments.Length == 1 &&
            string.Equals(arguments[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            this.ListActiveMatches(caller, matches);
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
            this.server.RankedQueue.Concat(this.server.UnrankedQueue).Any(entry =>
                entry.SteamId == player.PlayerSteamId))
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

        var matchingMatches = this.FindMatchesByParticipant(matches, targetName);
        if (matchingMatches.Length == 0)
        {
            caller.Reply(
                $"No active match contains a player matching \"{targetName}\". " +
                "Use /spectate list to see active matches.");
            return;
        }

        if (matchingMatches.Length > 1)
        {
            caller.Reply(
                "That name matches multiple active matches; use a full player name.");
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

        var (position, angles) = this.GetMatchSpectatorView(matchToSpectate);
        pawn.Teleport(position, angles, null);
        this.spectatingMatches[player.PlayerSteamId] = matchToSpectate;
        this.botViewerMasksDirty = true;
        pawn.AddModifier("modifier_citadel_disarmed");
        caller.Reply(
            $"Spectating {this.GetMatchParticipantDisplayName(matchToSpectate.Player1)} " +
            $"vs {this.GetMatchParticipantDisplayName(matchToSpectate.Player2)}.");
    }

    public void StopSpectating(CCitadelPlayerController player)
    {
        this.spectatingMatches.Remove(player.PlayerSteamId);
        this.botViewerMasksDirty = true;
        if (player.GetHeroPawn() is { } disarmedPawn)
        {
            RemoveAllDisarms(disarmedPawn);
        }
    }

    public void ReturnMatchSpectators(DuelMatch match)
    {
        ulong[] spectatorIds = this.spectatingMatches
            .Where(entry => ReferenceEquals(entry.Value, match))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (ulong steamId in spectatorIds)
        {
            this.spectatingMatches.Remove(steamId);
            this.botViewerMasksDirty = true;
            this.ReturnSpectatorToSpawn(steamId);
        }
    }

    private void ListActiveMatches(Caller caller, DuelMatch[] matches)
    {
        if (matches.Length == 0)
        {
            caller.Reply("There are no active matches.");
            return;
        }

        foreach (var match in matches)
        {
            string state = match.ArenaInitialized
                ? string.Empty
                : " | initializing";
            caller.Reply(
                $"{this.GetMatchParticipantDisplayName(match.Player1)} vs " +
                $"{this.GetMatchParticipantDisplayName(match.Player2)} | " +
                $"{match.Score1}-{match.Score2}{state}");
        }
    }

    /* Exact name matches win; otherwise names starting with the text */
    private DuelMatch[] FindMatchesByParticipant(DuelMatch[] matches, string targetName)
    {
        bool IsExactly(string matchKey) =>
            string.Equals(
                matchKey,
                targetName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                this.GetMatchParticipantDisplayName(matchKey),
                targetName,
                StringComparison.OrdinalIgnoreCase);
        bool StartsWith(string matchKey) =>
            matchKey.StartsWith(
                targetName,
                StringComparison.OrdinalIgnoreCase) ||
            this.GetMatchParticipantDisplayName(matchKey).StartsWith(
                targetName,
                StringComparison.OrdinalIgnoreCase);

        var exactMatches = matches
            .Where(match => IsExactly(match.Player1) || IsExactly(match.Player2))
            .ToArray();
        if (exactMatches.Length > 0)
        {
            return exactMatches;
        }

        return matches
            .Where(match => StartsWith(match.Player1) || StartsWith(match.Player2))
            .ToArray();
    }

    private string GetMatchParticipantDisplayName(string participantName)
    {
        var participant = this.FindMatchParticipant(participantName);
        if (participant == null)
        {
            return this.GetOfflineParticipantName(participantName);
        }

        return GetPracticeBotDisplayName(participant) ??
            this.server.GuildManager.FormatPlayerNameWithSelectedTag(participant);
    }

    private (Vector3 Position, Vector3 Angles) GetMatchSpectatorView(
        DuelMatch match)
    {
        Vector3 center = GetDuelArenaSphereCenter(match.ArenaPosition);
        float radius = DuelArenaSphereRadius;
        Vector3 position = center + new Vector3(0f, -radius - 128f, 180f);
        Vector3 direction = center - position;
        float horizontalDistance = MathF.Sqrt(
            (direction.X * direction.X) + (direction.Y * direction.Y));
        float pitch = MathF.Atan2(
            -direction.Z,
            horizontalDistance) * (180f / MathF.PI);
        float yaw = MathF.Atan2(
            direction.Y,
            direction.X) * (180f / MathF.PI);

        return (position, new Vector3(pitch, yaw, 0f));
    }

    private void ReturnSpectatorToSpawn(
        ulong steamId,
        int retriesRemaining = 20,
        CCitadelPlayerController? cachedPlayer = null)
    {
        var player = cachedPlayer is { IsValid: true } &&
            cachedPlayer.PlayerSteamId == steamId
                ? cachedPlayer
                : Players.GetAll().FirstOrDefault(controller =>
                    controller.PlayerSteamId == steamId);
        if (player is not { IsValid: true })
        {
            return;
        }

        if (player.GetHeroPawn() is { } disarmedPawn)
        {
            RemoveAllDisarms(disarmedPawn);
        }

        EnsureTeam(player, 2);
        var pawn = player.GetHeroPawn();
        if (pawn is not { IsValid: true, IsAlive: true })
        {
            if (retriesRemaining <= 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Could not return spectator {player.PlayerName} to spawn.");
                return;
            }

            this.server.ScheduleOnce(0.25.Seconds(), () =>
                ReturnSpectatorToSpawn(
                    steamId,
                    retriesRemaining - 1,
                    player));
            return;
        }

        pawn.Teleport(aimLOCK.MidtownSpawnPosition, null, null);
        PlayMatchTeleportEffect(pawn);
        this.server.UIManager.RestoreServerInfoAfterSpawn(player);
        Chat.PrintToChat(player, "The match ended; you have been returned to spawn.");
    }
}
