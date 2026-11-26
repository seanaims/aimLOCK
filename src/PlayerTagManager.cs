namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DeadworksManaged.Api;

public enum PlayerTag
{
    YouTube,
    Twitch,
    Owner,
    Staff,
    Aimlock,
    Supporter,
    GuildTag,
}

public sealed class PlayerTagManager
{
    public const string TagsFileName = "player-tags.json";
    public const string StableTagsFileName = "player-tags-by-id.json";
    public const string GrantedTagsFileName = "player-tag-grants.json";
    public const string VisibilityFileName = "player-tag-visibility.json";
    public const string StableVisibilityFileName = "player-tag-visibility-by-id.json";
    public const string ClearedTagsFileName = "player-tag-cleared-by-id.json";

    private const int PawnRetries = 8;
    private const float TagHeight = 100f;
    private const float FontSize = 64f;
    private const float WorldUnitsPerPx = 0.2f;
    private const float TagBackgroundPadding = 8f;
    private const int TagBackgroundRows = 1;
    private const byte TagBackgroundAlpha = 160;
    private const char TagBackgroundGlyph = '\u2588';

    // Stands the text upright; billboard mode then only turns it around the up axis.
    private static readonly Vector3 UprightTextAngles = new(180f, 90f, 270f);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    // Best first: the tag a player gets automatically when they have not chosen one.
    private static readonly PlayerTag[] TagPriority =
    {
        PlayerTag.Owner,
        PlayerTag.Staff,
        PlayerTag.Aimlock,
        PlayerTag.YouTube,
        PlayerTag.Twitch,
        PlayerTag.Supporter,
        PlayerTag.GuildTag,
    };

    private readonly aimLOCK server;
    private readonly string tagsFilePath = JsonStore.PathFor(TagsFileName);
    private readonly string stableTagsFilePath = JsonStore.PathFor(StableTagsFileName);
    private readonly string grantedTagsFilePath = JsonStore.PathFor(GrantedTagsFileName);
    private readonly string visibilityFilePath = JsonStore.PathFor(VisibilityFileName);
    private readonly string stableVisibilityFilePath =
        JsonStore.PathFor(StableVisibilityFileName);
    private readonly string clearedTagsFilePath = JsonStore.PathFor(ClearedTagsFileName);

    private readonly Dictionary<string, PlayerTag> tags =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<ulong, PlayerTag> stableTags = new();
    private readonly Dictionary<ulong, HashSet<PlayerTag>> grantedTags = new();
    private readonly Dictionary<ulong, CPointWorldText> activeTags = new();
    private readonly Dictionary<ulong, CPointWorldText> activeTagBackgrounds = new();
    private readonly Dictionary<ulong, PlayerTag> activeTagValues = new();
    private readonly Dictionary<ulong, uint> activeTagPawnHandles = new();
    private readonly Dictionary<ulong, int> refreshVersions = new();
    private readonly HashSet<string> hiddenTags =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<ulong> hiddenSteamIds = new();

    // Players who removed their tag on purpose; they are not given one automatically.
    private readonly HashSet<ulong> clearedSteamIds = new();

    public PlayerTagManager(aimLOCK server)
    {
        this.server = server;
        this.LoadTags();
        this.LoadHiddenTags();
        this.LoadStableTags();
        this.LoadGrantedTags();
        this.LoadHiddenSteamIds();
        this.LoadClearedSteamIds();
    }

    public static bool TryParseTag(string tagName, out PlayerTag tag)
    {
        tag = tagName.Trim().ToUpperInvariant() switch
        {
            "YT" => PlayerTag.YouTube,
            "TTV" => PlayerTag.Twitch,
            "OWNER" => PlayerTag.Owner,
            "STAFF" => PlayerTag.Staff,
            "AIMLOCK" => PlayerTag.Aimlock,
            "SUPPORTER" => PlayerTag.Supporter,
            "GUILD" or "GUILDTAG" => PlayerTag.GuildTag,
            _ => (PlayerTag)(-1),
        };
        return Enum.IsDefined(typeof(PlayerTag), tag);
    }

