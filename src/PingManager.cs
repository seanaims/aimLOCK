namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.Globalization;
using DeadworksManaged.Api;

public sealed class PingManager
{
    // `status` prints every player to the server console, which is not free on a
    // Windows console. Poll quickly only while someone is queued (matchmaking needs fresh
    // pings); otherwise poll just often enough to keep readings from expiring.
    private const int PollIntervalSeconds = 5;
    private const int IdlePollIntervalSeconds = 10;
    private const int PollTimeoutSeconds = 2;
    private const int SampleCount = 3;
    private static readonly TimeSpan ReadingLifetime = TimeSpan.FromSeconds(15);

    private readonly aimLOCK server;
    private readonly Dictionary<ulong, Queue<int>> samples = new();
    private readonly Dictionary<ulong, PingSnapshot> latestReadings = new();
    private bool started;
    private bool pollInProgress;
    private bool warnedAboutMissingRows;
    private int pollGeneration;
    private int scheduledPollToken;

    public PingManager(aimLOCK server)
    {
        this.server = server;
    }

    public void Start()
    {
        if (this.started)
        {
            return;
        }

        this.started = true;
        this.server.ScheduleOnce(2.Seconds(), Poll);
    }

    public void Refresh()
    {
        if (this.started && !this.pollInProgress)
        {
            this.Poll();
        }
    }

    public bool TryGetPing(CCitadelPlayerController player, out int pingMs)
    {
        pingMs = 0;
        if (player.IsBot || player.PlayerSteamId == 0)
        {
            return false;
        }

        if (!this.latestReadings.TryGetValue(player.PlayerSteamId, out var reading))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - reading.CapturedAt > ReadingLifetime)
        {
            return false;
        }

