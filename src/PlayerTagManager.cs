using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using DeadworksManaged.Api;

namespace aimLOCK;

public enum PlayerTag
{
    YouTube,
    Twitch,
    Owner,
    Staff,
    Aimlock,
    Supporter,
    GuildTag
}

public sealed class PlayerTagManager
{
    private sealed class TagOrientationRefresh
    {
        public CCitadelPlayerController Player { get; }
        public CPointWorldText BackgroundText { get; }
        public CPointWorldText[] TagEntities { get; }
        public TagSegment[] Segments { get; }
        public int Version { get; }
        public System.Action Callback { get; }

        public TagOrientationRefresh(
            PlayerTagManager manager,
            CCitadelPlayerController player,
            CPointWorldText backgroundText,
            CPointWorldText[] tagEntities,
            TagSegment[] segments,
            int version)
        {
            Player = player;
            BackgroundText = backgroundText;
            TagEntities = tagEntities;
            Segments = segments;
            Version = version;
            Callback = () => manager.RefreshTagOrientation(this);
        }
    }

    private const string EntityNamePrefix = "aimlock_player_tag_";
    private const int PawnRetries = 8;
    private const float TagHeight = 100f;
    private const float FontSize = 64f;
    private const float WorldUnitsPerPx = 0.2f;
    private const float TagBackgroundPadding = 8f;
    private const float AimlockTagBackgroundExtraPadding = 16f;
    private const int TagBackgroundRows = 1;
    private const byte TagBackgroundAlpha = 160;
    private const char TagBackgroundGlyph = '\u2588';
    private static readonly Vector3 TextAngles = new(180f, 90f, 270f);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly aimLOCK server;
    public const string TagsFileName = "player-tags.json";
    public const string StableTagsFileName = "player-tags-by-id.json";
    public const string GrantedTagsFileName = "player-tag-grants.json";
    public const string VisibilityFileName = "player-tag-visibility.json";
    public const string StableVisibilityFileName = "player-tag-visibility-by-id.json";
    private readonly string tagsFilePath = JsonStore.PathFor(TagsFileName);
    private readonly string stableTagsFilePath = JsonStore.PathFor(StableTagsFileName);
    private readonly string grantedTagsFilePath = JsonStore.PathFor(GrantedTagsFileName);
    private readonly string visibilityFilePath = JsonStore.PathFor(VisibilityFileName);
    private readonly string stableVisibilityFilePath =
        JsonStore.PathFor(StableVisibilityFileName);
    private readonly Dictionary<string, PlayerTag> tags =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, PlayerTag> stableTags = new();
    private readonly Dictionary<ulong, HashSet<PlayerTag>> grantedTags = new();
    private readonly Dictionary<ulong, CPointWorldText[]> activeTags = new();
    private readonly Dictionary<ulong, CPointWorldText> activeTagBackgrounds = new();
    private readonly Dictionary<ulong, PlayerTag> activeTagValues = new();
    private readonly Dictionary<ulong, uint> activeTagPawnHandles = new();
    private readonly Dictionary<ulong, (Vector3 Position, Vector3 Angles)>
        lastTagTransforms = new();
    private readonly Dictionary<ulong, int> refreshVersions = new();
    // Camera yaw each tag was last oriented to. Tags are parented to the pawn, so they
    // follow it automatically; they only need re-orienting when the view turns.
    private readonly Dictionary<ulong, float> lastTagYaw = new();
    private const float TagReorientThresholdDegrees = 1.5f;
    private readonly HashSet<string> hiddenTags =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ulong> hiddenSteamIds = new();
    private bool staleTagEntitiesRemoved;

    public PlayerTagManager(aimLOCK server)
    {
        this.server = server;
        LoadTags();
        LoadHiddenTags();
        LoadStableTags();
        LoadGrantedTags();
        LoadHiddenSteamIds();
    }

    public void Initialize()
    {
        MigrateNameKeyedTags();
        EnsureStaleTagEntitiesRemoved();

        foreach (var player in Players.GetAll())
            RefreshPlayerTag(player);
    }

    public void SetTag(ulong steamId, PlayerTag tag)
    {
        var updatedStableTags = new Dictionary<ulong, PlayerTag>(stableTags)
        {
            [steamId] = tag
        };

        SaveStableTags(updatedStableTags);
        ReplaceStableTags(updatedStableTags);
        RefreshOnlinePlayer(steamId);
    }

