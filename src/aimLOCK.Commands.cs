using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using DeadworksManaged.Api;

namespace aimLOCK;

public partial class aimLOCK
{
    /* Queue Commands */

    [Command(
        "help",
        "commands",
        Description = "List aimLOCK server commands: help [page].",
        SuppressChat = true
    )]
    public void HelpCommand(Caller caller, int page = 1)
    {
        if (page == 1)
        {
            caller.Reply("aimLOCK server commands (page 1/3):");
            caller.Reply("This is a 1v1 duel server: ranked tracks Elo; unranked is casual.");
            caller.Reply("/join <ranked|unranked> (alias: /queue) - Join that queue.");
            caller.Reply("/leave - Leave your current queue.");
            caller.Reply("/pbmm <on|off> - Toggle ping-based matchmaking for your queue matches.");
            caller.Reply("Use /help 2 for stats and Elo, or /help 3 for duels, arenas, and other commands.");
        }
        else if (page == 2)
        {
            caller.Reply("aimLOCK server commands (page 2/3):");
            caller.Reply("/stats [player] - View unranked wins, losses, and win rate.");
            caller.Reply("/elo - View ranked Elo, wins, losses, and win rate.");
            caller.Reply("/ping - View your server-reported ping.");
            caller.Reply("/whois <player name> - Look up a player's SteamID64 (permission required).");
            caller.Reply("/resetelo <player name> - Reset Elo to 1000 and ranked wins/losses to 0-0 (permission required).");
            caller.Reply("/resetranked <player name> (alias: /resetrankedrecord) - Reset ranked wins/losses, keeping Elo (permission required).");
            caller.Reply("/resetunranked <player name> (alias: /resetunrankedrecord) - Reset unranked wins/losses (permission required).");
            caller.Reply("Use an exact name or unique name prefix. Use /help 1 for queueing or /help 3 for other commands.");
        }
        else if (page == 3)
        {
            caller.Reply("aimLOCK server commands (page 3/3):");
            caller.Reply("/duel <player name> [arena name] - Request a duel; omit the arena for a random map.");
            caller.Reply("/accept - Accept a pending duel request.");
            caller.Reply("/bot [easy|medium|hard] - Start a bot match (default: hard).");
            caller.Reply("/botspeed <units/sec> - Set the practice bot's strafe speed (100-2500; default 2500).");
            caller.Reply("/arena [number|name] - List arenas or teleport to one.");
            caller.Reply("/spectate (/spec) list | <player name> - List active matches or spectate one until it ends; the Midtown wall shows arena, queue, players, and score.");
            caller.Reply("/tp <player name|arena number|name> - Teleport to a player or arena (permission required).");
            caller.Reply("Arena names: clock, boxes, stocks, forest, railroad, bridge.");
            caller.Reply("More arenas: cola, tunnel, purple, sewer, front_door, clock2.");
            caller.Reply("/spawn - Return to spawn after a 2-second countdown; forfeits an active duel.");
            caller.Reply("/scoreboard <on|off> - Show or hide the server info UI.");
            caller.Reply("/timezone [IANA ID] - View or set your local time zone.");
            caller.Reply("/tag <tagname|clear> - Equip or clear your tag; /tag list shows your available tags.");
            caller.Reply("/tag give|revoke <player|SteamID64> <tagname> - Grant or take away tag access (aimlock.tags.manage).");
            caller.Reply("/tagoff - Hide your own overhead tag.");
            caller.Reply("/tagon - Show your overhead tag again.");
            caller.Reply("Moderation uses the Deadworks Admin plugin: /kick, /ban, /unban, /gag (text chat), /mute (voice), /who (permission required).");
            caller.Reply("/fixationmax - Fully upgrade Fixation with a 999-stack cap for all online players (permission required).");
            caller.Reply("/guild help - Create and manage guilds, invitations, rosters, and access settings.");
            caller.Reply("/leaderboardmove <x> <y> <z> - Move the leaderboard (permission required).");
            caller.Reply("Use /help 1 for queueing or /help 2 for stats, Elo, and stat resets.");
        }
        else
        {
            caller.Reply("Help pages are 1-3. Use /help, /help 2, or /help 3.");
        }
    }

