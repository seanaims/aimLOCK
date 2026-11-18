# aimLOCK

aimLOCK is a Deadlock server plugin for 1v1 matchmaking, practice-bot duels, player and guild leaderboards, and server-side quality-of-life features.

On connection, each player privately receives a short welcome message introducing the 1v1 server and explaining how to join ranked or unranked queues.

## Matchmaking

Join a ranked 1v1 queue with `/join ranked` (ranked matches track Elo) or a casual 1v1 queue with `/join unranked`; the alias `/queue` also works. Use `/leave` to leave before a match is found. Players matched together receive the match-found notice and 3-2-1/FIGHT countdown; other players on the server do not.

Matchmaking can use server-reported ping readings from the server-console `status` command. It uses the median of up to three recent readings and requires an unambiguous player-name match. The initial maximum ping difference is 6 ms; the window widens by 3 ms every 15 seconds (up to 27 ms), and after two minutes any ping difference is allowed, even when a ping reading is missing, to avoid leaving players queued indefinitely. Use `/pbmm off` to opt out of ping filtering for your matches, or `/pbmm on` to enable it again. The setting is saved per player. Practice-bot matches are not ping-filtered.

When a match ends, only that duel's participants and its registered spectators are returned to Midtown spawn. A player who disconnects is removed with their hero and controlled pawns; leaving a duel records a forfeit. Server-info UI is restored after matches, and the duel countdown is private to the participants.

Each active arena displays its current duel score above the arena. The Midtown `ACTIVE MATCHES` wall lists each live match's arena, queue type, players, and score. Scoreboards show only a player's selected overhead tag; they do not add a guild tag by default, and show no tag when the player has cleared theirs. Guild member listings show the selected overhead tag without repeating the guild tag. The final score remains visible until both participants have left, then the scoreboard is removed. New matches use only free arenas; queued matches wait for an arena, and practice-bot matches cannot start while all arenas are occupied. You can play a practice-bot match while queued; when the queue finds an opponent, the bot match ends and the real match starts.

Server pausing is disabled at startup.

## Commands

Commands are entered in chat with a leading `/`. `/help [page]` and `/commands [page]` show the in-game command guide.

### Queues, matches, and movement

| Command | Description |
| --- | --- |
| `/join <ranked\|unranked>` or `/queue <ranked\|unranked>` | Join ranked or unranked matchmaking. |
| `/leave` | Leave your current queue. |
| `/pbmm <on\|off>` | Toggle ping-based matchmaking for your queue matches. |
| `/handicap <number>` (`/handi`) | Set your own saved duel lifesteal percent, from 0 up to the duel's lifesteal (85 by default, or the value agreed with `/duel ... ls`), e.g. `/handi 70`. `/handicap off` clears it. A number is required. |
| `/lifesteal <number>` (`/ls`) | Set the lifesteal percent for duels and bot matches (85 by default), e.g. `/ls 85`. Resets to 85 on server restart. Requires `aimlock.lifesteal`. |
| `/duel <player name> [arena name] [ls <0-100>]` | Send an unranked duel request to an online player, optionally selecting an arena and the duel's lifesteal (e.g. `/duel raven ls 100`). During a duel, `/duel ls <0-100>` proposes a new lifesteal; it applies when the opponent uses `/accept` (right away against a bot). Omit the arena for a random map. They must use `/accept` within 60 seconds; both players must be available, unqueued, and not spectating. Exact names or unique prefixes are accepted. |
| `/accept` | Accept your pending duel request and play on the selected arena, or a random arena if none was selected. |
| `/bot [easy\|medium\|hard]` | Start a practice-bot duel; the bot is named `Bot (difficulty)`. Difficulty defaults to `hard`. |
| `/botspeed <units/sec>` | Set the practice bot's strafe speed from 100 to 2500 units/sec. |
| `/spectate list` or `/spec list` | List active duels and their scores. |
| `/spectate <player name>` or `/spec <player name>` | Spectate the active duel containing that player. Spectators are disarmed; they return to spawn when the match ends or when they use `/spawn`. |
| `/arena` | List all configured arena names, numbers, and coordinates: clock, boxes, stocks, forest, railroad, bridge, cola, tunnel, purple, sewer, front_door, and clock2. |
| `/arena <number|name>` | Teleport to a configured arena by number or name. |
| `/tp <player name>` | Teleport to an online player. Requires `aimlock.teleport`. |
| `/tp arena <number|name>` | Teleport to an arena by number or name. Requires `aimlock.teleport`. |
| `/spawn` | Return to spawn after a two-second countdown. Using it during a duel forfeits the duel. |