    public static string GetTagToken(PlayerTag tag) =>
        tag switch
        {
            PlayerTag.YouTube => "YT",
            PlayerTag.Twitch => "TTV",
            PlayerTag.Owner => "OWNER",
            PlayerTag.Staff => "STAFF",
            PlayerTag.Aimlock => "AIMLOCK",
            PlayerTag.Supporter => "SUPPORTER",
            PlayerTag.GuildTag => "GUILDTAG",
            _ => throw new ArgumentOutOfRangeException(nameof(tag)),
        };

    public void Initialize()
    {
        this.MigrateNameKeyedTags();

        foreach (var player in Players.GetAll())
        {
            RefreshPlayerTag(player);
        }
    }

    public void SetTag(ulong steamId, PlayerTag tag)
    {
        var updatedStableTags = new Dictionary<ulong, PlayerTag>(this.stableTags)
        {
            [steamId] = tag,
        };

        this.SaveStableTags(updatedStableTags);
        this.ReplaceStableTags(updatedStableTags);
        this.SetTagCleared(steamId, false);
        this.RefreshOnlinePlayer(steamId);
    }

    /* A deliberate removal: the player is not given a tag automatically afterwards */
    public void ClearTag(ulong steamId)
    {
        this.RemoveTag(steamId);
        this.SetTagCleared(steamId, true);
    }

    public bool RemoveTag(ulong steamId)
    {
        var updatedStableTags = new Dictionary<ulong, PlayerTag>(this.stableTags);
        if (!updatedStableTags.Remove(steamId))
        {
            return false;
        }

        this.SaveStableTags(updatedStableTags);
        this.ReplaceStableTags(updatedStableTags);
        this.RefreshOnlinePlayer(steamId);
        return true;
    }

    public void GrantTag(ulong steamId, PlayerTag tag)
    {
        var updatedGrants = this.grantedTags.ToDictionary(
            entry => entry.Key,
            entry => new HashSet<PlayerTag>(entry.Value));
        if (!updatedGrants.TryGetValue(steamId, out var playerTags))
        {
            playerTags = new HashSet<PlayerTag>();
            updatedGrants[steamId] = playerTags;
        }

        playerTags.Add(tag);
        this.SaveGrantedTags(updatedGrants);
        this.ReplaceGrantedTags(updatedGrants);
    }

    public bool RevokeTag(ulong steamId, PlayerTag tag)
    {
        var updatedGrants = this.grantedTags.ToDictionary(
            entry => entry.Key,
            entry => new HashSet<PlayerTag>(entry.Value));
        if (
            !updatedGrants.TryGetValue(steamId, out var playerTags) ||
            !playerTags.Remove(tag))
        {
            return false;
        }

        if (playerTags.Count == 0)
        {
            updatedGrants.Remove(steamId);
        }

        this.SaveGrantedTags(updatedGrants);
        this.ReplaceGrantedTags(updatedGrants);

        if (
            this.stableTags.TryGetValue(steamId, out PlayerTag equippedTag) &&
            equippedTag == tag)
        {
            this.RemoveTag(steamId);
            this.TryAutoEquipBestTag(steamId);
        }

        return true;
    }

    /* Tags the player may wear, best first */
    public PlayerTag[] GetUsableTags(ulong steamId)
    {
        bool inGuild = this.server.GuildManager.GetMemberTag(steamId) != null;
        return TagPriority
            .Where(tag =>
                tag == PlayerTag.GuildTag ? inGuild : this.CanUseTag(steamId, tag))
            .ToArray();
    }

    public bool IsWearing(ulong steamId, PlayerTag tag) =>
        this.stableTags.TryGetValue(steamId, out PlayerTag equippedTag) && equippedTag == tag;

