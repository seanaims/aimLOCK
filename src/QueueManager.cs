/*
this file is for the queue manager
it parses the various commands, starting matches, finding other players, and setting up the matches
*/

using System;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;
using DeadworksManaged.Api.Sounds;

namespace aimLOCK;

public class QueueManager
{
    private readonly aimLOCK server;

    public QueueManager(aimLOCK server)
    {
        this.server = server;
    }

    public void JoinQueue(Caller caller)
    {
        var controller = caller.Player;

        if (controller == null)
        {
            caller.Reply("You must be a player to join the queue.");
            return;
        }

        if (server.queue.Any(x =>
            StringComparer.OrdinalIgnoreCase.Equals(
                x.PlayerName,
                controller.PlayerName)))
        {
            caller.Reply("You are already in the queue.");
            return;
        }

        server.queue.Add(new QueueEntry(controller.PlayerName));

        server.UIManager.RefreshHud();

        TryStartMatch();

        caller.Reply("You have been added to the queue.");

        Sounds.Play(
            "Damage.Send.Crit",
            RecipientFilter.Single(controller.Slot)
        );

        Console.WriteLine(
            $"{controller.PlayerName} has joined the queue."
        );
    }

    public void LeaveQueue(Caller caller)
    {
        var controller = caller.Player;

        if (controller == null)
            return;

        var player = server.queue.FirstOrDefault(x =>
            StringComparer.OrdinalIgnoreCase.Equals(
                x.PlayerName,
                controller.PlayerName));

        if (player == null)
        {
            caller.Reply("You are not currently in the queue.");
            return;
        }

        server.queue.Remove(player);

        server.UIManager.RefreshHud();

        caller.Reply("You have left the queue.");

        Console.WriteLine(
            $"{controller.PlayerName} has left the queue."
        );
    }

    public void TryStartMatch()
    {
        Console.WriteLine($"Queue Count: {server.queue.Count}");

        if (server.queue.Count < 2)
            return;

        var player1 = Players.GetAll()
            .FirstOrDefault(p =>
                p.PlayerName == server.queue[0].PlayerName);

        var player2 = Players.GetAll()
            .FirstOrDefault(p =>
                p.PlayerName == server.queue[1].PlayerName);

        if (player1 == null || player2 == null)
        {
            Console.WriteLine("Player lookup failed.");
            return;
        }

        Console.WriteLine(
            $"Starting match: {player1.PlayerName} vs {player2.PlayerName}"
        );

        player1.ChangeTeam(2);
        player2.ChangeTeam(3);

        server.ScheduleOnce(0.25.Seconds(), () =>
        {
            var pawn1 = player1.GetHeroPawn();
            var pawn2 = player2.GetHeroPawn();

            if (pawn1 == null || pawn2 == null)
            {
                Console.WriteLine("A player pawn is null.");
                return;
            }

            Vector3 arena =
                server.DuelLocations[
                    server.random.Next(server.DuelLocations.Length)
                ];

            pawn1.Teleport(
                arena + new Vector3(-200f, 0f, 0f),
                new Vector3(0f, 45f, 0f),
                null
            );

            pawn2.Teleport(
                arena + new Vector3(200f, 0f, 0f),
                new Vector3(0f, 180f, 0f),
                null
            );

            // Lifesteal
            pawn1.AddModifier(
                "upgrade_vampire/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BulletLifestealPercent"] = aimLOCK.DUEL_LIFESTEAL
                }
            );

            pawn2.AddModifier(
                "upgrade_vampire/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BulletLifestealPercent"] = aimLOCK.DUEL_LIFESTEAL
                }
            );

            // Bonus Health
            pawn1.AddModifier(
                "upgrade_health/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BonusHealth"] = aimLOCK.DUEL_BONUS_HEALTH
                }
            );

            pawn2.AddModifier(
                "upgrade_health/modifier_intrinsic_base",
                abilityValues: new()
                {
                    ["BonusHealth"] = aimLOCK.DUEL_BONUS_HEALTH
                }
            );

            // Glass Cannon
            pawn1.AddModifier(
                "upgrade_glass_cannon/modifier_glass_cannon_intrinstic",
                new System.Collections.Generic.Dictionary<string, float>
                {
                    ["MaxHealthLossPercent"] = aimLOCK.DUEL_GLASS_CANNON_HEALTH_LOSS,
                    ["BaseAttackDamagePercent"] = aimLOCK.DUEL_GLASS_CANNON_DAMAGE
                }
            );

            pawn2.AddModifier(
                "upgrade_glass_cannon/modifier_glass_cannon_intrinstic",
                new System.Collections.Generic.Dictionary<string, float>
                {
                    ["MaxHealthLossPercent"] = aimLOCK.DUEL_GLASS_CANNON_HEALTH_LOSS,
                    ["BaseAttackDamagePercent"] = aimLOCK.DUEL_GLASS_CANNON_DAMAGE
                }
            );

            pawn1.AddItem("upgrade_infinite_rounds");
            pawn2.AddItem("upgrade_infinite_rounds");

            var match = new DuelMatch
            {
                Player1 = player1.PlayerName,
                Player2 = player2.PlayerName,
                ArenaPosition = arena,
                Score1 = 0,
                Score2 = 0
            };

            server.ActiveMatches[player1.PlayerName] = match;
            server.ActiveMatches[player2.PlayerName] = match;

            server.UIManager.ShowMatchHud(match);

            server.UIManager.HideQueuePanel(player1, player2);

            Console.WriteLine(
                $"Match Found! {player1.PlayerName} vs {player2.PlayerName}"
            );
        });

        server.queue.RemoveAt(1);
        server.queue.RemoveAt(0);

        server.UIManager.RefreshHud();
    }
}