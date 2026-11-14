# aimLOCK

aimLOCK is a Deadlock server plugin for 1v1 matchmaking, practice-bot duels, player and guild leaderboards, and server-side quality-of-life features.

## Matchmaking

Join a queue with `/join ranked` or `/join unranked` (the alias `/queue` also works). Use `/leave` to leave before a match is found. Players matched together receive the match-found notice and 3-2-1/FIGHT countdown; other players on the server do not.

Matchmaking can use server-reported ping readings from the server-console `status` command. It uses the median of up to three recent readings and requires an unambiguous player-name match. The initial maximum ping difference is 6 ms; the window widens by 3 ms every 15 seconds to 60 ms, and after two minutes any ping difference is allowed to avoid leaving players queued indefinitely. Use `/pbmm off` to opt out of ping filtering for your matches, or `/pbmm on` to enable it again. The setting is saved per player. Practice-bot matches are not ping-filtered.

When a match ends, only that duel's participants and its registered spectators are returned to Midtown spawn. A player who disconnects is removed with their hero and controlled pawns; leaving a duel records a forfeit. Server-info UI is restored after matches, and the duel countdown is private to the participants.

## Commands

Commands are entered in chat with a leading `/`. `/help [page]` and `/commands [page]` show the in-game command guide.

### Queues, matches, and movement

| Command | Description |
| --- | --- |
| `/join <ranked\|unranked>` or `/queue <ranked\|unranked>` | Join ranked or unranked matchmaking. |
| `/leave` | Leave your current queue. |
| `/pbmm <on\|off>` | Toggle ping-based matchmaking for your queue matches. |
| `/bot [easy\|medium\|hard]` | Start a practice-bot duel. Difficulty defaults to `hard`. |
| `/botspeed <units/sec>` | Set the practice bot's strafe speed from 100 to 2500 units/sec. |
| `/spectate list` | List active duels and their scores. |
| `/spectate <player name>` | Spectate the active duel containing that player. Spectators are disarmed; they return to spawn when the match ends or when they use `/spawn`. |
| `/arena` | List configured arena numbers and coordinates. |
| `/arena <number>` | Teleport to a configured arena. |
| `/tp <player name>` | Teleport to an online player. Requires `aimlock.teleport`. |
| `/tp arena <number>` | Teleport to an arena. Requires `aimlock.teleport`. |
| `/spawn` | Return to Midtown spawn after a two-second countdown. Using it during a duel forfeits the duel. |

### Stats and leaderboards

| Command | Description |
| --- | --- |
| `/stats [player name]` | View your unranked record, or another player's record. |
| `/elo` | View your ranked Elo, ranked record, and ranked win rate. |
| `/ping` | View your latest server-reported ping. If no fresh unambiguous reading is available, the plugin refreshes the readings; try again shortly. |
| `/leaderboardmove <x> <y> <z>` | Move the in-world leaderboard and save its position. Requires `aimlock.leaderboard.move`. |

The in-world leaderboards show unranked win rate (minimum five games), unranked wins, ranked Elo, and guild Elo. Individual player entries include their guild tag when they belong to a guild.

### Server UI, tags, and time

| Command | Description |
| --- | --- |
| `/scoreboard <on\|off>` | Show or hide your server-info UI. |
| `/timezone` | Show your saved time zone. |
| `/timezone <IANA ID>` | Set your time zone, for example `/timezone America/New_York`. IDs are case-insensitive; daylight-saving changes follow the selected zone's rules. |
| `/tag <player name> <YT\|TTV\|OWNER\|STAFF\|AIMLOCK\|SUPPORTER\|off>` | Set or remove a player's overhead tag. Requires `aimlock.tags.manage`. |
| `/tagoff` | Hide your own overhead tag. |
| `/tagon` | Show your overhead tag again. |

### Guilds

Use `/guild help` for the short in-game reference. Guild tags are 1-4 characters; new guilds are invite-only by default.

| Command | Description |
| --- | --- |
| `/guild create <tag>` | Create a guild. |
| `/guild join <tag or name>` | Join an open guild. |
| `/guild invite <player name>` | Invite an online player. |
| `/guild invites` | List your pending invitations. |
| `/guild accept [tag or name]` | Accept an invitation. |
| `/guild decline [tag or name]` | Decline an invitation. |
| `/guild leave` | Leave your guild. |
| `/guild lookup [tag or name]` or `/guild info [tag or name]` | View guild information; without a target, view your own guild. |
| `/guild members [tag or name]` | List guild members. |
| `/guild list` | List guilds and their access modes. |
| `/guild mode <open\|invite>` or `/guild access <open\|invite>` | Change your guild's join access. |
| `/guild newtag <tag>` | Change the guild tag. |
| `/guild rename <name>` | Rename the guild. |
| `/guild kick <member>` | Remove a member. |
| `/guild transfer <member>` | Transfer guild ownership. |
| `/guild disband confirm` | Disband your guild after confirming. |

Guild membership and other player-management actions are checked by the guild system; ownership is required for owner-only changes.

### Record administration

The following commands require `aimlock.records.reset`. Player lookup accepts an exact name or a unique name prefix.

| Command | Description |
| --- | --- |
| `/resetelo <player name>` | Reset ranked Elo to 1000 and reset ranked wins/losses. |
| `/resetranked <player name>` or `/resetrankedrecord <player name>` | Reset ranked wins/losses without changing Elo. |
| `/resetunranked <player name>` or `/resetunrankedrecord <player name>` | Reset unranked wins/losses. |

## Build and deployment

The project targets .NET 10 and references `DeadworksManaged.Api.dll` and `Google.Protobuf.dll` from the default Deadlock installation under `game\bin\win64\managed`.

Build from the repository root:

```powershell
dotnet build aimLOCK.csproj
```

The project build target copies `aimLOCK.dll` and `aimLOCK.pdb` into `game\bin\win64\managed\plugins`. The reference and deployment paths in `aimLOCK.csproj` use the default Steam installation path; update them if Deadlock is installed elsewhere.
