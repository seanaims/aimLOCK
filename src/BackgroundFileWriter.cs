/*
Writes data files off the game thread.

Saving JSON with File.WriteAllText / FileStream.Flush(true) on the game thread stalls the
server frame for as long as the disk takes (fsync on a VPS disk can be 10-100+ ms), which
shows up as a server-frame spike and missed shots in every other running duel.

Callers serialize on the game thread (cheap, and it snapshots the data so there is no
cross-thread access to game state), then hand the bytes here. Writes to the same path are
coalesced: only the newest pending payload is written.
*/
namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public static class BackgroundFileWriter
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, byte[]> Pending =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Running =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Enqueue(string path, byte[] contents, bool durable = true)
    {
        string fullPath = Path.GetFullPath(path);
        lock (SyncRoot)
        {
            Pending[fullPath] = contents;
            if (!Running.Add(fullPath))
            {
                return; // a writer for this path is active and will pick up the newest payload
            }
        }

        Task.Run(() => DrainPath(fullPath, durable));
    }

    /// <summary>Blocks until every queued write has finished. Call from OnUnload.</summary>
    public static void Flush(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (SyncRoot)
            {
                if (Running.Count == 0)
                {
                    return;
                }
            }

            Thread.Sleep(10);
        }

        Console.WriteLine("[aimLOCK] Timed out waiting for background file writes to finish.");
    }

    private static void DrainPath(string fullPath, bool durable)
    {
        while (true)
        {
            byte[] contents;
            lock (SyncRoot)
            {
                if (!Pending.Remove(fullPath, out contents!))
                {
                    Running.Remove(fullPath);
                    return;
                }
            }

            try
            {
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temporaryPath = fullPath + ".tmp";
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(durable);
                }

                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[aimLOCK] Background write to {fullPath} failed: {ex}");
            }
        }
    }
}
