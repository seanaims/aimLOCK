# aimLOCK - An open source Deadlock Vamp plugin

Queue commands: `/join ranked`, `/join unranked`, and `/leave`.
Ranked and unranked queues pair players using server-reported ping from the server-console `status` command. `/ping` displays your latest server-reported ping. The system uses the median of up to three recent readings, requires unique player names to map status rows to controllers, and starts with a 6 ms maximum difference. That window widens by 3 ms every 15 seconds, up to 60 ms; after two minutes it permits any ping difference to avoid leaving players queued indefinitely. Bot matches are not filtered by ping.
The bot follows the most recently selected queue and returns to that queue after a match.
Use `/stats` for unranked wins/losses and `/elo` for ranked Elo and ranked wins/losses.
Use `/spawn` to return to Midtown spawn; using it during a duel forfeits the match and records a loss.
Use `/scoreboard on` or `/scoreboard off` to show or hide your server info UI.
Timezone command: `/timezone <IANA ID>` (for example, `/timezone America/New_York`; timezone IDs are case-insensitive). Daylight saving is handled by the selected zone's rules; fixed zones such as UTC do not shift.
Run `/help` (or `/commands`) for queue commands on page 1, stats and Elo on page 2, and spawn/UI settings on page 3.