    public bool RemoveTag(ulong steamId)
    {
        var updatedStableTags = new Dictionary<ulong, PlayerTag>(stableTags);
        if (!updatedStableTags.Remove(steamId))
            return false;

        SaveStableTags(updatedStableTags);
        ReplaceStableTags(updatedStableTags);
        RefreshOnlinePlayer(steamId);
        return true;
    }

    public void GrantTag(ulong steamId, PlayerTag tag)
    {
        var updatedGrants = grantedTags.ToDictionary(
            entry => entry.Key,
            entry => new HashSet<PlayerTag>(entry.Value)
        );
        if (!updatedGrants.TryGetValue(steamId, out var playerTags))
        {
            playerTags = new HashSet<PlayerTag>();
            updatedGrants[steamId] = playerTags;
        }

        playerTags.Add(tag);
        SaveGrantedTags(updatedGrants);
        ReplaceGrantedTags(updatedGrants);
    }

    public bool RevokeTag(ulong steamId, PlayerTag tag)
    {
        var updatedGrants = grantedTags.ToDictionary(
            entry => entry.Key,
            entry => new HashSet<PlayerTag>(entry.Value)
        );
        if (
            !updatedGrants.TryGetValue(steamId, out var playerTags) ||
            !playerTags.Remove(tag)
        )
        {
            return false;
        }

        if (playerTags.Count == 0)
            updatedGrants.Remove(steamId);

        SaveGrantedTags(updatedGrants);
        ReplaceGrantedTags(updatedGrants);

        if (
            stableTags.TryGetValue(steamId, out PlayerTag equippedTag) &&
            equippedTag == tag
        )
        {
            RemoveTag(steamId);
        }

        return true;
    }


    // Guild members can always wear their own guild's tag; every other tag is granted.
    public bool CanUseTag(ulong steamId, PlayerTag tag) =>
        (tag == PlayerTag.GuildTag &&
            server.GuildManager.GetMemberTag(steamId) != null) ||
        (grantedTags.TryGetValue(steamId, out var playerTags) &&
            playerTags.Contains(tag));

    public string FormatPlayerNameWithSelectedTag(
        ulong steamId,
        string playerName,
        string? guildTag
    )
    {
        if (!stableTags.TryGetValue(steamId, out PlayerTag tag))
            return playerName;
        if (tag == PlayerTag.GuildTag)
            return string.IsNullOrWhiteSpace(guildTag)
                ? playerName
                : $"[{guildTag}] {playerName}";

        string overheadTag = tag switch
        {
            PlayerTag.YouTube => "[YT]",
            PlayerTag.Twitch => "[TTV]",
            PlayerTag.Owner => "[OWNER]",
            PlayerTag.Staff => "[STAFF]",
            PlayerTag.Aimlock => "[aimlock.xyz]",
            PlayerTag.Supporter => "[♥]",
            _ => throw new ArgumentOutOfRangeException(nameof(tag))
        };
        return $"{overheadTag} {playerName}";
    }

    public string GetGrantedTagList(ulong steamId)
    {
        var usableTags = grantedTags.TryGetValue(steamId, out var playerTags)
            ? new HashSet<PlayerTag>(playerTags)
            : new HashSet<PlayerTag>();
        if (server.GuildManager.GetMemberTag(steamId) != null)
            usableTags.Add(PlayerTag.GuildTag);
        else
            usableTags.Remove(PlayerTag.GuildTag);

        if (usableTags.Count == 0)
            return "No overhead tags have been granted to you yet.";

        string[] names = usableTags
            .Select(GetTagToken)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return "Tags you can use: " + string.Join(", ", names) + ".";
    }

    public void RefreshGuildTags()
    {
        foreach (var player in Players.GetAll())
            RefreshPlayerTag(player);
    }

    public void RefreshPlayerTag(CCitadelPlayerController player)
    {
        ulong steamId = player.PlayerSteamId;
        int version = refreshVersions.TryGetValue(steamId, out int currentVersion)
            ? currentVersion + 1
            : 1;
        refreshVersions[steamId] = version;
        RefreshPlayerTag(player, version, PawnRetries);
    }