    [Command(
        "bot",
        Description = "Start a bot match: bot [easy|medium|hard] (default: hard).",
        SuppressChat = true
    )]
    public void BotCommand(Caller caller, string? difficulty = "hard")
    {
        PlayerManager.StartPracticeBotMatch(caller, difficulty);
    }

    [Command(
        "fixationmax",
        Description = "Unlock and fully upgrade Fixation with a 999-stack cap in Signature3 for all online players.",
        Permission = "aimlock.fixation.max",
        SuppressChat = true
    )]
    public void FixationMaxCommand(Caller caller)
    {
        int updatedPlayers = PlayerManager.MaxFixationForOnlinePlayers();
        caller.Reply(
            $"Fully upgraded Fixation with a 999-stack cap for {updatedPlayers} online player(s)."
        );
    }

    [Command(
        "botmirror",
        Description = "Toggle whether the practice bot copies the items you buy during a bot match (on by default).",
        SuppressChat = true
    )]
    public void BotMirrorCommand(Caller caller)
    {
        if (caller.Player is not { } player)
        {
            caller.Reply("You must be a player to use /botmirror.");
            return;
        }

        caller.Reply(PlayerManager.ToggleBotMirror(player)
            ? "Bot mirror on: the bot buys every item you buy in a bot match."
            : "Bot mirror off: the bot keeps its items when you buy.");
    }

    [Command(
        "ab",
        "allowbuy",
        Description = "Allow buying items during your duels: ab <on|off> (off by default). Both players need it on.",
        SuppressChat = true
    )]
    public void AllowBuyCommand(Caller caller, string? setting = null)
    {
        if (caller.Player is not { } player)
        {
            caller.Reply("You must be a player to use /ab.");
            return;
        }

        bool allow;
        if (string.Equals(setting, "on", StringComparison.OrdinalIgnoreCase))
            allow = true;
        else if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
            allow = false;
        else
        {
            caller.Reply(
                $"Use /ab on or /ab off. It is {(PlayerManager.AllowsDuelBuying(player) ? "on" : "off")} for you."
            );
            return;
        }

        PlayerManager.SetAllowDuelBuying(player, allow);
        caller.Reply(
            (allow
                ? "Buying in duels on. It applies when both players have it on."
                : "Buying in duels off.") +
            (PlayerManager.TryGetActiveMatch(player, out _) ? " This takes effect from your next duel." : "")
        );
    }