### Arenas

| Number | Arena name |
| --- | --- |
| 1 | `clock` |
| 2 | `boxes` |
| 3 | `stocks` |
| 4 | `forest` |
| 5 | `railroad` |
| 6 | `bridge` |
| 7 | `cola` |
| 8 | `tunnel` |
| 9 | `purple` |
| 10 | `sewer` |
| 11 | `front_door` |
| 12 | `clock2` |

### Queue zones

Walking into either labeled zone at Midtown automatically joins that queue. The
interaction radii are invisible; walking into the other queue zone switches you
to that queue:

| Queue | Zone center |
| --- | --- |
| Unranked | `-2079 -9270 1211` |
| Ranked | `-2298 -9224 1211` |
| Leave queue | `-2546 -8967 1214` |

### Stats and leaderboards

| Command | Description |
| --- | --- |
| `/stats [player name\|SteamID64]` | View your unranked record, or another player's record. Accepts an exact name, a unique name prefix, or a SteamID64; it asks for a more specific name when more than one player matches. |
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
| `/tag <tagname\|clear>` | Equip a granted overhead tag or clear your selected tag. Supported tags: YT, TTV, OWNER, STAFF, AIMLOCK, SUPPORTER, and GUILDTAG. |
| `/tag list` | List the overhead tags you can use. |
| `/tag give <player name\|SteamID64> <tagname>` | Grant a player access to a tag. Requires `aimlock.tags.manage`; use `GUILDTAG` to grant their current guild tag. Names must match one online player exactly; use the SteamID64 for offline players. |
| `/tag revoke <player name\|SteamID64> <tagname>` | Take away tag access, and remove the tag if they are wearing it. Requires `aimlock.tags.manage`. |
| `/tag <player name\|SteamID64> <tagname\|clear>` | Directly set or clear a player's overhead tag. Requires `aimlock.tags.manage`. |
| `/tagoff` | Hide your own overhead tag. |
| `/tagon` | Show your overhead tag again. |
| `/fixationmax` | Unlock and fully upgrade Fixation in Signature3, with a 999-stack cap for all online players. Requires `aimlock.fixation.max`. |

### Chat moderation

Kicks, bans, and mutes use the Deadworks Admin plugin: `/kick`, `/ban`, `/unban`, `/gag` and `/ungag` (text chat), `/mute` and `/unmute` (voice), `/who`. Durations are in minutes. A gagged player privately sees the reason and time remaining when they try to chat.

### Guilds

Use `/guild help` for the short in-game reference. Guild tags are 1-5 letters or numbers and cannot use built-in or staff tags such as `YT`, `TTV`, `OWNER`, or `STAFF`; guild names are 2-15 characters, and new guilds are invite-only by default. Active overhead tags are shown alongside guild tags in match scoreboards and guild member listings.

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

The following commands require `aimlock.records.reset`. Player lookup accepts an exact name, a unique name prefix, or a SteamID64.

| Command | Description |
| --- | --- |
| `/resetelo <player name>` | Reset ranked Elo to 1000 and reset ranked wins/losses. |
| `/resetranked <player name>` or `/resetrankedrecord <player name>` | Reset ranked wins/losses without changing Elo. |
| `/resetunranked <player name>` or `/resetunrankedrecord <player name>` | Reset unranked wins/losses. |

### Server debugging

These commands require `aimlock.debug`.

