using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace aimLOCK;

/// <summary>
/// Loads and saves the plugin's JSON data files.
///
/// All files live in one directory (<see cref="DataDirectory"/>). Saves are
/// atomic (write to a temp file, flush to disk, then rename over the target),
/// so a crash mid-save leaves the previous file intact.
///
/// If a file exists but cannot be read or fails validation, it is copied to
/// a "*.bad-&lt;timestamp&gt;" backup and that path is locked: the plugin starts
/// with empty data, and every later save to that path throws
/// <see cref="DataStoreLockedException"/> instead of overwriting the original.
/// Fix or remove the file, then reload the plugin to unlock it.
/// </summary>
public static class JsonStore
{
    private static readonly object syncRoot = new();
    private static readonly HashSet<string> lockedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public static string DataDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "aimLOCK");

    public static string PathFor(string fileName) =>
        Path.GetFullPath(Path.Combine(DataDirectory, fileName));

    /// <summary>Clears locks left over from a previous plugin load.</summary>
    public static void ResetLocks()
    {
        lock (syncRoot)
            lockedPaths.Clear();
    }

    /// <summary>
    /// Copies data written by older versions into <see cref="DataDirectory"/>.
    /// Runs only while the new file does not exist yet; the old file is left
    /// in place as a backup. The first existing legacy path wins.
    /// </summary>
    public static void MigrateLegacyFile(string fileName, params string[] legacyPaths)
    {
        string target = PathFor(fileName);
        if (File.Exists(target))
            return;

        foreach (string legacyPath in legacyPaths)
        {
            string source = Path.GetFullPath(legacyPath);
            if (!File.Exists(source))
                continue;

            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.Copy(source, target, overwrite: false);
                Console.WriteLine($"[aimLOCK] Migrated {source} -> {target}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[aimLOCK] Failed to migrate {source} -> {target}: {ex}");
            }
            return;
        }
    }

    public static T Load<T>(
        string path,
        Func<T> createEmpty,
        JsonSerializerOptions? options = null,
        Func<T, T>? validate = null)
    {
        // validate: throw to reject the file, or return the (cleaned) data.
        if (!File.Exists(path))
            return createEmpty();

        try
        {
            T loaded;
            using (var stream = File.OpenRead(path))
            {
                loaded = JsonSerializer.Deserialize<T>(stream, options)
                    ?? throw new InvalidDataException("The file is empty or null.");
            }

            return validate == null ? loaded : validate(loaded);
        }
        catch (Exception ex)
        {
            string backupPath = $"{path}.bad-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try
            {
                File.Copy(path, backupPath, overwrite: false);
            }
            catch (Exception backupEx) when (
                backupEx is IOException or UnauthorizedAccessException)
            {
                backupPath = $"(backup failed: {backupEx.Message})";
            }

            lock (syncRoot)
                lockedPaths.Add(path);

            Console.WriteLine(
                $"[aimLOCK] ERROR: could not load {path}. Backup: {backupPath}. " +
                "Saving to this file is disabled until it is fixed and the plugin is reloaded. " +
                $"Cause: {ex}"
            );
            return createEmpty();
        }
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        lock (syncRoot)
        {
            if (lockedPaths.Contains(path))
                throw new DataStoreLockedException(path);
        }

        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"No directory for data file: {path}");
        Directory.CreateDirectory(directory);

        string temporaryPath = path + ".tmp";
        using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, options);
            stream.Flush(true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}

/// <summary>
/// Thrown when saving to a data file that failed to load. Derives from
/// <see cref="IOException"/> so existing save error handling covers it.
/// </summary>
public sealed class DataStoreLockedException : IOException
{
    public DataStoreLockedException(string path)
        : base($"Refusing to save {path}: it failed to load and was backed up. Fix it and reload the plugin.")
    {
    }
}
