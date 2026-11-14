using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using DeadworksManaged.Api;

namespace aimLOCK;

public sealed class GuildManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object syncRoot = new();
    private readonly string dataFilePath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "aimlock_guilds.json"
        )
    );
    private readonly RankedEloManager rankedElo;
    private List<GuildData> guilds;

    public GuildManager(RankedEloManager rankedElo)
    {
        this.rankedElo = rankedElo;
        guilds = LoadGuilds();
    }

    public string FormatPlayerName(CCitadelPlayerController player) =>
        FormatPlayerName(player.PlayerSteamId, player.PlayerName);

    public string FormatPlayerName(ulong steamId, string playerName)
    {
        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, steamId);
            return guild == null
                ? playerName
                : $"[{guild.Tag}] {playerName}";
        }
    }

    public IReadOnlyList<GuildLeaderboardEntry> GetLeaderboardEntries()
    {
        lock (syncRoot)
        {
            return guilds.Select(guild => new GuildLeaderboardEntry(
                guild.Tag,
                guild.Name,
                guild.Members.Select(member => member.SteamId).ToArray()
            )).ToArray();
        }
    }

    public void UpdatePlayerName(CCitadelPlayerController player)
    {
        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            GuildMember? member = guild?.Members.FirstOrDefault(
                candidate => candidate.SteamId == player.PlayerSteamId
            );
            if (
                member == null ||
                string.Equals(
                    member.LastKnownName,
                    player.PlayerName,
                    StringComparison.Ordinal
                )
            )
                return;

            var updatedGuilds = CloneGuilds(guilds);
            GuildMember updatedMember = FindGuildByTag(
                updatedGuilds,
                guild!.Tag
            )!.Members.First(candidate =>
                candidate.SteamId == player.PlayerSteamId);
            updatedMember.LastKnownName = player.PlayerName;
            Commit(updatedGuilds);
        }
    }

    public string ExecuteCommand(
        CCitadelPlayerController player,
        string[] arguments
    )
    {
        if (arguments.Length == 0)
            return GetHelp();

        string action = arguments[0].Trim().ToLowerInvariant();
        string target = string.Join(" ", arguments.Skip(1)).Trim();

        return action switch
        {
            "create" => Create(player, arguments.Skip(1).ToArray()),
            "join" => Join(player, target),
            "invite" => Invite(player, target),
            "accept" => AcceptInvite(player, target),
            "decline" => DeclineInvite(player, target),
            "leave" => Leave(player),
            "disband" => Disband(player, target),
            "mode" or "access" => SetAccessMode(player, target),
            "newtag" => RenameTag(player, target),
            "rename" => RenameGuild(player, target),
            "kick" => Kick(player, target),
            "transfer" => TransferOwnership(player, target),
            "lookup" or "info" => GetGuildInfo(player, target),
            "members" => GetGuildInfo(player, target, membersOnly: true),
            "invites" => GetInvites(player),
            "list" => ListGuilds(),
            "help" => GetHelp(),
            _ => GetHelp()
        };
    }

    private string Create(CCitadelPlayerController player, string[] arguments)
    {
        if (arguments.Length == 0)
            return "Usage: /guild create <tag>.";

        string tag = string.Join(" ", arguments).Trim();
        if (!IsValidTag(tag, 4))
            return "Guild tags must be 1-4 characters.";

        lock (syncRoot)
        {
            if (FindGuildForMember(guilds, player.PlayerSteamId) != null)
                return "You are already in a guild. Leave it before creating another.";
            if (guilds.Any(guild =>
                string.Equals(guild.Tag, tag, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(guild.Name, tag, StringComparison.OrdinalIgnoreCase)))
                return $"The guild tag [{tag}] is already in use.";

            var updatedGuilds = CloneGuilds(guilds);
            updatedGuilds.Add(new GuildData
            {
                Name = tag,
                Tag = tag,
                OwnerSteamId = player.PlayerSteamId,
                IsOpen = false,
                Members = new List<GuildMember>
                {
                    new()
                    {
                        SteamId = player.PlayerSteamId,
                        LastKnownName = player.PlayerName
                    }
                }
            });
            Commit(updatedGuilds);
        }

        return $"Created [{tag}]. Access is invite-only; use /guild mode open to allow anyone to join.";
    }

    private string Join(CCitadelPlayerController player, string guildQuery)
    {
        if (string.IsNullOrWhiteSpace(guildQuery))
            return "Usage: /guild join <tag>.";

        lock (syncRoot)
        {
            if (FindGuildForMember(guilds, player.PlayerSteamId) != null)
                return "You are already in a guild. Leave it before joining another.";

            GuildData? guild = FindGuild(guilds, guildQuery, out string error);
            if (guild == null)
                return error;
            if (!guild.IsOpen)
                return $"[{guild.Tag}] is invite-only. Ask its owner for an invite, then use /guild accept.";

            var updatedGuilds = CloneGuilds(guilds);
            GuildData updatedGuild = FindGuildByTag(updatedGuilds, guild.Tag)!;
            updatedGuild.Members.Add(new GuildMember
            {
                SteamId = player.PlayerSteamId,
                LastKnownName = player.PlayerName
            });
            RemoveInvites(updatedGuilds, player.PlayerSteamId);
            Commit(updatedGuilds);
            return $"You joined [{guild.Tag}].";
        }
    }

    private string Invite(CCitadelPlayerController player, string playerQuery)
    {
        if (string.IsNullOrWhiteSpace(playerQuery))
            return "Usage: /guild invite <online player name>.";

        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            if (guild == null)
                return "You are not in a guild.";
            if (guild.OwnerSteamId != player.PlayerSteamId)
                return "Only the guild owner can invite players.";

            CCitadelPlayerController? target = FindOnlinePlayer(playerQuery, out string error);
            if (target == null)
                return error;
            if (FindGuildForMember(guilds, target.PlayerSteamId) != null)
                return $"{target.PlayerName} is already in a guild.";
            if (guild.Invites.Any(invite => invite.SteamId == target.PlayerSteamId))
                return $"{target.PlayerName} already has a pending invite to [{guild.Tag}].";

            var updatedGuilds = CloneGuilds(guilds);
            FindGuildByTag(updatedGuilds, guild.Tag)!.Invites.Add(new GuildInvite
            {
                SteamId = target.PlayerSteamId,
                LastKnownName = target.PlayerName
            });
            Commit(updatedGuilds);
            return $"Invited {target.PlayerName} to [{guild.Tag}]. They can use /guild accept.";
        }
    }

    private string AcceptInvite(
        CCitadelPlayerController player,
        string guildQuery
    )
    {
        lock (syncRoot)
        {
            if (FindGuildForMember(guilds, player.PlayerSteamId) != null)
                return "You are already in a guild. Leave it before accepting another invite.";

            GuildData[] invitedGuilds = guilds
                .Where(guild => guild.Invites.Any(invite =>
                    invite.SteamId == player.PlayerSteamId))
                .ToArray();
            if (!string.IsNullOrWhiteSpace(guildQuery))
            {
                GuildData? requestedGuild = FindGuild(
                    invitedGuilds,
                    guildQuery,
                    out string error
                );
                if (requestedGuild == null)
                    return error;
                invitedGuilds = new[] { requestedGuild };
            }

            if (invitedGuilds.Length == 0)
                return "You have no pending guild invites.";
            if (invitedGuilds.Length > 1)
            {
                string choices = string.Join(
                    ", ",
                    invitedGuilds.Select(guild => $"[{guild.Tag}]")
                );
                return $"Choose an invite to accept: {choices}. Use /guild accept <tag>.";
            }

            GuildData guild = invitedGuilds[0];
            var updatedGuilds = CloneGuilds(guilds);
            GuildData updatedGuild = FindGuildByTag(updatedGuilds, guild.Tag)!;
            updatedGuild.Members.Add(new GuildMember
            {
                SteamId = player.PlayerSteamId,
                LastKnownName = player.PlayerName
            });
            RemoveInvites(updatedGuilds, player.PlayerSteamId);
            Commit(updatedGuilds);
            return $"You joined [{guild.Tag}].";
        }
    }

    private string DeclineInvite(
        CCitadelPlayerController player,
        string guildQuery
    )
    {
        lock (syncRoot)
        {
            GuildData[] invitedGuilds = guilds
                .Where(guild => guild.Invites.Any(invite =>
                    invite.SteamId == player.PlayerSteamId))
                .ToArray();
            if (!string.IsNullOrWhiteSpace(guildQuery))
            {
                GuildData? requestedGuild = FindGuild(
                    invitedGuilds,
                    guildQuery,
                    out string error
                );
                if (requestedGuild == null)
                    return error;
                invitedGuilds = new[] { requestedGuild };
            }

            if (invitedGuilds.Length == 0)
                return "You have no pending guild invites.";
            if (invitedGuilds.Length > 1)
                return "You have multiple invites. Use /guild decline <tag>.";

            GuildData guild = invitedGuilds[0];
            var updatedGuilds = CloneGuilds(guilds);
            FindGuildByTag(updatedGuilds, guild.Tag)!.Invites.RemoveAll(
                invite => invite.SteamId == player.PlayerSteamId
            );
            Commit(updatedGuilds);
            return $"Declined the invite to [{guild.Tag}].";
        }
    }

    private string Leave(CCitadelPlayerController player)
    {
        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            if (guild == null)
                return "You are not in a guild.";
            if (guild.OwnerSteamId == player.PlayerSteamId)
                return "Guild owners must transfer ownership or use /guild disband.";

            var updatedGuilds = CloneGuilds(guilds);
            FindGuildByTag(updatedGuilds, guild.Tag)!.Members.RemoveAll(
                member => member.SteamId == player.PlayerSteamId
            );
            Commit(updatedGuilds);
            return $"You left [{guild.Tag}].";
        }
    }

    private string Disband(CCitadelPlayerController player, string confirmation)
    {
        if (!string.Equals(confirmation, "confirm", StringComparison.OrdinalIgnoreCase))
            return "Disbanding is permanent. Run /guild disband confirm to confirm.";

        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            if (guild == null)
                return "You are not in a guild.";
            if (guild.OwnerSteamId != player.PlayerSteamId)
                return "Only the guild owner can disband the guild.";

            var updatedGuilds = CloneGuilds(guilds);
            updatedGuilds.RemoveAll(candidate =>
                string.Equals(candidate.Tag, guild.Tag, StringComparison.OrdinalIgnoreCase));
            Commit(updatedGuilds);
            return $"Disbanded [{guild.Tag}].";
        }
    }

    private string SetAccessMode(
        CCitadelPlayerController player,
        string requestedMode
    )
    {
        bool isOpen;
        if (string.Equals(requestedMode, "open", StringComparison.OrdinalIgnoreCase))
            isOpen = true;
        else if (
            string.Equals(requestedMode, "invite", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedMode, "invite-only", StringComparison.OrdinalIgnoreCase)
        )
            isOpen = false;
        else
            return "Usage: /guild mode <open|invite>.";

        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            if (guild == null)
                return "You are not in a guild.";
            if (guild.OwnerSteamId != player.PlayerSteamId)
                return "Only the guild owner can change guild access.";

            var updatedGuilds = CloneGuilds(guilds);
            FindGuildByTag(updatedGuilds, guild.Tag)!.IsOpen = isOpen;
            Commit(updatedGuilds);
            return $"[{guild.Tag}] is now {(isOpen ? "open to joining" : "invite-only")}.";
        }
    }

    private string RenameTag(
        CCitadelPlayerController player,
        string requestedTag
    )
    {
        string newTag = requestedTag.Trim();
        if (!IsValidTag(newTag, 4))
            return "Guild tags must be 1-4 characters.";

        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            if (guild == null)
                return "You are not in a guild.";
            if (guild.OwnerSteamId != player.PlayerSteamId)
                return "Only the guild owner can rename the guild tag.";
            if (string.Equals(guild.Tag, newTag, StringComparison.Ordinal))
                return $"Your guild tag is already [{newTag}].";
            if (guilds.Any(candidate =>
                candidate != guild &&
                (
                    string.Equals(candidate.Tag, newTag, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.Name, newTag, StringComparison.OrdinalIgnoreCase)
                )))
                return $"The guild tag [{newTag}] is already in use.";

            var updatedGuilds = CloneGuilds(guilds);
            GuildData updatedGuild = FindGuildByTag(updatedGuilds, guild.Tag)!;
            if (string.Equals(
                updatedGuild.Name,
                updatedGuild.Tag,
                StringComparison.OrdinalIgnoreCase
            ))
                updatedGuild.Name = newTag;
            updatedGuild.Tag = newTag;
            Commit(updatedGuilds);
            return $"Your guild tag is now [{newTag}].";
        }
    }

    private string RenameGuild(
        CCitadelPlayerController player,
        string requestedName
    )
    {
        string newName = requestedName.Trim();
        if (newName.Length is < 2 or > 32)
            return "Guild names must be 2-32 characters.";

        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            if (guild == null)
                return "You are not in a guild.";
            if (guild.OwnerSteamId != player.PlayerSteamId)
                return "Only the guild owner can rename the guild.";
            if (string.Equals(guild.Name, newName, StringComparison.Ordinal))
                return $"Your guild is already named {newName}.";
            if (guilds.Any(candidate =>
                candidate != guild &&
                (
                    string.Equals(candidate.Name, newName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.Tag, newName, StringComparison.OrdinalIgnoreCase)
                )))
                return $"A guild named {newName} is already in use.";

            var updatedGuilds = CloneGuilds(guilds);
            FindGuildByTag(updatedGuilds, guild.Tag)!.Name = newName;
            Commit(updatedGuilds);
            return $"Your guild is now named {newName}.";
        }
    }

    private string Kick(CCitadelPlayerController player, string memberQuery)
    {
        if (string.IsNullOrWhiteSpace(memberQuery))
            return "Usage: /guild kick <member name>.";

        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            if (guild == null)
                return "You are not in a guild.";
            if (guild.OwnerSteamId != player.PlayerSteamId)
                return "Only the guild owner can remove members.";

            GuildMember? member = FindMember(guild, memberQuery, out string error);
            if (member == null)
                return error;
            if (member.SteamId == player.PlayerSteamId)
                return "You cannot kick yourself. Use /guild transfer or /guild disband.";

            var updatedGuilds = CloneGuilds(guilds);
            FindGuildByTag(updatedGuilds, guild.Tag)!.Members.RemoveAll(
                candidate => candidate.SteamId == member.SteamId
            );
            Commit(updatedGuilds);
            return $"Removed {GetCurrentPlayerName(member)} from [{guild.Tag}].";
        }
    }

    private string TransferOwnership(
        CCitadelPlayerController player,
        string memberQuery
    )
    {
        if (string.IsNullOrWhiteSpace(memberQuery))
            return "Usage: /guild transfer <member name>.";

        lock (syncRoot)
        {
            GuildData? guild = FindGuildForMember(guilds, player.PlayerSteamId);
            if (guild == null)
                return "You are not in a guild.";
            if (guild.OwnerSteamId != player.PlayerSteamId)
                return "Only the guild owner can transfer ownership.";

            GuildMember? member = FindMember(guild, memberQuery, out string error);
            if (member == null)
                return error;
            if (member.SteamId == player.PlayerSteamId)
                return "You are already the guild owner.";

            var updatedGuilds = CloneGuilds(guilds);
            FindGuildByTag(updatedGuilds, guild.Tag)!.OwnerSteamId = member.SteamId;
            Commit(updatedGuilds);
            return $"Transferred [{guild.Tag}] ownership to {GetCurrentPlayerName(member)}.";
        }
    }

    private string GetGuildInfo(
        CCitadelPlayerController player,
        string guildQuery,
        bool membersOnly = false
    )
    {
        lock (syncRoot)
        {
            GuildData? guild;
            if (string.IsNullOrWhiteSpace(guildQuery))
            {
                guild = FindGuildForMember(guilds, player.PlayerSteamId);
                if (guild == null)
                    return "You are not in a guild. Use /guild lookup <tag>.";
            }
            else
            {
                guild = FindGuild(guilds, guildQuery, out string error);
                if (guild == null)
                    return error;
            }

            string[] members = guild.Members
                .Select(member =>
                    $"{(member.SteamId == guild.OwnerSteamId ? "[Owner] " : "")}" +
                    GetCurrentPlayerName(member))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (membersOnly)
                return $"[{guild.Tag}] {guild.Name} members ({members.Length}): " +
                    string.Join(", ", members);

            string access = guild.IsOpen ? "Open" : "Invite-only";
            int averageElo = (int)Math.Round(
                guild.Members.Average(member =>
                    (double)rankedElo.GetStats(
                        member.SteamId.ToString(CultureInfo.InvariantCulture),
                        member.LastKnownName
                    ).Rating),
                MidpointRounding.AwayFromZero
            );
            return $"[{guild.Tag}] {guild.Name}\n" +
                $"Access: {access} | Members: {members.Length}\n" +
                $"Guild ELO: {averageElo}\n" +
                $"Members: {string.Join(", ", members)}";
        }
    }

    private string GetInvites(CCitadelPlayerController player)
    {
        lock (syncRoot)
        {
            string[] invites = guilds
                .Where(guild => guild.Invites.Any(invite =>
                    invite.SteamId == player.PlayerSteamId))
                .Select(guild => $"[{guild.Tag}] {guild.Name}")
                .ToArray();
            return invites.Length == 0
                ? "You have no pending guild invites."
                : "Pending guild invites: " + string.Join(", ", invites) +
                    ". Use /guild accept <tag>.";
        }
    }

    private string ListGuilds()
    {
        lock (syncRoot)
        {
            if (guilds.Count == 0)
                return "There are no guilds yet.";

            string[] entries = guilds
                .OrderBy(guild => guild.Tag, StringComparer.OrdinalIgnoreCase)
                .Select(guild =>
                    $"[{guild.Tag}] {guild.Name} ({guild.Members.Count} members, " +
                    $"{(guild.IsOpen ? "open" : "invite-only")})")
                .ToArray();
            return "Guilds: " + string.Join("; ", entries);
        }
    }

    private static string GetHelp() =>
        "Guild commands: /guild create <tag>, " +
        "join <tag|name>, invite <player>, invites, accept [tag|name], " +
        "decline [tag|name], leave, lookup [tag|name], members [tag|name], " +
        "list, mode <open|invite>, newtag <tag>, rename <name>, kick <member>, transfer <member>, " +
        "disband confirm.";

    private static CCitadelPlayerController? FindOnlinePlayer(
        string query,
        out string error
    )
    {
        var onlinePlayers = Players.GetAll()
            .Where(player => !player.IsBot && !string.IsNullOrWhiteSpace(player.PlayerName))
            .ToArray();
        var matches = onlinePlayers
            .Where(player => string.Equals(
                player.PlayerName,
                query,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();
        if (matches.Length == 0)
        {
            matches = onlinePlayers
                .Where(player => player.PlayerName.StartsWith(
                    query,
                    StringComparison.OrdinalIgnoreCase
                ))
                .ToArray();
        }

        if (matches.Length == 1)
        {
            error = string.Empty;
            return matches[0];
        }

        error = matches.Length > 1
            ? $"More than one online player matches '{query}'; use a more specific name."
            : $"No online player matches '{query}'.";
        return null;
    }

    private static GuildMember? FindMember(
        GuildData guild,
        string query,
        out string error
    )
    {
        var matches = guild.Members
            .Where(member =>
                string.Equals(
                    GetCurrentPlayerName(member),
                    query,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                string.Equals(
                    member.LastKnownName,
                    query,
                    StringComparison.OrdinalIgnoreCase
                ))
            .ToArray();
        if (matches.Length == 0)
        {
            matches = guild.Members
                .Where(member =>
                    GetCurrentPlayerName(member).StartsWith(
                        query,
                        StringComparison.OrdinalIgnoreCase
                    ) ||
                    member.LastKnownName.StartsWith(
                        query,
                        StringComparison.OrdinalIgnoreCase
                    ))
                .ToArray();
        }

        if (matches.Length == 1)
        {
            error = string.Empty;
            return matches[0];
        }

        error = matches.Length > 1
            ? $"More than one guild member matches '{query}'; use a more specific name."
            : $"No guild member matches '{query}'.";
        return null;
    }

    private static string GetCurrentPlayerName(GuildMember member) =>
        Players.GetAll()
            .FirstOrDefault(player => player.PlayerSteamId == member.SteamId)
            ?.PlayerName
            ?? member.LastKnownName;

    private static GuildData? FindGuildForMember(
        IEnumerable<GuildData> source,
        ulong steamId
    ) =>
        source.FirstOrDefault(guild =>
            guild.Members.Any(member => member.SteamId == steamId));

    private static GuildData? FindGuild(
        IEnumerable<GuildData> source,
        string query,
        out string error
    )
    {
        GuildData[] matches = source
            .Where(guild =>
                string.Equals(guild.Tag, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(guild.Name, query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            matches = source
                .Where(guild =>
                    guild.Tag.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                    guild.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (matches.Length == 1)
        {
            error = string.Empty;
            return matches[0];
        }

        error = matches.Length > 1
            ? $"More than one guild matches '{query}'; use a more specific tag."
            : $"No guild matches '{query}'.";
        return null;
    }

    private static GuildData? FindGuildByTag(
        IEnumerable<GuildData> source,
        string tag
    ) =>
        source.FirstOrDefault(guild =>
            string.Equals(guild.Tag, tag, StringComparison.OrdinalIgnoreCase));

    private static bool IsValidTag(string tag, int maxLength)
    {
        int characterCount = StringInfo.ParseCombiningCharacters(tag).Length;
        return characterCount >= 1 &&
            characterCount <= maxLength &&
            !tag.Any(char.IsControl);
    }

    private static void RemoveInvites(
        IEnumerable<GuildData> source,
        ulong steamId
    )
    {
        foreach (GuildData guild in source)
            guild.Invites.RemoveAll(invite => invite.SteamId == steamId);
    }

    private List<GuildData> CloneGuilds(IEnumerable<GuildData> source) =>
        source.Select(guild => new GuildData
        {
            Name = guild.Name,
            Tag = guild.Tag,
            OwnerSteamId = guild.OwnerSteamId,
            IsOpen = guild.IsOpen,
            Members = guild.Members.Select(member => new GuildMember
            {
                SteamId = member.SteamId,
                LastKnownName = member.LastKnownName
            }).ToList(),
            Invites = guild.Invites.Select(invite => new GuildInvite
            {
                SteamId = invite.SteamId,
                LastKnownName = invite.LastKnownName
            }).ToList()
        }).ToList();

    private void Commit(List<GuildData> updatedGuilds)
    {
        SaveGuilds(updatedGuilds);
        guilds = updatedGuilds;
    }

    private List<GuildData> LoadGuilds()
    {
        if (!File.Exists(dataFilePath))
            return new List<GuildData>();

        using var stream = File.OpenRead(dataFilePath);
        List<GuildData> loadedGuilds =
            JsonSerializer.Deserialize<List<GuildData>>(stream, JsonOptions)
            ?? throw new InvalidDataException(
                $"Guild data is empty or invalid: {dataFilePath}"
            );

        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var memberIds = new HashSet<ulong>();
        foreach (GuildData guild in loadedGuilds)
        {
            if (
                string.IsNullOrWhiteSpace(guild.Name) ||
                !IsValidTag(guild.Tag, 6) ||
                !identifiers.Add(guild.Tag) ||
                (
                    !string.Equals(
                        guild.Name,
                        guild.Tag,
                        StringComparison.OrdinalIgnoreCase
                    ) &&
                    !identifiers.Add(guild.Name)
                ) ||
                guild.Members.Count == 0 ||
                !guild.Members.Any(member => member.SteamId == guild.OwnerSteamId)
            )
                throw new InvalidDataException(
                    $"Guild data contains an invalid guild: {guild.Name}"
                );

            foreach (GuildMember member in guild.Members)
            {
                if (
                    member.SteamId == 0 ||
                    string.IsNullOrWhiteSpace(member.LastKnownName) ||
                    !memberIds.Add(member.SteamId)
                )
                    throw new InvalidDataException(
                        $"Guild data contains an invalid or duplicate member in {guild.Name}."
                    );
            }

        }

        foreach (GuildData guild in loadedGuilds)
        {
            var invitedIds = new HashSet<ulong>();
            foreach (GuildInvite invite in guild.Invites)
            {
                if (
                    invite.SteamId == 0 ||
                    string.IsNullOrWhiteSpace(invite.LastKnownName) ||
                    !invitedIds.Add(invite.SteamId) ||
                    memberIds.Contains(invite.SteamId)
                )
                    throw new InvalidDataException(
                        $"Guild data contains an invalid invite in {guild.Name}."
                    );
            }
        }

        return loadedGuilds;
    }

    private void SaveGuilds(List<GuildData> updatedGuilds)
    {
        string? directory = Path.GetDirectoryName(dataFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                $"Could not determine the guild data directory: {dataFilePath}"
            );
        Directory.CreateDirectory(directory);

        string temporaryPath = dataFilePath + ".tmp";
        using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        ))
        {
            JsonSerializer.Serialize(stream, updatedGuilds, JsonOptions);
            stream.Flush(true);
        }

        File.Move(temporaryPath, dataFilePath, overwrite: true);
    }

    public sealed class GuildData
    {
        public string Name { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public ulong OwnerSteamId { get; set; }
        public bool IsOpen { get; set; }
        public List<GuildMember> Members { get; set; } = new();
        public List<GuildInvite> Invites { get; set; } = new();
    }

    public sealed class GuildMember
    {
        public ulong SteamId { get; set; }
        public string LastKnownName { get; set; } = string.Empty;
    }

    public sealed class GuildInvite
    {
        public ulong SteamId { get; set; }
        public string LastKnownName { get; set; } = string.Empty;
    }

    public sealed record GuildLeaderboardEntry(
        string Tag,
        string Name,
        ulong[] MemberSteamIds
    );
}