| Command | Description |
| --- | --- |
| `/hitdebug` | Toggle printing the hit group and position of every bullet hit on a Haze. |
| `/hazehitgroup <id\|clear>` | Toggle ignoring bullet hits on a Haze hit group (workaround for the oversized right-side hitbox). With no argument, lists blocked groups. |
| `/dumpents` | Write every entity designer name on the map, with counts, to `aimlock_entities.txt`. |
| `/lsdebug` | Toggle a once-a-second lifesteal report in chat (damage dealt, heal expected vs. applied, hits that didn't heal and why, damage taken, net health, both players' current HP, and any health that changed outside damage and lifesteal); per-hit lines go to the server log. Requires `aimlock.debug`. |
| `/botmirror` | Toggle whether the practice bot buys every item you buy during a bot match (on by default). |
| `/ab <on\|off>` (`/allowbuy`) | Allow buying items during your duels (off by default). A duel allows buying only if both players had it on when it started; the opponent is told what you buy. |
| `/hidebots <off\|hero\|all>` | Test: hide practice bots from players outside their match (`off` is the default; hiding made the other players' games crash). Requires `aimlock.debug`. |
| `/hidepassive <on\|off>` | Crash test: `off` sends the Fixation passive to clients again (its HUD icon comes back), `on` hides it (default). Resets on restart. Requires `aimlock.debug`. |
| `/fixdebug` | List the Fixation modifiers (stacks and stack limit) on you and your duel opponent. |

## Duel rules

- Lifesteal is applied server-side to bullet damage only, including Fixation's stack damage: 100% of the health the target actually loses by default, 85% in duels and bot matches (`/lifesteal` for the server, `/duel ... ls` for one duel), or a player's own `/handicap` value. Practice bots lifesteal too; the hard bot heals for 40% instead of 85%. Item bullet lifesteal is suspended during duels, and while a round is live any other healing (regen, healing from bought items) is undone, so lifesteal is the only healing.
- Duel bullets deal normal damage (set `DUEL_BULLET_DAMAGE` above 25 to scale them up, e.g. 40).
- Infinite ammo; no comeback or difficulty gold; bots never take over for players who leave; every player keeps 999,999 souls (set at spawn, topped up after each purchase or any other change, and checked once a second).
- In a live duel round, health only changes through reported damage and lifesteal: any other gain is taken back within 1/20 s.
- After every duel round, each player sees their headshot accuracy and body shot accuracy for that round in chat (hits out of shots fired).
- Practice bots are created next to their player once the player is in the arena (a bot created far from a player crashed that player's game). A bot is removed when its player leaves. `/bottest` bots and any other leftover bot are kicked whenever a player leaves or joins.
- Duel item stack: Swift Striker, Opening Rounds, Fleetfoot, Titanic Magazine, Enduring Speed and Burst Fire, all at their normal values (Swift Striker 20%), so players' games predict the same fire rate as the server. No Glass Cannon. Duel max health is the hero's own value (max boons plus these items), so the health bar is accurate.
- Fixation stacks are cleared on every kill, at the start of every countdown and again at FIGHT, and every duel and round starts with health full. Burst Fire stays active: the server sets `citadel_ability_cooldown_max 0.001` (ability and item cooldowns capped at almost nothing), as other 1v1 servers do, and its buff is applied permanently in duels.
- Everyone spawns as Haze with max boons. Fixation is the only ability: it is unlocked with every upgrade tier and a 999 stack cap; Sleep Dagger, Smoke Bomb and Bullet Dance are removed.
- The map is kept empty: troopers, guardians, walkers, patrons, neutrals, the mid boss, breakables, the urn, powerups, healing pickups and the sandbox soul orb spawners are disabled or removed as they spawn.

## Build and deployment

The project targets .NET 10 and references `DeadworksManaged.Api.dll`, `DeadworksManaged.Game.dll` and `Google.Protobuf.dll` from the default Deadlock installation under `game\bin\win64\managed`.

Build from the repository root:

```powershell
dotnet build aimLOCK.csproj
```

The project build target copies `aimLOCK.dll` and `aimLOCK.pdb` into `game\bin\win64\managed\plugins`. The reference and deployment paths in `aimLOCK.csproj` use the default Steam installation path; update them if Deadlock is installed elsewhere.
