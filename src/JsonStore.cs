namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public static class JsonStore
{
    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> LockedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public static string DataDirectory { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "aimLOCK"));

    public static string PathFor(string fileName) =>
        Path.GetFullPath(Path.Combine(DataDirectory, fileName));

    public static void ResetLocks()
    {
        lock (SyncRoot)
        {
            LockedPaths.Clear();
        }
    }

    public static void MigrateLegacyFile(string fileName, params string[] legacyPaths)
    {
        string target = PathFor(fileName);
        if (File.Exists(target))
        {
            return;
        }

        foreach (string legacyPath in legacyPaths)
        {
            string source = Path.GetFullPath(legacyPath);
            if (!File.Exists(source))
            {
                continue;
            }

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

    public static T Load<T>(
        string path,
        Func<T> createEmpty,
        JsonSerializerOptions? options = null,
        Func<T, T>? validate = null)
    {
        if (!File.Exists(path))
        {
            return createEmpty();
        }

        try
        {
            T loaded;
            using (var stream = File.OpenRead(path))
            {
                loaded = JsonSerializer.Deserialize<T>(stream, options)
                    ?? throw new InvalidDataException(
                        $"Data file is empty or invalid: {path}");
            }

            return validate == null ? loaded : validate(loaded);
        }
        catch (Exception ex)
        {
            string backupPath = BackUpBadFile(path);
            lock (SyncRoot)
            {
                LockedPaths.Add(path);
            }

            Console.WriteLine(
                $"Failed to load {path}: {ex}\n" +
                $"[aimLOCK] Backup saved to {backupPath}. " +
                "Saving to this file is disabled until it is fixed and the plugin is reloaded.");
            return createEmpty();
        }
    }

    public static void Save<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        bool durable = true)
    {
        lock (SyncRoot)
        {
            if (LockedPaths.Contains(path))
            {
                throw new DataStoreLockedException(path);
            }
        }

        byte[] contents = JsonSerializer.SerializeToUtf8Bytes(value, options);
        BackgroundFileWriter.Enqueue(path, contents, durable);
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

public sealed class DataStoreLockedException : IOException
{
    public DataStoreLockedException(string path)
        : base($"Refusing to save {path} because it failed to load.")
    {
    }
}