    public void SetTagVisibility(CCitadelPlayerController player, bool visible)
    {
        var updatedHiddenSteamIds = new HashSet<ulong>(hiddenSteamIds);

        if (visible)
            updatedHiddenSteamIds.Remove(player.PlayerSteamId);
        else
            updatedHiddenSteamIds.Add(player.PlayerSteamId);

        SaveHiddenSteamIds(updatedHiddenSteamIds);
        hiddenSteamIds.Clear();
        foreach (ulong hiddenSteamId in updatedHiddenSteamIds)
            hiddenSteamIds.Add(hiddenSteamId);
        RefreshPlayerTag(player);
    }

    private void RefreshPlayerTag(
        CCitadelPlayerController player,
        int version,
        int retriesRemaining)
    {
        if (!player.IsValid)
            return;

        EnsureStaleTagEntitiesRemoved();
        ulong steamId = player.PlayerSteamId;
        string playerName = player.PlayerName;

        if (
            hiddenSteamIds.Contains(steamId) ||
            !stableTags.TryGetValue(steamId, out PlayerTag tag)
        )
        {
            RemoveTagEntity(steamId);
            return;
        }

        TagSegment[]? segments = GetTagSegments(tag, steamId);
        if (segments == null)
        {
            RemoveTagEntity(steamId);
            return;
        }

        var pawn = player.GetHeroPawn();
        if (pawn == null || !pawn.IsValid)
        {
            RemoveTagEntity(steamId);
            if (retriesRemaining > 0)
            {
                server.ScheduleOnce(0.5.Seconds(), () =>
                {
                    if (
                        refreshVersions.TryGetValue(steamId, out int activeVersion) &&
                        activeVersion == version
                    )
                    {
                        RefreshPlayerTag(
                            player,
                            version,
                            retriesRemaining - 1
                        );
                    }
                });
            }

            return;
        }

        if (
            activeTagValues.TryGetValue(steamId, out PlayerTag activeTag) &&
            activeTag == tag &&
            tag != PlayerTag.GuildTag &&
            activeTagPawnHandles.TryGetValue(
                steamId,
                out uint activePawnHandle
            ) &&
            activePawnHandle == pawn.EntityHandle &&
            activeTagBackgrounds.TryGetValue(
                steamId,
                out var activeBackground
            ) &&
            activeBackground.IsValid &&
            activeTags.TryGetValue(steamId, out var existingTags) &&
            AreTagEntitiesValid(existingTags)
        )
        {
            UpdateTagTransforms(
                steamId,
                pawn,
                activeBackground,
                existingTags,
                segments
            );
            ScheduleTagOrientationRefresh(new TagOrientationRefresh(
                this,
                player,
                activeBackground,
                existingTags,
                segments,
                version
            ));
            return;
        }

        RemoveTagEntity(steamId);
        CPointWorldText? backgroundText = CreateTagBackground(
            steamId,
            pawn,
            tag,
            segments
        );
        if (backgroundText == null)
        {
            Console.WriteLine(
                $"[aimLOCK] Failed to create the background for {playerName}'s overhead tag."
            );
            return;
        }

        var createdTags = new List<CPointWorldText>(segments.Length);
        for (int i = 0; i < segments.Length; i++)
        {
            TagSegment segment = segments[i];
            var tagEntity = CPointWorldText.Create(
                segment.Text,
                pawn.Position + new Vector3(0f, 0f, TagHeight),
                fontSize: FontSize,
                worldUnitsPerPx: WorldUnitsPerPx,
                fontName: "RetailDemo-Bold",
                reorientMode: 0
            );

            if (tagEntity == null)
            {
                backgroundText.Remove();
                foreach (var createdTag in createdTags)
                    createdTag.Remove();

                Console.WriteLine(
                    $"[aimLOCK] Failed to create the overhead tag for {playerName}."
                );
                return;
            }

            tagEntity.ColorABGR = segment.Color;
            tagEntity.JustifyHorizontal = HorizontalJustify.Center;
            tagEntity.AcceptInput(
                "AddOutput",
                null,
                null,
                $"targetname {GetEntityName(steamId, i)}"
            );
            tagEntity.SetParent(pawn);
            createdTags.Add(tagEntity);
        }

        CPointWorldText[] tagEntities = createdTags.ToArray();
        activeTags[steamId] = tagEntities;
        activeTagBackgrounds[steamId] = backgroundText;
        activeTagValues[steamId] = tag;
        activeTagPawnHandles[steamId] = pawn.EntityHandle;
        UpdateTagTransforms(
            steamId,
            pawn,
            backgroundText,
            tagEntities,
            segments
        );
        ScheduleTagOrientationRefresh(new TagOrientationRefresh(
            this,
            player,
            backgroundText,
            tagEntities,
            segments,
            version
        ));
    }