    [Command(
        "hidebots",
        Description = "Test: hidebots <off|hero|all> hides practice bots from players outside their match (off is the default).",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void HideBotsCommand(Caller caller, string? setting = null)
    {
        PlayerManager.BotHideMode mode;
        switch (setting?.Trim().ToLowerInvariant())
        {
            case "off": mode = PlayerManager.BotHideMode.Off; break;
            case "hero": mode = PlayerManager.BotHideMode.Hero; break;
            case "all": mode = PlayerManager.BotHideMode.All; break;
            default:
                caller.Reply(
                    $"Use /hidebots off, /hidebots hero or /hidebots all. It is {PlayerManager.PracticeBotHideMode.ToString().ToLowerInvariant()}."
                );
                return;
        }

        PlayerManager.SetPracticeBotHideMode(mode);
        caller.Reply($"Practice bot hiding: {setting!.Trim().ToLowerInvariant()}. Resets to off on server restart.");
    }

    [Command(
        "hidepassive",
        Description = "Crash test: hidepassive <on|off> keeps the Fixation passive off the clients (on, the default) or sends it (off).",
        Permission = "aimlock.debug",
        SuppressChat = true
    )]
    public void HidePassiveCommand(Caller caller, string? setting = null)
    {
        bool hide;
        if (string.Equals(setting, "on", StringComparison.OrdinalIgnoreCase))
            hide = true;
        else if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
            hide = false;
        else
        {
            caller.Reply(
                $"Use /hidepassive on or /hidepassive off. It is {(PlayerManager.HideFixationPassive ? "on" : "off")}."
            );
            return;
        }

        int updated = PlayerManager.SetHideFixationPassive(hide);
        caller.Reply(
            (hide
                ? "Fixation passive hidden again (no HUD icon)"
                : "Fixation passive now sent to clients (the HUD icon comes back)") +
            $"; Fixation redone for {updated} hero(es). Resets on server restart."
        );
    }

    [Command(
        "botspeed",
        Description = "Set the practice bot's strafe speed: botspeed <100-2500 units/sec>.",
        SuppressChat = true
    )]
    public void BotSpeedCommand(Caller caller, float speed)
    {
        PlayerManager.SetPracticeBotStrafeSpeed(caller, speed);
    }

    [Command(
        "guild",
        Description = "Create and manage player guilds: guild help.",
        SuppressChat = true
    )]
    public void GuildCommand(Caller caller, params string[] arguments)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("Guild commands are only available to players.");
            return;
        }

        try
        {
            string response = GuildManager.ExecuteCommand(player, arguments);
            if (
                arguments.Length > 0 &&
                arguments[0].ToLowerInvariant() is
                    "create" or "join" or "accept" or "leave" or "disband" or
                    "mode" or "access" or "newtag" or "rename" or "kick" or
                    "transfer"
            )
            {
                LeaderboardManager.Refresh();
                PlayerManager.RefreshArenaScoreboards();
                PlayerTagManager.RefreshGuildTags();
            }

            foreach (string line in response.Split('\n'))
                caller.Reply(line);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to save guild data: {ex}");
            caller.Reply("Could not save the guild change.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"Access denied saving guild data: {ex}");
            caller.Reply("Could not save the guild change.");
        }
    }

    [Command("join", "queue", Description = "Join a queue: join <ranked|unranked>.")]
    public void JoinQueue(Caller caller, string queueType)
    {
        MatchQueueType parsedQueueType;
        if (string.Equals(queueType, "ranked", StringComparison.OrdinalIgnoreCase))
        {
            parsedQueueType = MatchQueueType.Ranked;
        }
        else if (string.Equals(queueType, "unranked", StringComparison.OrdinalIgnoreCase))
        {
            parsedQueueType = MatchQueueType.Unranked;
        }
        else
        {
            caller.Reply("Choose a queue: /join ranked or /join unranked.");
            return;
        }

        Queue.JoinQueue(caller, parsedQueueType);
    }

    [Command("leave", Description = "Remove player from queue.")]
    public void LeaveQueue(Caller caller)
    {
        Queue.LeaveQueue(caller);
    }

    [Command(
        "pbmm",
        Description = "Enable or disable ping-based matchmaking: pbmm <on|off>.",
        SuppressChat = true
    )]
    public void PingBasedMatchmakingCommand(Caller caller, string setting)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to change this setting.");
            return;
        }

        bool enabled;
        if (string.Equals(setting, "on", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
        }
        else if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
        }
        else
        {
            caller.Reply("Choose a setting: /pbmm on or /pbmm off.");
            return;
        }

