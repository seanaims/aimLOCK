using System;
using System.Collections.Generic;
using System.Globalization;

using DeadworksManaged.Api;

namespace aimLOCK;

public sealed class PingManager
{
    private const int PollIntervalSeconds = 5;
    private const int PollTimeoutSeconds = 2;
    private static readonly TimeSpan ReadingLifetime = TimeSpan.FromSeconds(15);
    private const int SampleCount = 3;

    private readonly aimLOCK server;
    private readonly Dictionary<ulong, Queue<int>> _samples = new();
    private readonly Dictionary<ulong, PingSnapshot> _latestReadings = new();
    private bool _started;
    private bool _pollInProgress;
    private bool _warnedAboutMissingRows;
    private int _pollGeneration;

    public PingManager(aimLOCK server)
    {
        this.server = server;
    }

    public void Start()
    {
        if (_started)
            return;

        _started = true;
        server.ScheduleOnce(2.Seconds(), Poll);
    }

    public void Refresh()
    {
        if (_started && !_pollInProgress)
            Poll();
    }

    public bool TryGetPing(CCitadelPlayerController player, out int pingMs)
    {
        pingMs = 0;
        if (player.IsBot || player.PlayerSteamId == 0)
            return false;

        if (!_latestReadings.TryGetValue(player.PlayerSteamId, out var reading))
            return false;

        if (DateTimeOffset.UtcNow - reading.CapturedAt > ReadingLifetime)
            return false;

        pingMs = reading.PingMs;
        return true;
    }

    private void Poll()
    {
        if (!_started)
            return;

        if (_pollInProgress)
        {
            ScheduleNextPoll();
            return;
        }

        _pollInProgress = true;
        int pollGeneration = ++_pollGeneration;
        try
        {
            Server.ExecuteCommand(
                "status",
                output => ProcessStatusOutput(pollGeneration, output)
            );
            server.ScheduleOnce(
                PollTimeoutSeconds.Seconds(),
                () => HandlePollTimeout(pollGeneration)
            );
        }
        catch (Exception ex)
        {
            _pollInProgress = false;
            _pollGeneration++;
            Console.WriteLine($"[PING] Failed to run server status: {ex}");
            ScheduleNextPoll();
        }
    }

    private void ProcessStatusOutput(int pollGeneration, string output)
    {
        if (!_pollInProgress || pollGeneration != _pollGeneration)
            return;

        try
        {
            var rowsByName = new Dictionary<string, StatusRow>(
                StringComparer.OrdinalIgnoreCase
            );
            var ambiguousRowNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (string line in output.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries
            ))
            {
                var row = ParseStatusRow(line);
                if (row == null || ambiguousRowNames.Contains(row.Name))
                    continue;

                if (!rowsByName.TryAdd(row.Name, row))
                {
                    rowsByName.Remove(row.Name);
                    ambiguousRowNames.Add(row.Name);
                }
            }

            var controllersByName =
                new Dictionary<string, CCitadelPlayerController>(
                    StringComparer.OrdinalIgnoreCase
                );
            var ambiguousControllerNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );
            var connectedIds = new HashSet<ulong>();
            int playerCount = 0;
            foreach (var player in Players.GetAll())
            {
                if (player.IsBot || player.PlayerSteamId == 0)
                    continue;

                playerCount++;
                connectedIds.Add(player.PlayerSteamId);
                if (ambiguousControllerNames.Contains(player.PlayerName))
                    continue;

                if (!controllersByName.TryAdd(player.PlayerName, player))
                {
                    controllersByName.Remove(player.PlayerName);
                    ambiguousControllerNames.Add(player.PlayerName);
                }
            }

            var staleSampleIds = new List<ulong>();
            foreach (var sample in _samples)
            {
                if (!connectedIds.Contains(sample.Key))
                    staleSampleIds.Add(sample.Key);
            }
            foreach (ulong steamId in staleSampleIds)
                _samples.Remove(steamId);

            _latestReadings.Clear();
            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            foreach (var group in controllersByName)
            {
                if (
                    ambiguousControllerNames.Contains(group.Key) ||
                    ambiguousRowNames.Contains(group.Key) ||
                    !rowsByName.TryGetValue(group.Key, out var statusRow)
                )
                {
                    continue;
                }

                var player = group.Value;
                var samples = GetSamples(player.PlayerSteamId);
                samples.Enqueue(statusRow.PingMs);
                while (samples.Count > SampleCount)
                    samples.Dequeue();

                _latestReadings[player.PlayerSteamId] = new PingSnapshot(
                    Median(samples),
                    capturedAt
                );
            }

            if (playerCount > 0 && _latestReadings.Count == 0)
            {
                if (!_warnedAboutMissingRows)
                {
                    Console.WriteLine(
                        "[PING] No unambiguous player rows were parsed from server status; " +
                        "ping matchmaking will wait for usable readings."
                    );
                    _warnedAboutMissingRows = true;
                }
            }
            else
            {
                _warnedAboutMissingRows = false;
            }

            server.Queue.TryStartMatches();
        }
        catch (Exception ex)
        {
            _latestReadings.Clear();
            Console.WriteLine($"[PING] Failed to process server status output: {ex}");
        }
        finally
        {
            _pollInProgress = false;
            ScheduleNextPoll();
        }
    }

    private void HandlePollTimeout(int pollGeneration)
    {
        if (!_pollInProgress || pollGeneration != _pollGeneration)
            return;

        _pollInProgress = false;
        _pollGeneration++;
        Console.WriteLine("[PING] Server status did not return output before the timeout.");
        ScheduleNextPoll();
    }

    private Queue<int> GetSamples(ulong steamId)
    {
        if (!_samples.TryGetValue(steamId, out var samples))
        {
            samples = new Queue<int>();
            _samples[steamId] = samples;
        }

        return samples;
    }

    private static int Median(Queue<int> values)
    {
        using var enumerator = values.GetEnumerator();
        enumerator.MoveNext();
        int first = enumerator.Current;
        if (!enumerator.MoveNext())
            return first;

        int second = enumerator.Current;
        if (!enumerator.MoveNext())
            return (int)Math.Round((first + second) / 2d);

        int third = enumerator.Current;
        if (first > second)
            (first, second) = (second, first);
        if (second > third)
            (second, third) = (third, second);
        if (first > second)
            (first, second) = (second, first);
        return second;
    }

    private static StatusRow? ParseStatusRow(string line)
    {
        int firstQuote = line.IndexOf('\'');
        int lastQuote = line.LastIndexOf('\'');
        if (firstQuote < 0 || lastQuote <= firstQuote)
            return null;

        string[] fields = line[..firstQuote].Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries
        );
        if (fields.Length < 3
            || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || fields[1].Equals("BOT", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(
                fields[2],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int pingMs
            )
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
        if (_started)
            server.ScheduleOnce(PollIntervalSeconds.Seconds(), Poll);
    }

    private sealed record StatusRow(string Name, int PingMs);
    private sealed record PingSnapshot(int PingMs, DateTimeOffset CapturedAt);
}
