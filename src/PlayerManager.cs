/*
This file handels player activity and matches
forces haze, bot spawning, player connections, score tracking, match completion, respawning
*/

using System;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

public class PlayerManager
{
    private readonly aimLOCK server;

    public PlayerManager(aimLOCK server)
    {
        this.server = server;
    }

    public void OnPlayerConnect(ClientFullConnectEvent args)
    {
        if (args.Controller is not { } controller)
            return;

        Console.WriteLine(
            $"Forcing Haze for {controller.PlayerName}"
        );

        controller.ChangeTeam(2);
        controller.SelectHero(Heroes.Haze);

        // Spawn testing bot
        if (!server.botSpawned)
        {
            server.botSpawned = true;

            var pawn = controller.GetHeroPawn();
            var spawn = pawn?.Position ?? new Vector3(0, 45f, 0);

            var bot = Bots.Add(
                Heroes.Haze,
                team: 2,
                spawn,
                BotDifficulty.Hard
            );

            if (bot == null)
            {
                Console.WriteLine("BOT FAILED TO SPAWN");
            }
            else
            {
                string botName =
                    bot.Controller?.PlayerName ?? "Bot";

                if (!server.queue.Any(x =>
                    StringComparer.OrdinalIgnoreCase.Equals(
                        x.PlayerName,
                        botName)))
                {
                    server.queue.Add(new QueueEntry(botName));

                    server.UIManager.RefreshHud();
                    server.Queue.TryStartMatch();
                }

                Console.WriteLine(
                    $"Bot {botName} added to queue."
                );
            }
        }

        server.UIManager.ShowQueuePanel(controller);
    }

    public HookResult OnPlayerDeath(PlayerDeathEvent args)
    {
        var victim = args.UseridController;
        var killer = args.AttackerController;

        if (victim == null || killer == null)
            return HookResult.Continue;

        if (!server.ActiveMatches.TryGetValue(
            victim.PlayerName,
            out var match))
        {
            return HookResult.Continue;
        }

        if (killer.PlayerName == match.Player1)
            match.Score1++;

        if (killer.PlayerName == match.Player2)
            match.Score2++;

        server.UIManager.UpdateMatchHud(match);

        if (match.Finished || match.RespawnPending)
            return HookResult.Continue;

        if (match.Score1 >= aimLOCK.WIN_SCORE ||
            match.Score2 >= aimLOCK.WIN_SCORE)
        {
            match.Finished = true;

            server.ScheduleOnce(3.Seconds(), () =>
            {
                FinishMatch(match);
            });

            return HookResult.Continue;
        }

        match.RespawnPending = true;

        server.ScheduleOnce(2.Seconds(), () =>
        {
            RespawnIntoArena(match);
            match.RespawnPending = false;
        });

        return HookResult.Continue;
    }

    private void RespawnIntoArena(DuelMatch match)
    {
        var player1 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player1);

        var player2 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player2);

        var pawn1 = player1?.GetHeroPawn();
        var pawn2 = player2?.GetHeroPawn();

        if (pawn1 == null || pawn2 == null)
            return;

        pawn1.Teleport(
            match.ArenaPosition + new Vector3(-200f, 0f, 0f),
            new Vector3(0f, 45f, 0f),
            null
        );

        pawn2.Teleport(
            match.ArenaPosition + new Vector3(200f, 0f, 0f),
            new Vector3(0f, 180f, 0f),
            null
        );
    }

    private void FinishMatch(DuelMatch match)
    {
        var winner =
            match.Score1 > match.Score2
                ? match.Player1
                : match.Player2;

        Console.WriteLine($"Match Over! Winner: {winner}");

        var player1 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player1);

        var player2 = Players.GetAll()
            .FirstOrDefault(x => x.PlayerName == match.Player2);

        string winnerText = $"Winner: {winner}";

        server.UIManager.ShowWinner(match, player1, player2, winnerText);

        Vector3 spawn = new Vector3(-1202, -10120, 1323);

        // Remove modifiers
        player1?.GetHeroPawn()?.RemoveModifier(
            "upgrade_vampire/modifier_intrinsic_base"
        );

        player2?.GetHeroPawn()?.RemoveModifier(
            "upgrade_vampire/modifier_intrinsic_base"
        );

        server.ActiveMatches.Remove(match.Player1);
        server.ActiveMatches.Remove(match.Player2);

        // Default teams
        player1?.ChangeTeam(2);
        player2?.ChangeTeam(2);

        server.ScheduleOnce(2.Seconds(), () =>
        {
            server.UIManager.HideMatchHud(player1, player2);

            player1?.GetHeroPawn()?.Teleport(spawn, null, null);
            player2?.GetHeroPawn()?.Teleport(spawn, null, null);

            server.UIManager.RefreshHud();
        });
    }
}