    private void ScheduleTagOrientationRefresh(
        TagOrientationRefresh refresh)
    {
        server.ScheduleOnce(0.1.Seconds(), refresh.Callback);
    }

    private void RefreshTagOrientation(TagOrientationRefresh refresh)
    {
        ulong steamId = refresh.Player.PlayerSteamId;
        if (
            !refresh.Player.IsValid ||
            !refresh.BackgroundText.IsValid ||
            !AreTagEntitiesValid(refresh.TagEntities) ||
            !refreshVersions.TryGetValue(steamId, out int activeVersion) ||
            activeVersion != refresh.Version ||
            !activeTags.TryGetValue(steamId, out var currentTags) ||
            !ReferenceEquals(currentTags, refresh.TagEntities) ||
            !activeTagBackgrounds.TryGetValue(
                steamId,
                out var currentBackground
            ) ||
            !ReferenceEquals(currentBackground, refresh.BackgroundText)
        )
        {
            return;
        }

        var pawn = refresh.Player.GetHeroPawn();
        if (pawn == null || !pawn.IsValid)
        {
            RefreshPlayerTag(refresh.Player);
            return;
        }

        UpdateTagTransforms(
            steamId,
            pawn,
            refresh.BackgroundText,
            refresh.TagEntities,
            refresh.Segments
        );
        ScheduleTagOrientationRefresh(refresh);
    }

    private static bool AreTagEntitiesValid(CPointWorldText[] tagEntities)
    {
        for (int i = 0; i < tagEntities.Length; i++)
        {
            if (!tagEntities[i].IsValid)
                return false;
        }

        return true;
    }

    private void UpdateTagTransforms(
        ulong steamId,
        CCitadelPlayerPawn pawn,
        CPointWorldText backgroundText,
        CPointWorldText[] tagEntities,
        TagSegment[] segments)
    {
        Vector3 right = GetHorizontalRight(pawn.CameraAngles.Y);
        Vector3 headPosition =
            pawn.Position + new Vector3(0f, 0f, TagHeight);
        Vector3 tagAngles = GetTagAngles(pawn.CameraAngles);
        // The tag entities are parented to the pawn, so they follow it on their own.
        // They only need re-orienting when the view turns: comparing the exact position
        // re-teleported every tag entity 10 times a second whenever its player moved.
        float yaw = pawn.CameraAngles.Y;
        if (
            lastTagTransforms.ContainsKey(steamId) &&
            lastTagYaw.TryGetValue(steamId, out float previousYaw) &&
            MathF.Abs(DeltaYaw(yaw, previousYaw)) < TagReorientThresholdDegrees
        )
        {
            return;
        }

        lastTagTransforms[steamId] = (headPosition, tagAngles);
        lastTagYaw[steamId] = yaw;

        backgroundText.Teleport(
            position: headPosition,
            angles: tagAngles
        );
        for (int i = 0; i < tagEntities.Length; i++)
        {
            tagEntities[i].Teleport(
                position: headPosition + right * segments[i].HorizontalOffset,
                angles: tagAngles
            );
        }
    }