        try
        {
            PlayerManager.SetPingBasedMatchmakingEnabled(player, enabled);
            caller.Reply(
                enabled
                    ? "Ping-based matchmaking enabled."
                    : "Ping-based matchmaking disabled; you can be matched with any queued player."
            );
            Queue.TryStartMatches();
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to update ping-based matchmaking for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your ping-based matchmaking setting.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.WriteLine(
                $"Invalid Steam ID for ping-based matchmaking setting: {ex}"
            );
            caller.Reply("Your player account is not ready; try again shortly.");
        }
    }

    [Command(
        "handicap",
        "handi",
        Description = "Set your own duel lifesteal percent: handicap <number> (handicap off to clear).",
        SuppressChat = true
    )]
    public void HandicapCommand(Caller caller, string? value = null)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to change this setting.");
            return;
        }

        float duelPercent = PlayerManager.DuelLifestealPercent;
        float? percent;
        if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            percent = null;
        }
        else if (TryParsePercent(value, out float parsed) && parsed <= duelPercent)
        {
            percent = parsed;
        }
        else
        {
            float? current = PlayerManager.GetHandicapLifestealPercent(player);
            caller.Reply(
                $"Use /handicap <number from 0 to {duelPercent:0.#}>, for example /handi {duelPercent:0.#}, " +
                "or /handicap off. " +
                (current is { } currentPercent
                    ? $"Your duel lifesteal is set to {MathF.Min(currentPercent, duelPercent):0.#}%."
                    : $"You have no handicap; your duel lifesteal is {duelPercent:0.#}%.")
            );
            return;
        }

        try
        {
            PlayerManager.SetHandicapLifestealPercent(player, percent);
            caller.Reply(
                percent is { } saved
                    ? $"Handicap set. Your duel lifesteal is {saved:0.#}% until you change it."
                    : $"Handicap off. Your duel lifesteal is back to {duelPercent:0.#}%."
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Failed to update handicap for {player.PlayerName}: {ex}"
            );
            caller.Reply("Could not save your handicap setting.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Console.WriteLine($"Invalid Steam ID for handicap setting: {ex}");
            caller.Reply("Your player account is not ready; try again shortly.");
        }
    }

    [Command(
        "lifesteal",
        "ls",
        Description = "Set the lifesteal percent for duels and bot matches: lifesteal <number>.",
        Permission = "aimlock.lifesteal",
        SuppressChat = true
    )]
    public void LifestealCommand(Caller caller, string? value = null)
    {
        if (!TryParsePercent(value, out float percent))
        {
            caller.Reply(
                "Use /lifesteal <number from 0 to 100>, for example /ls 85. " +
                $"Duel and bot lifesteal is {PlayerManager.DuelLifestealPercent:0.#}%."
            );
            return;
        }

        PlayerManager.SetDuelLifestealPercent(percent);
        caller.Reply(
            $"Duel and bot lifesteal set to {percent:0.#}% until the server restarts. " +
            "Lifesteal outside duels stays at 100%."
        );
    }

    private static bool TryParsePercent(string? value, out float percent)
    {
        percent = 0f;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return float.TryParse(
                value.Trim().TrimEnd('%'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out percent
            ) &&
            percent >= 0f &&
            percent <= 100f;
    }

    [Command("spawn", Description = "Return to spawn after a 2-second countdown or forfeit an active duel (instant in bot matches).")]
    public void SpawnCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to return to spawn.");
            return;
        }

        if (PlayerManager.TryGetActiveMatch(player, out var activeMatch))
        {
            bool isBotMatch =
                PlayerManager.FindMatchParticipant(activeMatch.Player1)?.IsBot == true ||
                PlayerManager.FindMatchParticipant(activeMatch.Player2)?.IsBot == true;

            // Bot matches record nothing, so leaving one needs no countdown.
            if (isBotMatch)
            {
                ForfeitToSpawn();
                return;
            }

            caller.Reply("Returning to spawn in 2...");
            ScheduleOnce(1.Seconds(), () =>
                caller.Reply("Returning to spawn in 1..."));
            ScheduleOnce(2.Seconds(), ForfeitToSpawn);
            return;

            void ForfeitToSpawn()
            {
                if (!player.IsValid)
                    return;

                if (
                    PlayerManager.TryGetActiveMatch(player, out var currentMatch) &&
                    ReferenceEquals(currentMatch, activeMatch) &&
                    PlayerManager.ForfeitMatch(
                        PlayerManager.GetMatchParticipantName(player)
                    )
                )
                {
                    UIManager.RestoreServerInfoAfterSpawn(player);
                    if (!isBotMatch)
                        caller.Reply("Forfeit recorded as a loss. The match has ended.");
                }
                else
                {
                    caller.Reply("The match is already ending; /spawn was not used.");
                }
            }
        }

        var pawn = player.GetHeroPawn();
        if (pawn == null)
        {
            caller.Reply("Your hero is not available to teleport.");
            return;
        }

        caller.Reply("Returning to spawn in 2...");
        ScheduleOnce(1.Seconds(), () => caller.Reply("Returning to spawn in 1..."));
        ScheduleOnce(2.Seconds(), () =>
        {
            if (!player.IsValid || !pawn.IsValid)
                return;

            if (PlayerManager.TryGetActiveMatch(player, out _))
            {
                caller.Reply("You joined a match; /spawn was cancelled.");
                return;
            }

            PlayerManager.StopSpectating(player);
            PlayerManager.EnsureTeam(player, 2);
            pawn.Teleport(MidtownSpawnPosition, null, null);
            UIManager.RestoreServerInfoAfterSpawn(player);
            caller.Reply("Returned to spawn.");
        });
    }

    [Command(
        "scoreboard",
        Description = "Show or hide the server info UI: scoreboard <on|off>.",
        SuppressChat = true
    )]
    public void ScoreboardCommand(Caller caller, string state)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to change the server info UI.");
            return;
        }

        bool visible;
        if (string.Equals(state, "on", StringComparison.OrdinalIgnoreCase))
        {
            visible = true;
        }
        else if (string.Equals(state, "off", StringComparison.OrdinalIgnoreCase))
        {
            visible = false;
        }
        else
        {
            caller.Reply("Use /scoreboard on or /scoreboard off.");
            return;
        }

        UIManager.SetServerInfoVisibility(player, visible);
        caller.Reply(
            $"Server info UI turned {(visible ? "on" : "off")}."
        );
    }

    [Command("stats", Description = "View player stats.")]
    public void StatsCommand(Caller caller, string? targetName = null)
    {
        PlayerManager.StatsCommand(caller, targetName);
    }

    [Command(
        "elo",
        Description = "Check your ranked Elo rating.",
        SuppressChat = true
    )]
    public void EloCommand(Caller caller)
    {
        var player = caller.Player;

        if (player == null)
        {
            caller.Reply("You must be a player to check your Elo.");
            return;
        }

        RankedEloStats stats = RankedElo.GetStats(
            player.PlayerSteamId.ToString(),
            player.PlayerName
        );
        int rankedGames = stats.Wins + stats.Losses;
        float rankedWinRate = rankedGames > 0
            ? (float)stats.Wins / rankedGames * 100f
            : 0f;

        caller.Reply(
            $"[{player.PlayerName}] Ranked | " +
            $"Record: {stats.Wins}-{stats.Losses} | " +
            $"Elo: {stats.Rating} | WR: {rankedWinRate:F1}%"
        );
    }

    [Command(
        "ping",
        Description = "View your server-reported ping.",
        SuppressChat = true
    )]
    public void PingCommand(Caller caller)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to check your ping.");
            return;
        }

        if (PingManager.TryGetPing(player, out int pingMs))
        {
            caller.Reply($"[PING] {pingMs} ms (server-reported).");
            return;
        }

        PingManager.Refresh();
        caller.Reply(
            "[PING] No fresh, unambiguous server reading is available yet. " +
            "Try /ping again shortly."
        );
    }

    [Command(
        "timezone",
        Description = "Show or set your timezone: timezone <IANA ID>.",
        SuppressChat = true
    )]
    public void TimeZoneCommand(Caller caller, string? ianaTimeZoneId = null)
    {
        var player = caller.Player;
        if (player == null)
        {
            caller.Reply("You must be a player to check or set your timezone.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ianaTimeZoneId))
        {
            Chat.PrintToChat(
                player,
                $"[TIME] Timezone: {PlayerManager.GetTimeZoneId(player.PlayerSteamId)}. " +
                "Set it with /timezone <IANA ID>."
            );
            return;
        }

        try
        {
            PlayerManager.SetTimeZone(player, ianaTimeZoneId);
            UIManager.ShowQueuePanel(player);
            Chat.PrintToChat(player, $"[TIME] Timezone set to {ianaTimeZoneId}.");
        }
        catch (TimeZoneNotFoundException)
        {
            Chat.PrintToChat(
                player,
                $"[TIME] Unknown IANA timezone: {ianaTimeZoneId}."
            );
        }
        catch (InvalidTimeZoneException ex)
        {
            Console.WriteLine($"Invalid timezone data for {ianaTimeZoneId}: {ex}");
            Chat.PrintToChat(
                player,
                "[TIME] That timezone is unavailable on the server."
            );
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Failed to save timezone for {player.PlayerName}: {ex}");
            Chat.PrintToChat(player, "[TIME] Could not save your timezone setting.");
        }
    }
}