        pingMs = reading.PingMs;
        return true;
    }

    private static int Median(Queue<int> values)
    {
        using var enumerator = values.GetEnumerator();
        enumerator.MoveNext();
        int first = enumerator.Current;
        if (!enumerator.MoveNext())
        {
            return first;
        }

        int second = enumerator.Current;
        if (!enumerator.MoveNext())
        {
            return (int)Math.Round((first + second) / 2d);
        }

        int third = enumerator.Current;
        if (first > second)
        {
            (first, second) = (second, first);
        }

        if (second > third)
        {
            (second, third) = (third, second);
        }

        if (first > second)
        {
            (first, second) = (second, first);
        }

        return second;
    }

    private void Poll()
    {
        if (!this.started)
        {
            return;
        }

        if (this.pollInProgress)
        {
            this.ScheduleNextPoll();
            return;
        }

        this.pollInProgress = true;
        int pollGeneration = ++this.pollGeneration;
        try
        {
            Server.ExecuteCommand(
                "status",
                output => ProcessStatusOutput(pollGeneration, output));
            this.server.ScheduleOnce(
                PollTimeoutSeconds.Seconds(),
                () => this.HandlePollTimeout(pollGeneration));
        }
        catch (Exception ex)
        {
            this.pollInProgress = false;
            this.pollGeneration++;
            Console.WriteLine($"[PING] Failed to run server status: {ex}");
            this.ScheduleNextPoll();
        }
    }

    private void ProcessStatusOutput(int pollGeneration, string output)
    {
        if (!this.pollInProgress || pollGeneration != this.pollGeneration)
        {
            return;
        }

        try
        {
            var rowsByName = new Dictionary<string, StatusRow>(
                StringComparer.OrdinalIgnoreCase);
            var ambiguousRowNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string line in output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                var row = ParseStatusRow(line);
                if (row == null || ambiguousRowNames.Contains(row.Name))
                {
                    continue;
                }

                if (!rowsByName.TryAdd(row.Name, row))
                {
                    rowsByName.Remove(row.Name);
                    ambiguousRowNames.Add(row.Name);
                }
            }

            var controllersByName =
                new Dictionary<string, CCitadelPlayerController>(
                    StringComparer.OrdinalIgnoreCase);
            var ambiguousControllerNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var connectedIds = new HashSet<ulong>();
            int playerCount = 0;
            foreach (var player in Players.GetAll())
            {
                if (player.IsBot || player.PlayerSteamId == 0)
                {
                    continue;
                }

                playerCount++;
                connectedIds.Add(player.PlayerSteamId);
                if (ambiguousControllerNames.Contains(player.PlayerName))
                {
                    continue;
                }

                if (!controllersByName.TryAdd(player.PlayerName, player))
                {
                    controllersByName.Remove(player.PlayerName);
                    ambiguousControllerNames.Add(player.PlayerName);
                }
            }

            List<ulong>? staleSampleIds = null;
            foreach (var sample in this.samples)
            {
                if (!connectedIds.Contains(sample.Key))
                {
                    (staleSampleIds ??= new List<ulong>()).Add(sample.Key);
                }
            }

            if (staleSampleIds != null)
            {
                foreach (ulong steamId in staleSampleIds)
                {
                    this.samples.Remove(steamId);
                }
            }

            this.latestReadings.Clear();
            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            foreach (var group in controllersByName)
            {
                if (
                    ambiguousControllerNames.Contains(group.Key) ||
                    ambiguousRowNames.Contains(group.Key) ||
                    !rowsByName.TryGetValue(group.Key, out var statusRow))
                {
                    continue;
                }

                var player = group.Value;
                var samples = GetSamples(player.PlayerSteamId);
                samples.Enqueue(statusRow.PingMs);
                while (samples.Count > SampleCount)
                {
                    samples.Dequeue();
                }

                this.latestReadings[player.PlayerSteamId] = new PingSnapshot(
                    Median(samples),
                    capturedAt);
            }

            if (playerCount > 0 && this.latestReadings.Count == 0)
            {
                if (!this.warnedAboutMissingRows)
                {
                    Console.WriteLine(
                        "[PING] No unambiguous player rows were parsed from server status; " +
                        "ping matchmaking will wait for usable readings.");
                    this.warnedAboutMissingRows = true;
                }
            }
            else
            {
                this.warnedAboutMissingRows = false;
            }

            this.server.Queue.TryStartMatches();
        }
        catch (Exception ex)
        {
            this.latestReadings.Clear();
            Console.WriteLine($"[PING] Failed to process server status output: {ex}");
        }
        finally
        {
            this.pollInProgress = false;
            this.ScheduleNextPoll();
        }
    }

    private void HandlePollTimeout(int pollGeneration)
    {
        if (!this.pollInProgress || pollGeneration != this.pollGeneration)
        {
            return;
        }

        this.pollInProgress = false;
        this.pollGeneration++;
        Console.WriteLine("[PING] Server status did not return output before the timeout.");
        this.ScheduleNextPoll();
    }

    private Queue<int> GetSamples(ulong steamId)
    {
        if (!this.samples.TryGetValue(steamId, out var samples))
        {
            samples = new Queue<int>();
            this.samples[steamId] = samples;
        }

        return samples;
    }

    private static StatusRow? ParseStatusRow(string line)
    {
        int firstQuote = line.IndexOf('\'');
        int lastQuote = line.LastIndexOf('\'');
        if (firstQuote < 0 || lastQuote <= firstQuote)
        {
            return null;
        }

        string[] fields = line[..firstQuote].Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3
            || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || fields[1].Equals("BOT", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(
                fields[2],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int pingMs)
            || pingMs < 0)
        {
            return null;
        }

        string name = line[(firstQuote + 1)..lastQuote].Trim();
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new StatusRow(name, pingMs);
    }

    private void ScheduleNextPoll()
    {
        if (!this.started)
        {
            return;
        }

        // Only the most recently scheduled poll may run. Without this, every /ping
        // (Refresh) and every overlapping poll started an extra polling loop that never
        // ended, so `status` ran more and more often the longer the server was up.
        int token = ++this.scheduledPollToken;
        bool anyoneQueued =
            this.server.RankedQueue.Count > 0 || this.server.UnrankedQueue.Count > 0;
        int interval = anyoneQueued ? PollIntervalSeconds : IdlePollIntervalSeconds;
        this.server.ScheduleOnce(interval.Seconds(), () =>
        {
            if (token == this.scheduledPollToken)
            {
                this.Poll();
            }
        });
    }

    private sealed record StatusRow(string Name, int PingMs);

    private sealed record PingSnapshot(int PingMs, DateTimeOffset CapturedAt);
}