    private static CPointWorldText? CreateTagBackground(
        ulong steamId,
        CCitadelPlayerPawn pawn,
        PlayerTag tag,
        TagSegment[] segments
    )
    {
        float characterWidth = FontSize * WorldUnitsPerPx * 0.41f;
        float left = segments.Min(segment =>
            segment.HorizontalOffset - segment.Text.Length * characterWidth / 2f
        );
        float right = segments.Max(segment =>
            segment.HorizontalOffset + segment.Text.Length * characterWidth / 2f
        );
        float glyphWidth = FontSize * WorldUnitsPerPx;
        float horizontalPadding = TagBackgroundPadding +
            (tag == PlayerTag.Aimlock ? AimlockTagBackgroundExtraPadding : 0f);
        int glyphCount = Math.Max(
            1,
            (int)Math.Ceiling(
                (right - left + horizontalPadding * 2f) / glyphWidth
            )
        );
        string row = new(TagBackgroundGlyph, glyphCount);
        string message = string.Join(
            "\n",
            Enumerable.Repeat(row, TagBackgroundRows)
        );
        Vector3 headPosition =
            pawn.Position + new Vector3(0f, 0f, TagHeight);
        var backgroundText = CPointWorldText.Create(
            message,
            headPosition,
            fontSize: FontSize,
            worldUnitsPerPx: WorldUnitsPerPx,
            fontName: "RetailDemo-Bold",
            reorientMode: 0
        );
        if (backgroundText == null)
            return null;

        backgroundText.ColorABGR = (uint)TagBackgroundAlpha << 24;
        backgroundText.Fullbright = true;
        backgroundText.DepthOffset = -1f;
        backgroundText.JustifyHorizontal = HorizontalJustify.Center;
        backgroundText.AcceptInput(
            "AddOutput",
            null,
            null,
            $"targetname {GetEntityName(steamId, -1)}"
        );
        backgroundText.SetParent(pawn);
        backgroundText.Teleport(
            position: headPosition,
            angles: GetTagAngles(pawn.CameraAngles)
        );
        return backgroundText;
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
            _ => (PlayerTag)(-1)
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
            _ => throw new ArgumentOutOfRangeException(nameof(tag))
        };

    private TagSegment[]? GetTagSegments(PlayerTag tag, ulong steamId)
    {
        if (tag == PlayerTag.GuildTag)
        {
            string? guildTag = server.GuildManager.GetMemberTag(steamId);
            return string.IsNullOrWhiteSpace(guildTag)
                ? null
                : new[]
                {
                    new TagSegment($"[{guildTag}]", 0xFFFFFFFF, 0f)
                };
        }

        if (tag == PlayerTag.Aimlock)
        {
            float characterWidth = FontSize * WorldUnitsPerPx * 0.41f;
            float aimlockWidth = 7f * characterWidth;
            float domainWidth = 4f * characterWidth;
            float bracketWidth = characterWidth * 0.5f;
            float fullWidth = bracketWidth + aimlockWidth + domainWidth + bracketWidth;

            // World-text's local horizontal axis runs opposite the camera-right axis.
            return new[]
            {
                new TagSegment(
                    "[",
                    0xFF555555,
                    fullWidth / 2f - bracketWidth / 2f
                ),
                new TagSegment(
                    "aimlock",
                    0xFFFFFFFF,
                    fullWidth / 2f - bracketWidth - aimlockWidth / 2f
                ),
                new TagSegment(
                    ".xyz",
                    0xFF555555,
                    fullWidth / 2f - bracketWidth - aimlockWidth -
                    domainWidth / 2f
                ),
                new TagSegment(
                    "]",
                    0xFF555555,
                    -fullWidth / 2f + bracketWidth / 2f
                )
            };
        }

        (string text, uint color) = tag switch
        {
            PlayerTag.YouTube => ("[YT]", 0xFF0000FF),
            PlayerTag.Twitch => ("[TTV]", 0xFFF755A8),
            PlayerTag.Owner => ("[OWNER]", 0xFF000000),
            PlayerTag.Staff => ("[STAFF]", 0xFF00FFFF),
            PlayerTag.Supporter => ("[♥]", 0xFFB469FF),
            _ => throw new ArgumentOutOfRangeException(nameof(tag))
        };
        return new[] { new TagSegment(text, color, 0f) };
    }

    private static Vector3 GetHorizontalRight(float yaw)
    {
        float radians = yaw * (MathF.PI / 180f);
        return new Vector3(-MathF.Sin(radians), MathF.Cos(radians), 0f);
    }

    private static Vector3 GetTagAngles(Vector3 cameraAngles) =>
        new(
            TextAngles.X,
            NormalizeYaw(cameraAngles.Y + TextAngles.Y),
            TextAngles.Z
        );

    private static float DeltaYaw(float a, float b)
    {
        float delta = (a - b) % 360f;
        if (delta > 180f)
            delta -= 360f;
        else if (delta < -180f)
            delta += 360f;
        return delta;
    }

    private static float NormalizeYaw(float yaw)
    {
        yaw %= 360f;
        return yaw < 0f ? yaw + 360f : yaw;
    }

    private void RefreshOnlinePlayer(ulong steamId)
    {
        foreach (var player in Players.GetAll())
        {
            if (player.PlayerSteamId == steamId)
                RefreshPlayerTag(player);
        }
    }

