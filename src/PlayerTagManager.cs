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
    Supporter
}

public sealed class PlayerTagManager
{
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
    private readonly string tagsFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "aimlock_player_tags.json"
    );
    private readonly string stableTagsFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "aimlock_player_tags_by_id.json"
    );
    private readonly string visibilityFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "aimlock_player_tag_visibility.json"
    );
    private readonly string stableVisibilityFilePath = Path.Combine(
        AppContext.BaseDirectory,
        "aimlock_player_tag_visibility_by_id.json"
    );
    private readonly Dictionary<string, PlayerTag> tags =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, PlayerTag> stableTags = new();
    private readonly Dictionary<ulong, CPointWorldText[]> activeTags = new();
    private readonly Dictionary<ulong, CPointWorldText> activeTagBackgrounds = new();
    private readonly Dictionary<ulong, int> refreshVersions = new();
    private readonly HashSet<string> hiddenTags =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ulong> hiddenSteamIds = new();

    public PlayerTagManager(aimLOCK server)
    {
        this.server = server;
        LoadTags();
        LoadHiddenTags();
        LoadStableTags();
        LoadHiddenSteamIds();
    }

    public void Initialize()
    {
        MigrateKnownPlayerNames();
        RemoveStaleTagEntities();
        activeTags.Clear();
        activeTagBackgrounds.Clear();

        foreach (var player in Players.GetAll())
            RefreshPlayerTag(player);
    }

    public void SetTag(string playerName, PlayerTag tag)
    {
        string normalizedName = NormalizePlayerName(playerName);
        ulong[] steamIds = ResolvePlayerSteamIds(normalizedName);
        var updatedTags = new Dictionary<string, PlayerTag>(
            tags,
            StringComparer.OrdinalIgnoreCase
        )
        {
            [normalizedName] = tag
        };
        var updatedStableTags = new Dictionary<ulong, PlayerTag>(stableTags);
        foreach (ulong steamId in steamIds)
            updatedStableTags[steamId] = tag;

        SaveTags(updatedTags);
        SaveStableTags(updatedStableTags);
        ReplaceTags(updatedTags);
        ReplaceStableTags(updatedStableTags);
        RefreshOnlinePlayers(normalizedName);
    }

    public void RemoveTag(string playerName)
    {
        string normalizedName = NormalizePlayerName(playerName);
        ulong[] steamIds = ResolvePlayerSteamIds(normalizedName);
        var updatedTags = new Dictionary<string, PlayerTag>(
            tags,
            StringComparer.OrdinalIgnoreCase
        );
        bool removedNameTag = updatedTags.Remove(normalizedName);

        var updatedStableTags = new Dictionary<ulong, PlayerTag>(stableTags);
        bool removedStableTag = false;
        foreach (ulong steamId in steamIds)
            removedStableTag |= updatedStableTags.Remove(steamId);

        if (!removedNameTag && !removedStableTag)
        {
            RefreshOnlinePlayers(normalizedName);
            return;
        }

        SaveTags(updatedTags);
        SaveStableTags(updatedStableTags);
        ReplaceTags(updatedTags);
        ReplaceStableTags(updatedStableTags);
        RefreshOnlinePlayers(normalizedName);
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
        string playerName = NormalizePlayerName(player.PlayerName);
        var updatedHiddenTags = new HashSet<string>(
            hiddenTags,
            StringComparer.OrdinalIgnoreCase
        );
        var updatedHiddenSteamIds = new HashSet<ulong>(hiddenSteamIds);

        if (visible)
        {
            updatedHiddenTags.Remove(playerName);
            updatedHiddenSteamIds.Remove(player.PlayerSteamId);
        }
        else
        {
            updatedHiddenSteamIds.Add(player.PlayerSteamId);
        }

        SaveHiddenTags(updatedHiddenTags);
        SaveHiddenSteamIds(updatedHiddenSteamIds);
        hiddenTags.Clear();
        foreach (string hiddenPlayerName in updatedHiddenTags)
            hiddenTags.Add(hiddenPlayerName);
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

        ulong steamId = player.PlayerSteamId;
        RemoveTagEntity(steamId);

        string playerName = NormalizePlayerName(player.PlayerName);
        MigratePlayerTagToStableId(playerName, steamId);
        if (hiddenTags.Contains(playerName) && hiddenSteamIds.Add(steamId))
            PersistHiddenSteamIds();

        if (hiddenSteamIds.Contains(steamId) || hiddenTags.Contains(playerName))
            return;

        if (
            !stableTags.TryGetValue(steamId, out PlayerTag tag) &&
            !tags.TryGetValue(playerName, out tag)
        )
            return;

        var pawn = player.GetHeroPawn();
        if (pawn == null || !pawn.IsValid)
        {
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

        TagSegment[] segments = GetTagSegments(tag);
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
        UpdateTagTransforms(pawn, backgroundText, tagEntities, segments);
        ScheduleTagOrientationRefresh(
            player,
            backgroundText,
            tagEntities,
            segments,
            version
        );
    }

    private void ScheduleTagOrientationRefresh(
        CCitadelPlayerController player,
        CPointWorldText backgroundText,
        CPointWorldText[] tagEntities,
        TagSegment[] segments,
        int version)
    {
        server.ScheduleOnce(0.1.Seconds(), () =>
        {
            ulong steamId = player.PlayerSteamId;
            if (
                !player.IsValid ||
                !backgroundText.IsValid ||
                tagEntities.Any(tagEntity => !tagEntity.IsValid) ||
                !refreshVersions.TryGetValue(steamId, out int activeVersion) ||
                activeVersion != version ||
                !activeTags.TryGetValue(steamId, out var currentTags) ||
                !ReferenceEquals(currentTags, tagEntities) ||
                !activeTagBackgrounds.TryGetValue(
                    steamId,
                    out var currentBackground
                ) ||
                !ReferenceEquals(currentBackground, backgroundText)
            )
            {
                return;
            }

            var pawn = player.GetHeroPawn();
            if (pawn == null || !pawn.IsValid)
            {
                RefreshPlayerTag(player);
                return;
            }

            UpdateTagTransforms(
                pawn,
                backgroundText,
                tagEntities,
                segments
            );
            ScheduleTagOrientationRefresh(
                player,
                backgroundText,
                tagEntities,
                segments,
                version
            );
        });
    }

    private static void UpdateTagTransforms(
        CCitadelPlayerPawn pawn,
        CPointWorldText backgroundText,
        CPointWorldText[] tagEntities,
        TagSegment[] segments)
    {
        Vector3 right = GetHorizontalRight(pawn.CameraAngles.Y);
        Vector3 headPosition =
            pawn.Position + new Vector3(0f, 0f, TagHeight);
        Vector3 tagAngles = GetTagAngles(pawn.CameraAngles);

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

    private static TagSegment[] GetTagSegments(PlayerTag tag)
    {
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

    private static float NormalizeYaw(float yaw)
    {
        yaw %= 360f;
        return yaw < 0f ? yaw + 360f : yaw;
    }

    private void RefreshOnlinePlayers(string playerName)
    {
        var matchingPlayers = Players.GetAll()
            .Where(candidate => string.Equals(
                NormalizePlayerName(candidate.PlayerName),
                playerName,
                StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();

        foreach (var player in matchingPlayers)
            RefreshPlayerTag(player);
    }

    private ulong[] ResolvePlayerSteamIds(string playerName) =>
        Players.GetAll()
            .Where(player => string.Equals(
                NormalizePlayerName(player.PlayerName),
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

    private void MigratePlayerTagToStableId(string playerName, ulong steamId)
    {
        if (stableTags.ContainsKey(steamId) ||
            !tags.TryGetValue(playerName, out PlayerTag tag))
        {
            return;
        }

        stableTags[steamId] = tag;
        try
        {
            SaveStableTags(stableTags);
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to migrate the tag for SteamID {steamId}: {ex}"
            );
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine(
                $"Access denied migrating the tag for SteamID {steamId}: {ex}"
            );
        }
    }

    private void MigrateKnownPlayerNames()
    {
        bool tagsChanged = false;
        foreach (var entry in tags)
        {
            foreach (ulong steamId in ResolvePlayerSteamIds(entry.Key))
            {
                if (stableTags.ContainsKey(steamId))
                    continue;

                stableTags[steamId] = entry.Value;
                tagsChanged = true;
            }
        }

        if (tagsChanged)
        {
            try
            {
                SaveStableTags(stableTags);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Failed to migrate player tags: {ex}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Access denied migrating player tags: {ex}");
            }
        }

        bool visibilityChanged = false;
        foreach (string playerName in hiddenTags)
        {
            foreach (ulong steamId in ResolvePlayerSteamIds(playerName))
                visibilityChanged |= hiddenSteamIds.Add(steamId);
        }

        if (visibilityChanged)
            PersistHiddenSteamIds();
    }

    private void PersistHiddenSteamIds()
    {
        try
        {
            SaveHiddenSteamIds(hiddenSteamIds);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to migrate hidden player tags: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied migrating hidden player tags: {ex}");
        }
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

        string entityNamePrefix = GetEntityNamePrefix(steamId);
        foreach (var entity in Entities.All)
        {
            if (
                entity.IsValid &&
                entity.Name.StartsWith(
                    entityNamePrefix,
                    StringComparison.Ordinal
                )
            )
            {
                entity.Remove();
            }
        }
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

    private void ReplaceTags(Dictionary<string, PlayerTag> updatedTags)
    {
        tags.Clear();
        foreach (var entry in updatedTags)
            tags[entry.Key] = entry.Value;
    }

    private void ReplaceStableTags(Dictionary<ulong, PlayerTag> updatedTags)
    {
        stableTags.Clear();
        foreach (var entry in updatedTags)
            stableTags[entry.Key] = entry.Value;
    }

    private static string NormalizePlayerName(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            throw new ArgumentException("A player name is required.", nameof(playerName));

        return playerName.Trim();
    }

    private void LoadTags()
    {
        if (!File.Exists(tagsFilePath))
            return;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PlayerTag>>(
                File.ReadAllText(tagsFilePath)
            );
            if (loaded == null)
                throw new InvalidDataException("The tag file is empty.");

            foreach (var entry in loaded)
            {
                if (Enum.IsDefined(typeof(PlayerTag), entry.Value))
                    tags[NormalizePlayerName(entry.Key)] = entry.Value;
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to load player tags: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Failed to access player tags: {ex}");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Failed to parse player tags: {ex}");
        }
    }

    private void LoadHiddenTags()
    {
        if (!File.Exists(visibilityFilePath))
            return;

        try
        {
            var loaded = JsonSerializer.Deserialize<HashSet<string>>(
                File.ReadAllText(visibilityFilePath)
            );
            if (loaded == null)
                throw new InvalidDataException(
                    "The player tag visibility file is empty."
                );

            foreach (string playerName in loaded)
            {
                if (!string.IsNullOrWhiteSpace(playerName))
                    hiddenTags.Add(NormalizePlayerName(playerName));
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to load player tag visibility: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Failed to access player tag visibility: {ex}");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Failed to parse player tag visibility: {ex}");
        }
    }

    private void LoadStableTags()
    {
        if (!File.Exists(stableTagsFilePath))
            return;

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<ulong, PlayerTag>>(
                File.ReadAllText(stableTagsFilePath)
            );
            if (loaded == null)
                throw new InvalidDataException(
                    "The stable player tag file is empty."
                );

            foreach (var entry in loaded)
            {
                if (Enum.IsDefined(typeof(PlayerTag), entry.Value))
                    stableTags[entry.Key] = entry.Value;
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to load stable player tags: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Failed to access stable player tags: {ex}");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Failed to parse stable player tags: {ex}");
        }
    }

    private void LoadHiddenSteamIds()
    {
        if (!File.Exists(stableVisibilityFilePath))
            return;

        try
        {
            var loaded = JsonSerializer.Deserialize<HashSet<ulong>>(
                File.ReadAllText(stableVisibilityFilePath)
            );
            if (loaded == null)
                throw new InvalidDataException(
                    "The stable player tag visibility file is empty."
                );

            foreach (ulong steamId in loaded)
                hiddenSteamIds.Add(steamId);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to load stable tag visibility: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Failed to access stable tag visibility: {ex}");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Failed to parse stable tag visibility: {ex}");
        }
    }

    private void SaveHiddenTags(HashSet<string> hiddenTagsToSave)
    {
        string temporaryPath = visibilityFilePath + ".tmp";
        string json = JsonSerializer.Serialize(hiddenTagsToSave, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, visibilityFilePath, overwrite: true);
    }

    private void SaveHiddenSteamIds(HashSet<ulong> hiddenSteamIdsToSave)
    {
        string temporaryPath = stableVisibilityFilePath + ".tmp";
        string json = JsonSerializer.Serialize(hiddenSteamIdsToSave, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, stableVisibilityFilePath, overwrite: true);
    }

    private void SaveTags(Dictionary<string, PlayerTag> tagsToSave)
    {
        string temporaryPath = tagsFilePath + ".tmp";
        string json = JsonSerializer.Serialize(tagsToSave, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, tagsFilePath, overwrite: true);
    }

    private void SaveStableTags(Dictionary<ulong, PlayerTag> tagsToSave)
    {
        string temporaryPath = stableTagsFilePath + ".tmp";
        string json = JsonSerializer.Serialize(tagsToSave, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, stableTagsFilePath, overwrite: true);
    }
}