    /* Gives the best usable tag to a player who has none and never removed theirs */
    public bool TryAutoEquipBestTag(ulong steamId)
    {
        if (this.stableTags.ContainsKey(steamId) || this.clearedSteamIds.Contains(steamId))
        {
            return false;
        }

        PlayerTag[] usableTags = this.GetUsableTags(steamId);
        if (usableTags.Length == 0)
        {
            return false;
        }

        try
        {
            this.SetTag(steamId, usableTags[0]);
            return true;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to auto-equip a tag for {steamId}: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Failed to auto-equip a tag for {steamId}: {ex}");
        }

        return false;
    }

    /* Join hint, only for players wearing a visible tag who have others to choose from */
    public string? GetTagHint(ulong steamId)
    {
        if (
            this.hiddenSteamIds.Contains(steamId) ||
            !this.stableTags.TryGetValue(steamId, out PlayerTag equippedTag))
        {
            return null;
        }

        string[] otherTags = this.GetUsableTags(steamId)
            .Where(tag => tag != equippedTag)
            .Select(GetTagToken)
            .ToArray();
        if (otherTags.Length == 0)
        {
            return null;
        }

        return
            $"[aimLOCK] Your overhead tag is {GetTagToken(equippedTag)}. " +
            $"You also have: {string.Join(", ", otherTags)}. Change it with /tag <name>.";
    }

    // Guild members can always wear their own guild's tag; every other tag is granted.
    public bool CanUseTag(ulong steamId, PlayerTag tag) =>
        (tag == PlayerTag.GuildTag &&
            this.server.GuildManager.GetMemberTag(steamId) != null) ||
        (this.grantedTags.TryGetValue(steamId, out var playerTags) &&
            playerTags.Contains(tag));

    public string FormatPlayerNameWithSelectedTag(
        ulong steamId,
        string playerName,
        string? guildTag)
    {
        if (!this.stableTags.TryGetValue(steamId, out PlayerTag tag))
        {
            return playerName;
        }

        if (tag == PlayerTag.GuildTag)
        {
            return string.IsNullOrWhiteSpace(guildTag)
                ? playerName
                : $"[{guildTag}] {playerName}";
        }

        string overheadTag = tag switch
        {
            PlayerTag.YouTube => "[YT]",
            PlayerTag.Twitch => "[TTV]",
            PlayerTag.Owner => "[OWNER]",
            PlayerTag.Staff => "[STAFF]",
            PlayerTag.Aimlock => "[aimlock.xyz]",
            PlayerTag.Supporter => "[♥]",
            _ => throw new ArgumentOutOfRangeException(nameof(tag)),
        };
        return $"{overheadTag} {playerName}";
    }

    public string GetGrantedTagList(ulong steamId)
    {
        var usableTags = this.grantedTags.TryGetValue(steamId, out var playerTags)
            ? new HashSet<PlayerTag>(playerTags)
            : new HashSet<PlayerTag>();
        if (this.server.GuildManager.GetMemberTag(steamId) != null)
        {
            usableTags.Add(PlayerTag.GuildTag);
        }
        else
        {
            usableTags.Remove(PlayerTag.GuildTag);
        }

        if (usableTags.Count == 0)
        {
            return "No overhead tags have been granted to you yet.";
        }

        string[] names = usableTags
            .Select(GetTagToken)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return "Tags you can use: " + string.Join(", ", names) + ".";
    }

    public void RefreshGuildTags()
    {
        foreach (var player in Players.GetAll())
        {
            // A player with no tag who just joined a guild gets the guild tag.
            if (!player.IsBot)
            {
                TryAutoEquipBestTag(player.PlayerSteamId);
            }

            RefreshPlayerTag(player);
        }
    }

    public void RefreshPlayerTag(CCitadelPlayerController player)
    {
        ulong steamId = player.PlayerSteamId;
        int version = this.refreshVersions.TryGetValue(steamId, out int currentVersion)
            ? currentVersion + 1
            : 1;
        this.refreshVersions[steamId] = version;
        this.RefreshPlayerTag(player, version, PawnRetries);
    }