    private ulong[] ResolveLegacyName(
        string playerName,
        IEnumerable<CCitadelPlayerController> onlinePlayers
    ) =>
        onlinePlayers
            .Where(player => !player.IsBot && string.Equals(
                player.PlayerName?.Trim(),
                playerName,
                StringComparison.OrdinalIgnoreCase
            ))
            .Select(player => player.PlayerSteamId)
            .Concat(server.PlayerRecords.Values
                .Where(record =>
                    !string.IsNullOrWhiteSpace(record.LastKnownName) &&
                    string.Equals(
                        record.LastKnownName.Trim(),
                        playerName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Select(record => record.SteamId))
            .Where(steamId => steamId != 0)
            .Distinct()
            .ToArray();

    /* Name-keyed tag migration */

    private void MigrateNameKeyedTags()
    {
        if (tags.Count == 0 && hiddenTags.Count == 0)
            return;

        var onlinePlayers = Players.GetAll().ToArray();
        var updatedStableTags = new Dictionary<ulong, PlayerTag>(stableTags);
        var updatedHiddenSteamIds = new HashSet<ulong>(hiddenSteamIds);

        foreach (var entry in tags)
        {
            ulong[] steamIds = ResolveLegacyName(entry.Key, onlinePlayers);
            if (steamIds.Length != 1)
            {
                Console.WriteLine(
                    $"Failed to migrate the {GetTagToken(entry.Value)} tag for \"{entry.Key}\": " +
                    $"{steamIds.Length} players match that name. Set it again with /tag."
                );
                continue;
            }

            updatedStableTags.TryAdd(steamIds[0], entry.Value);
        }

        foreach (string playerName in hiddenTags)
        {
            ulong[] steamIds = ResolveLegacyName(playerName, onlinePlayers);
            if (steamIds.Length == 1)
                updatedHiddenSteamIds.Add(steamIds[0]);
        }

        try
        {
            BackUpLegacyFile(tagsFilePath);
            BackUpLegacyFile(visibilityFilePath);
            SaveStableTags(updatedStableTags);
            SaveHiddenSteamIds(updatedHiddenSteamIds);
            SaveTags(new Dictionary<string, PlayerTag>());
            SaveHiddenTags(new HashSet<string>());
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

        ReplaceStableTags(updatedStableTags);
        hiddenSteamIds.Clear();
        foreach (ulong steamId in updatedHiddenSteamIds)
            hiddenSteamIds.Add(steamId);
        tags.Clear();
        hiddenTags.Clear();

        Console.WriteLine("[aimLOCK] Overhead tags are now saved by SteamID. Current holders:");
        foreach (var entry in stableTags)
        {
            string name = server.PlayerRecords.TryGetValue(entry.Key, out var record)
                ? record.LastKnownName
                : "unknown";
            Console.WriteLine(
                $"[aimLOCK]   {entry.Key} ({name}): {GetTagToken(entry.Value)}"
            );
        }
    }

    private static void BackUpLegacyFile(string path)
    {
        if (!File.Exists(path))
            return;

        File.Copy(
            path,
            $"{path}.migrated-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            overwrite: false
        );
    }

    private void RemoveTagEntity(ulong steamId)
    {
        if (
            activeTagBackgrounds.Remove(steamId, out var backgroundText) &&
            backgroundText.IsValid
        )
        {
            backgroundText.Remove();
        }

        if (activeTags.Remove(steamId, out var tagEntities))
        {
            foreach (var tagEntity in tagEntities)
            {
                if (tagEntity.IsValid)
                    tagEntity.Remove();
            }
        }

        activeTagValues.Remove(steamId);
        activeTagPawnHandles.Remove(steamId);
        lastTagTransforms.Remove(steamId);
        lastTagYaw.Remove(steamId);
    }

    private void EnsureStaleTagEntitiesRemoved()
    {
        if (staleTagEntitiesRemoved)
            return;

        RemoveStaleTagEntities();
        activeTags.Clear();
        activeTagBackgrounds.Clear();
        activeTagValues.Clear();
        activeTagPawnHandles.Clear();
        lastTagTransforms.Clear();
        lastTagYaw.Clear();
        staleTagEntitiesRemoved = true;
    }

    private void RemoveStaleTagEntities()
    {
        foreach (var entity in Entities.All)
        {
            if (
                entity.IsValid &&
                entity.Name is { } name &&
                name.StartsWith(EntityNamePrefix, StringComparison.Ordinal)
            )
            {
                entity.Remove();
            }
        }
    }

    private static string GetEntityName(ulong steamId, int index) =>
        GetEntityNamePrefix(steamId) + index;

    private static string GetEntityNamePrefix(ulong steamId) =>
        EntityNamePrefix + steamId + "_";

    private readonly record struct TagSegment(
        string Text,
        uint Color,
        float HorizontalOffset
    );

    private void ReplaceStableTags(Dictionary<ulong, PlayerTag> updatedTags)
    {
        stableTags.Clear();
        foreach (var entry in updatedTags)
            stableTags[entry.Key] = entry.Value;
    }

    private void ReplaceGrantedTags(
        Dictionary<ulong, HashSet<PlayerTag>> updatedGrants
    )
    {
        grantedTags.Clear();
        foreach (var entry in updatedGrants)
            grantedTags[entry.Key] = entry.Value;
    }

    private static string NormalizePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            throw new ArgumentException("A player name is required.", nameof(playerName));

        return playerName.Trim();
    }

    private void LoadTags()
    {
        var loaded = JsonStore.Load(
            tagsFilePath,
            () => new Dictionary<string, PlayerTag>(),
            JsonOptions
        );
        foreach (var entry in loaded)
        {
            if (Enum.IsDefined(typeof(PlayerTag), entry.Value))
                tags[NormalizePlayerName(entry.Key)] = entry.Value;
        }
    }

    private void LoadHiddenTags()
    {
        var loaded = JsonStore.Load(
            visibilityFilePath,
            () => new HashSet<string>(),
            JsonOptions
        );
        foreach (string playerName in loaded)
        {
            if (!string.IsNullOrWhiteSpace(playerName))
                hiddenTags.Add(NormalizePlayerName(playerName));
        }
    }

    private void LoadStableTags()
    {
        var loaded = JsonStore.Load(
            stableTagsFilePath,
            () => new Dictionary<ulong, PlayerTag>(),
            JsonOptions
        );
        foreach (var entry in loaded)
        {
            if (Enum.IsDefined(typeof(PlayerTag), entry.Value))
                stableTags[entry.Key] = entry.Value;
        }
    }

    private void LoadGrantedTags()
    {
        var loaded = JsonStore.Load(
            grantedTagsFilePath,
            () => new Dictionary<ulong, HashSet<PlayerTag>>(),
            JsonOptions
        );
        foreach (var entry in loaded)
        {
            if (entry.Key == 0 || entry.Key == ulong.MaxValue || entry.Value == null)
                continue;

            PlayerTag[] validTags = entry.Value
                .Where(tag => Enum.IsDefined(typeof(PlayerTag), tag))
                .Distinct()
                .ToArray();
            if (validTags.Length > 0)
                grantedTags[entry.Key] = new HashSet<PlayerTag>(validTags);
        }
    }

    private void LoadHiddenSteamIds()
    {
        var loaded = JsonStore.Load(
            stableVisibilityFilePath,
            () => new HashSet<ulong>(),
            JsonOptions
        );
        foreach (ulong steamId in loaded)
            hiddenSteamIds.Add(steamId);
    }

    private void SaveHiddenTags(HashSet<string> hiddenTagsToSave) =>
        JsonStore.Save(
            visibilityFilePath,
            hiddenTagsToSave,
            JsonOptions,
            durable: false
        );

    private void SaveHiddenSteamIds(HashSet<ulong> hiddenSteamIdsToSave) =>
        JsonStore.Save(
            stableVisibilityFilePath,
            hiddenSteamIdsToSave,
            JsonOptions,
            durable: false
        );

    private void SaveTags(Dictionary<string, PlayerTag> tagsToSave) =>
        JsonStore.Save(
            tagsFilePath,
            tagsToSave,
            JsonOptions,
            durable: false
        );

    private void SaveStableTags(Dictionary<ulong, PlayerTag> tagsToSave) =>
        JsonStore.Save(
            stableTagsFilePath,
            tagsToSave,
            JsonOptions,
            durable: false
        );

    private void SaveGrantedTags(
        Dictionary<ulong, HashSet<PlayerTag>> grantsToSave
    ) =>
        JsonStore.Save(
            grantedTagsFilePath,
            grantsToSave,
            JsonOptions,
            durable: false
        );
}
