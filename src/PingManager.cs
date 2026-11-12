using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
            var statusRows = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseStatusRow)
                .Where(row => row != null)
                .Select(row => row!)
                .ToArray();

            var players = Players.GetAll()
                .Where(player => !player.IsBot && player.PlayerSteamId != 0)
                .ToArray();
            var controllersByName = players
                .GroupBy(player => player.PlayerName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.OrdinalIgnoreCase
                );
            var rowsByName = statusRows
                .GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.ToArray(),
                    StringComparer.OrdinalIgnoreCase
                );

            var connectedIds = players
                .Select(player => player.PlayerSteamId)
                .ToHashSet();
            foreach (ulong steamId in _samples.Keys.Where(id => !connectedIds.Contains(id)).ToArray())
                _samples.Remove(steamId);

            _latestReadings.Clear();
            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            foreach (var group in controllersByName)
            {
                if (group.Value.Length != 1
                    || !rowsByName.TryGetValue(group.Key, out var matchingRows)
                    || matchingRows.Length != 1)
                {
                    continue;
                }

                var player = group.Value[0];
                var samples = GetSamples(player.PlayerSteamId);
                samples.Enqueue(matchingRows[0].PingMs);
                while (samples.Count > SampleCount)
                    samples.Dequeue();

                _latestReadings[player.PlayerSteamId] = new PingSnapshot(
                    Median(samples),
                    capturedAt
                );
            }

            if (players.Length > 0 && _latestReadings.Count == 0)
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

    private static int Median(IEnumerable<int> values)
    {
        int[] sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (int)Math.Round((sorted[middle - 1] + sorted[middle]) / 2d);
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