    public void SetTagVisibility(CCitadelPlayerController player, bool visible)
    {
        var updatedHiddenSteamIds = new HashSet<ulong>(this.hiddenSteamIds);

        if (visible)
        {
            updatedHiddenSteamIds.Remove(player.PlayerSteamId);
        }
        else
        {
            updatedHiddenSteamIds.Add(player.PlayerSteamId);
        }

        this.SaveHiddenSteamIds(updatedHiddenSteamIds);
        this.hiddenSteamIds.Clear();
        foreach (ulong hiddenSteamId in updatedHiddenSteamIds)
        {
            this.hiddenSteamIds.Add(hiddenSteamId);
        }

        this.RefreshPlayerTag(player);
    }

    /* On unload: a reloaded plugin starts without knowing these entities */
    public void RemoveAllTagEntities()
    {
        foreach (ulong steamId in this.activeTags.Keys.Concat(this.activeTagBackgrounds.Keys).ToArray())
        {
            this.RemoveTagEntity(steamId);
        }
    }

    /* Text above the pawn's head: it follows the pawn and turns to face every viewer */
    private static CPointWorldText? CreateOverheadText(
        CCitadelPlayerPawn pawn,
        string text,
        uint color)
    {
        Vector3 headPosition = pawn.Position + new Vector3(0f, 0f, TagHeight);
        var entity = CPointWorldText.Create(
            text,
            headPosition,
            fontSize: FontSize,
            worldUnitsPerPx: WorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 1);
        if (entity == null)
        {
            return null;
        }

        entity.ColorABGR = color;
        entity.JustifyHorizontal = HorizontalJustify.Center;
        entity.SetParent(pawn);
        entity.Teleport(
            position: headPosition,
            angles: UprightTextAngles);
        return entity;
    }

    /* A row of block glyphs wide enough to sit behind the tag text */
    private static string BuildBackgroundRow(string tagText)
    {
        // 0.41 is the average character width of the font relative to its size.
        float textWidth = tagText.Length * FontSize * WorldUnitsPerPx * 0.41f;
        float glyphWidth = FontSize * WorldUnitsPerPx;
        int glyphCount = Math.Max(
            1,
            (int)Math.Ceiling((textWidth + (TagBackgroundPadding * 2f)) / glyphWidth));
        string row = new(TagBackgroundGlyph, glyphCount);
        return string.Join("\n", Enumerable.Repeat(row, TagBackgroundRows));
    }

    private static void BackUpLegacyFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.Copy(
            path,
            $"{path}.migrated-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            overwrite: false);
    }

