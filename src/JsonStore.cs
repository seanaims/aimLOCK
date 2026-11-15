using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace aimLOCK;

/*
Loads and saves the plugin's JSON data files, all kept in one folder.
Saves are atomic: temp file, flush, then rename over the old file.
A file that fails to load is backed up and locked, so it never gets
overwritten with empty data. Fix the file and reload the plugin to unlock it.
*/

public static class JsonStore
{
    private static readonly object syncRoot = new();
    private static readonly HashSet<string> lockedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    // BaseDirectory can be empty inside the plugin host; GetFullPath then
    // resolves against the server's working directory, like the old code did.
    public static string DataDirectory { get; } = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "aimLOCK"
        )
    );

    public static string PathFor(string fileName) =>
        Path.GetFullPath(Path.Combine(DataDirectory, fileName));

    public static void ResetLocks()
    {
        lock (syncRoot)
            lockedPaths.Clear();
    }

    // Copies a file from an old location once. The old file is kept as a backup.
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
                Console.WriteLine($"[aimLOCK] Migrated {source} to {target}.");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Failed to migrate {source}: {ex}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine($"Access denied migrating {source}: {ex}");
            }

            return;
        }
    }

    // validate throws to reject the file, or returns the cleaned data.
    public static T Load<T>(
        string path,
        Func<T> createEmpty,
        JsonSerializerOptions? options = null,
        Func<T, T>? validate = null)
    {
        if (!File.Exists(path))
            return createEmpty();

        try
        {
            T loaded;
            using (var stream = File.OpenRead(path))
            {
                loaded = JsonSerializer.Deserialize<T>(stream, options)
                    ?? throw new InvalidDataException(
                        $"Data file is empty or invalid: {path}"
                    );
            }

            return validate == null ? loaded : validate(loaded);
        }
        catch (Exception ex)
        {
            string backupPath = BackUpBadFile(path);
            lock (syncRoot)
                lockedPaths.Add(path);

            Console.WriteLine(
                $"Failed to load {path}: {ex}\n" +
                $"[aimLOCK] Backup saved to {backupPath}. " +
                "Saving to this file is disabled until it is fixed and the plugin is reloaded."
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
            ?? throw new InvalidOperationException(
                $"Could not determine the data directory: {path}"
            );
        Directory.CreateDirectory(directory);

        string temporaryPath = path + ".tmp";
        using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        ))
        {
            JsonSerializer.Serialize(stream, value, options);
            stream.Flush(true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string BackUpBadFile(string path)
    {
        string backupPath = $"{path}.bad-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(path, backupPath, overwrite: false);
            return backupPath;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to back up {path}: {ex}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied backing up {path}: {ex}");
        }

        return "(backup failed)";
    }
}

// An IOException, so the existing save error handling already covers it.
public sealed class DataStoreLockedException : IOException
{
    public DataStoreLockedException(string path)
        : base($"Refusing to save {path} because it failed to load.")
    {
    }
}
