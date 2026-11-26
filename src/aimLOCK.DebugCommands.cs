namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using DeadworksManaged.Api;

public partial class aimLOCK
{
    [Command(
        "hitdebug",
        Description = "Toggle printing hit group and position for bullet hits on Haze.",
        Permission = "aimlock.debug",
        SuppressChat = true)]
    public void HitDebugCommand(Caller caller)
    {
        if (caller.Player is not { } player)
        {
            caller.Reply("You must be a player to use /hitdebug.");
            return;
        }

        caller.Reply(this.HazeHitboxFilter.ToggleDebug(player)
            ? "Hit debug on: shoot a Haze to see hit groups."
            : "Hit debug off.");
    }

    [Command(
        "lsdebug",
        Description = "Toggle a once-a-second lifesteal report in chat (per-hit details in the server log).",
        Permission = "aimlock.debug",
        SuppressChat = true)]
    public void LifestealDebugCommand(Caller caller)
    {
        if (caller.Player is not { } player)
        {
            caller.Reply("You must be a player to use /lsdebug.");
            return;
        }

        caller.Reply(this.PlayerManager.ToggleLifestealDebug(player)
            ? "Lifesteal debug on: a report prints every second while you deal or take damage."
            : "Lifesteal debug off.");
    }

    [Command(
        "hazehitgroup",
        Description = "Toggle ignoring bullet hits on a Haze hit group: hazehitgroup <id|clear>.",
        Permission = "aimlock.debug",
        SuppressChat = true)]
    public void HazeHitGroupCommand(Caller caller, string? hitGroup = null)
    {
        if (string.Equals(hitGroup, "clear", StringComparison.OrdinalIgnoreCase))
        {
            this.HazeHitboxFilter.ClearBlockedHitGroups();
            caller.Reply("No Haze hit groups are blocked.");
            return;
        }

        if (!int.TryParse(hitGroup, out int id))
        {
            caller.Reply(
                "Blocked Haze hit groups: " +
                (this.HazeHitboxFilter.BlockedHitGroups.Count == 0
                    ? "none"
                    : string.Join(", ", this.HazeHitboxFilter.BlockedHitGroups)) +
                ". Use /hazehitgroup <id> or /hazehitgroup clear.");
            return;
        }

        caller.Reply(this.HazeHitboxFilter.ToggleBlockedHitGroup(id)
            ? $"Bullet hits on Haze hit group {id} are now ignored."
            : $"Haze hit group {id} is no longer ignored.");
    }

    [Command(
        "bottest",
        Description = "Debug: spawn a bare bot: bottest [hero] [lane] [ai] [fx] [rename] [arena] [playerfx].",
        Permission = "aimlock.debug",
        SuppressChat = true)]
    public void BotTestCommand(Caller caller, params string[] arguments)
    {
        // Bare Deadworks bot: no match, rename, teleport, items or effects. Used to
        // find out whether spawning a bot by itself crashes clients.
        var pawn = caller.Player?.GetHeroPawn();
        if (pawn is not { IsValid: true })
        {
            caller.Reply("You need a living hero to use /bottest.");
            return;
        }

        string heroArgument = arguments.Length > 0 ? arguments[0].ToLowerInvariant() : "haze";
        string heroName = heroArgument.StartsWith("hero_", StringComparison.Ordinal)
            ? heroArgument
            : "hero_" + heroArgument;
        if (!HeroTypeExtensions.TryParse(heroName, out var hero))
        {
            caller.Reply($"Unknown hero \"{heroName}\". Try haze or inferno.");
            return;
        }

        bool assignLane = arguments.Any(a => a.Equals("lane", StringComparison.OrdinalIgnoreCase));
        bool keepAi = arguments.Any(a => a.Equals("ai", StringComparison.OrdinalIgnoreCase));
        if (!keepAi && Bots.AiEnabled)
        {
            Bots.SetAiEnabled(false);
        }

        bool Has(string option) =>
            arguments.Any(a => a.Equals(option, StringComparison.OrdinalIgnoreCase));

        // "arena": spawn where /bot puts the bot (an arena far from you) instead of
        // next to you.
        Vector3 spawnPosition = pawn.Position + new Vector3(300f, 0f, 0f);
        if (Has("arena"))
        {
            Vector3 arena = this.DuelLocations[this.Random.Next(this.DuelLocations.Length)];
            spawnPosition = this.PlayerManager.GetDuelArenaCenter(arena) +
                new Vector3(DUELSPAWNOFFSET, 0f, 0f);
        }

        // "playerfx": put the respawn effect on you, like /bot does at match start.
        if (Has("playerfx"))
        {
            this.PlayerManager.CreateDebugEffect(pawn);
        }

        var bot = Bots.Add(
            hero,
            3,
            spawnPosition,
            BotDifficulty.Medium,
            assignLane);
        if (bot is { IsValid: true })
        {
            if (arguments.Any(a => a.Equals("fx", StringComparison.OrdinalIgnoreCase)))
            {
                this.PlayerManager.CreateDebugEffect(bot);
            }

            if (
                arguments.Any(a => a.Equals("rename", StringComparison.OrdinalIgnoreCase)) &&
                bot.Controller is { } botController)
            {
                botController.PlayerName = botController.PlayerName + " (Test)";
            }
        }

        caller.Reply(bot == null
            ? "Bot spawn failed."
            : $"Spawned test bot {heroName} (lane {(assignLane ? "on" : "off")}, AI {(keepAi ? "on" : "off")}). Use /botclear to remove it.");
    }

    [Command(
        "botclear",
        Description = "Debug: kick every bot.",
        Permission = "aimlock.debug",
        SuppressChat = true)]
    public void BotClearCommand(Caller caller)
    {
        this.PlayerManager.RemoveExistingBots();
        caller.Reply("All bots kicked.");
    }

    [Command(
        "fixdebug",
        Description = "List Fixation modifiers (stacks and limit) on you and your duel opponent.",
        Permission = "aimlock.debug",
        SuppressChat = true)]
    public void FixationDebugCommand(Caller caller) => this.PlayerManager.FixationDebugCommand(caller);

    [Command(
        "dumpents",
        Description = "Write every entity designer name on the map to aimlock_entities.txt.",
        Permission = "aimlock.debug",
        SuppressChat = true)]
    public void DumpEntitiesCommand(Caller caller)
    {
        try
        {
            caller.Reply($"Entity list written to {this.MapCleaner.DumpEntities()}.");
        }
        catch (IOException ex)
        {
            caller.Reply("Could not write the entity list.");
            Console.WriteLine($"[aimLOCK] /dumpents failed: {ex}");
        }
    }
}