    private static string NormalizePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            throw new ArgumentException("A player name is required.", nameof(playerName));
        }

        return playerName.Trim();
    }

    private void RefreshPlayerTag(
        CCitadelPlayerController player,
        int version,
        int retriesRemaining)
    {
        if (!player.IsValid)
        {
            return;
        }

        ulong steamId = player.PlayerSteamId;

        if (
            this.hiddenSteamIds.Contains(steamId) ||
            !this.stableTags.TryGetValue(steamId, out PlayerTag tag) ||
            this.GetTagStyle(tag, steamId) is not { } style)
        {
            this.RemoveTagEntity(steamId);
            return;
        }

        var pawn = player.GetHeroPawn();
        if (pawn == null || !pawn.IsValid)
        {
            this.RemoveTagEntity(steamId);
            if (retriesRemaining > 0)
            {
                this.ScheduleTagRetry(player, steamId, version, retriesRemaining - 1);
            }

            return;
        }

        if (HasCurrentTagEntities(steamId, tag, pawn))
        {
            return;
        }

        this.RemoveTagEntity(steamId);
        CreateTagEntities(steamId, player.PlayerName, pawn, tag, style);
    }

    /* The hero pawn is not ready yet: try again unless a newer refresh started */
    private void ScheduleTagRetry(
        CCitadelPlayerController player,
        ulong steamId,
        int version,
        int retriesRemaining)
    {
        this.server.ScheduleOnce(0.5.Seconds(), () =>
        {
            if (
                this.refreshVersions.TryGetValue(steamId, out int activeVersion) &&
                activeVersion == version)
            {
                this.RefreshPlayerTag(player, version, retriesRemaining);
            }
        });
    }

    /* The same tag on the same pawn keeps its entities; guild tags are always rebuilt */
    private bool HasCurrentTagEntities(
        ulong steamId,
        PlayerTag tag,
        CCitadelPlayerPawn pawn) =>
        tag != PlayerTag.GuildTag &&
        this.activeTagValues.TryGetValue(steamId, out PlayerTag activeTag) &&
        activeTag == tag &&
        this.activeTagPawnHandles.TryGetValue(steamId, out uint activePawnHandle) &&
        activePawnHandle == pawn.EntityHandle &&
        this.activeTagBackgrounds.TryGetValue(steamId, out var background) &&
        background.IsValid &&
        this.activeTags.TryGetValue(steamId, out var text) &&
        text.IsValid;

    private void CreateTagEntities(
        ulong steamId,
        string playerName,
        CCitadelPlayerPawn pawn,
        PlayerTag tag,
        TagStyle style)
    {
        var background = CreateOverheadText(
            pawn,
            BuildBackgroundRow(style.Text),
            (uint)TagBackgroundAlpha << 24);
        if (background == null)
        {
            Console.WriteLine(
                $"[aimLOCK] Failed to create the background for {playerName}'s overhead tag.");
            return;
        }

        background.Fullbright = true;
        background.DepthOffset = -1f;

        var text = CreateOverheadText(
            pawn,
            style.Text,
            style.Color);
        if (text == null)
        {
            background.Remove();
            Console.WriteLine(
                $"[aimLOCK] Failed to create the overhead tag for {playerName}.");
            return;
        }

        this.activeTags[steamId] = text;
        this.activeTagBackgrounds[steamId] = background;
        this.activeTagValues[steamId] = tag;
        this.activeTagPawnHandles[steamId] = pawn.EntityHandle;
    }

    private TagStyle? GetTagStyle(PlayerTag tag, ulong steamId)
    {
        if (tag == PlayerTag.GuildTag)
        {
            string? guildTag = this.server.GuildManager.GetMemberTag(steamId);
            return string.IsNullOrWhiteSpace(guildTag)
                ? null
                : new TagStyle($"[{guildTag}]", 0xFFFFFFFF);
        }

        // Colors are ABGR.
        return tag switch
        {
            PlayerTag.YouTube => new TagStyle("[YT]", 0xFF0000FF),
            PlayerTag.Twitch => new TagStyle("[TTV]", 0xFFF755A8),
            PlayerTag.Owner => new TagStyle("[OWNER]", 0xFF00D7FF),
            PlayerTag.Staff => new TagStyle("[STAFF]", 0xFF00FFFF),
            PlayerTag.Aimlock => new TagStyle("[aimlock.xyz]", 0xFFFFFFFF),
            PlayerTag.Supporter => new TagStyle("[♥]", 0xFFB469FF),
            _ => throw new ArgumentOutOfRangeException(nameof(tag)),
        };
    }

    private void RefreshOnlinePlayer(ulong steamId)
    {
        foreach (var player in Players.GetAll())
        {
            if (player.PlayerSteamId == steamId)
            {
                RefreshPlayerTag(player);
            }
        }
    }

    private ulong[] ResolveLegacyName(
        string playerName,
        IEnumerable<CCitadelPlayerController> onlinePlayers) =>
        onlinePlayers
            .Where(player => !player.IsBot && string.Equals(
                player.PlayerName?.Trim(),
                playerName,
                StringComparison.OrdinalIgnoreCase))
            .Select(player => player.PlayerSteamId)
            .Concat(this.server.PlayerRecords.Values
                .Where(record =>
                    !string.IsNullOrWhiteSpace(record.LastKnownName) &&
                    string.Equals(
                        record.LastKnownName.Trim(),
                        playerName,
                        StringComparison.OrdinalIgnoreCase))
                .Select(record => record.SteamId))
            .Where(steamId => steamId != 0)
            .Distinct()
            .ToArray();

    /* Name-keyed tag migration */

    private void MigrateNameKeyedTags()
    {
        if (this.tags.Count == 0 && this.hiddenTags.Count == 0)
        {
            return;
        }

        var onlinePlayers = Players.GetAll().ToArray();
        var updatedStableTags = new Dictionary<ulong, PlayerTag>(this.stableTags);
        var updatedHiddenSteamIds = new HashSet<ulong>(this.hiddenSteamIds);

        foreach (var entry in this.tags)
        {
            ulong[] steamIds = ResolveLegacyName(entry.Key, onlinePlayers);
            if (steamIds.Length != 1)
            {
                Console.WriteLine(
                    $"Failed to migrate the {GetTagToken(entry.Value)} tag for \"{entry.Key}\": " +
                    $"{steamIds.Length} players match that name. Set it again with /tag.");
                continue;
            }

            updatedStableTags.TryAdd(steamIds[0], entry.Value);
        }

        foreach (string playerName in this.hiddenTags)
        {
            ulong[] steamIds = ResolveLegacyName(playerName, onlinePlayers);
            if (steamIds.Length == 1)
            {
                updatedHiddenSteamIds.Add(steamIds[0]);
            }
        }

        try
        {
            BackUpLegacyFile(this.tagsFilePath);
            BackUpLegacyFile(this.visibilityFilePath);
            this.SaveStableTags(updatedStableTags);
            this.SaveHiddenSteamIds(updatedHiddenSteamIds);
            this.SaveTags(new Dictionary<string, PlayerTag>());
            this.SaveHiddenTags(new HashSet<string>());
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to migrate name-keyed player tags: {ex}");
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied migrating name-keyed player tags: {ex}");
            return;
        }

        this.ReplaceStableTags(updatedStableTags);
        this.hiddenSteamIds.Clear();
        foreach (ulong steamId in updatedHiddenSteamIds)
        {
            this.hiddenSteamIds.Add(steamId);
        }

        this.tags.Clear();
        this.hiddenTags.Clear();

        Console.WriteLine("[aimLOCK] Overhead tags are now saved by SteamID. Current holders:");
        foreach (var entry in this.stableTags)
        {
            string name = this.server.PlayerRecords.TryGetValue(entry.Key, out var record)
                ? record.LastKnownName
                : "unknown";
            Console.WriteLine(
                $"[aimLOCK]   {entry.Key} ({name}): {GetTagToken(entry.Value)}");
        }
    }

    private void RemoveTagEntity(ulong steamId)
    {
        if (
            this.activeTagBackgrounds.Remove(steamId, out var backgroundText) &&
            backgroundText.IsValid)
        {
            backgroundText.Remove();
        }

        if (this.activeTags.Remove(steamId, out var text) && text.IsValid)
        {
            text.Remove();
        }

        this.activeTagValues.Remove(steamId);
        this.activeTagPawnHandles.Remove(steamId);
    }

    private void ReplaceStableTags(Dictionary<ulong, PlayerTag> updatedTags)
    {
        this.stableTags.Clear();
        foreach (var entry in updatedTags)
        {
            this.stableTags[entry.Key] = entry.Value;
        }
    }

    private void ReplaceGrantedTags(
        Dictionary<ulong, HashSet<PlayerTag>> updatedGrants)
    {
        this.grantedTags.Clear();
        foreach (var entry in updatedGrants)
        {
            this.grantedTags[entry.Key] = entry.Value;
        }
    }

    private void LoadTags()
    {
        var loaded = JsonStore.Load(
            this.tagsFilePath,
            () => new Dictionary<string, PlayerTag>(),
            JsonOptions);
        foreach (var entry in loaded)
        {
            if (Enum.IsDefined(typeof(PlayerTag), entry.Value))
            {
                this.tags[NormalizePlayerName(entry.Key)] = entry.Value;
            }
        }
    }

    private void LoadHiddenTags()
    {
        var loaded = JsonStore.Load(
            this.visibilityFilePath,
            () => new HashSet<string>(),
            JsonOptions);
        foreach (string playerName in loaded)
        {
            if (!string.IsNullOrWhiteSpace(playerName))
            {
                this.hiddenTags.Add(NormalizePlayerName(playerName));
            }
        }
    }

    private void LoadStableTags()
    {
        var loaded = JsonStore.Load(
            this.stableTagsFilePath,
            () => new Dictionary<ulong, PlayerTag>(),
            JsonOptions);
        foreach (var entry in loaded)
        {
            if (Enum.IsDefined(typeof(PlayerTag), entry.Value))
            {
                this.stableTags[entry.Key] = entry.Value;
            }
        }
    }

    private void LoadGrantedTags()
    {
        var loaded = JsonStore.Load(
            this.grantedTagsFilePath,
            () => new Dictionary<ulong, HashSet<PlayerTag>>(),
            JsonOptions);
        foreach (var entry in loaded)
        {
            if (entry.Key == 0 || entry.Key == ulong.MaxValue || entry.Value == null)
            {
                continue;
            }

            PlayerTag[] validTags = entry.Value
                .Where(tag => Enum.IsDefined(typeof(PlayerTag), tag))
                .Distinct()
                .ToArray();
            if (validTags.Length > 0)
            {
                this.grantedTags[entry.Key] = new HashSet<PlayerTag>(validTags);
            }
        }
    }

    private void LoadHiddenSteamIds()
    {
        var loaded = JsonStore.Load(
            this.stableVisibilityFilePath,
            () => new HashSet<ulong>(),
            JsonOptions);
        foreach (ulong steamId in loaded)
        {
            this.hiddenSteamIds.Add(steamId);
        }
    }

    private void LoadClearedSteamIds()
    {
        var loaded = JsonStore.Load(
            this.clearedTagsFilePath,
            () => new HashSet<ulong>(),
            JsonOptions);
        foreach (ulong steamId in loaded)
        {
            this.clearedSteamIds.Add(steamId);
        }
    }

    private void SetTagCleared(ulong steamId, bool cleared)
    {
        if (this.clearedSteamIds.Contains(steamId) == cleared)
        {
            return;
        }

        var updatedClearedSteamIds = new HashSet<ulong>(this.clearedSteamIds);
        if (cleared)
        {
            updatedClearedSteamIds.Add(steamId);
        }
        else
        {
            updatedClearedSteamIds.Remove(steamId);
        }

        JsonStore.Save(
            this.clearedTagsFilePath,
            updatedClearedSteamIds,
            JsonOptions,
            durable: false);
        if (cleared)
        {
            this.clearedSteamIds.Add(steamId);
        }
        else
        {
            this.clearedSteamIds.Remove(steamId);
        }
    }

    private void SaveHiddenTags(HashSet<string> hiddenTagsToSave) =>
        JsonStore.Save(
            this.visibilityFilePath,
            hiddenTagsToSave,
            JsonOptions,
            durable: false);

    private void SaveHiddenSteamIds(HashSet<ulong> hiddenSteamIdsToSave) =>
        JsonStore.Save(
            this.stableVisibilityFilePath,
            hiddenSteamIdsToSave,
            JsonOptions,
            durable: false);

    private void SaveTags(Dictionary<string, PlayerTag> tagsToSave) =>
        JsonStore.Save(
            this.tagsFilePath,
            tagsToSave,
            JsonOptions,
            durable: false);

    private void SaveStableTags(Dictionary<ulong, PlayerTag> tagsToSave) =>
        JsonStore.Save(
            this.stableTagsFilePath,
            tagsToSave,
            JsonOptions,
            durable: false);

    private void SaveGrantedTags(
        Dictionary<ulong, HashSet<PlayerTag>> grantsToSave) =>
        JsonStore.Save(
            this.grantedTagsFilePath,
            grantsToSave,
            JsonOptions,
            durable: false);

    private readonly record struct TagStyle(string Text, uint Color);
}
