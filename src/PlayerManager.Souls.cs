namespace AimLOCK;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DeadworksManaged.Api;
using DeadworksManaged.Game;

public partial class PlayerManager
{
    /* Souls and buying */

    // Every player keeps this many souls to spend; topped up after each purchase.
    public const int MaxSouls = 999_999;

    private const int InjectedSoulsBalance = 1_000_000_000;
    private const int SoulsCleanupRetries = 10;

    private static readonly Dictionary<string, string> ItemDisplayNames = new(StringComparer.Ordinal)
    {
        ["upgrade_blitz_bullets"] = "Swift Striker",
        ["upgrade_high_velocity_mag"] = "High-Velocity Mag",
        ["upgrade_pristine_emblem"] = "Opening Rounds",
        ["upgrade_fleetfoot_boots"] = "Fleetfoot",
        ["upgrade_titan_round"] = "Titanic Magazine",
        ["upgrade_cardio_calibrator"] = "Enduring Speed",
        ["upgrade_burst_fire"] = "Burst Fire",
        ["upgrade_chonky"] = "Fortitude",
        ["upgrade_health"] = "Extra Health",
        ["upgrade_vampire"] = "Bullet Lifesteal",
    };

    // Players (Steam ID) who turned bot mirror off with /botmirror. On by default.
    private readonly HashSet<ulong> botMirrorOff = new();

    // Players (Steam ID) who allow buying in duels (/ab on). Off by default.
    private readonly HashSet<ulong> duelBuyingAllowed = new();

    // Pawn -> the items it had when a purchase started; checked on the next tick to
    // find what was bought.
    private readonly Dictionary<uint, HashSet<string>> pendingPurchases = new();

    /// <summary>
    /// Souls changes from the game. The starting amount is replaced with the max right
    /// away; anything else (dropped on death, hero swap, a sale) is topped back up on
    /// the next tick. Purchases are handled by OnItemPurchase.
    /// </summary>
    public HookResult OnSoulsChanged(ModifyCurrencyEvent args)
    {
        var pawn = args.Pawn;
        if (pawn is not { IsValid: true } || pawn.Controller is { IsBot: true })
        {
            return HookResult.Continue;
        }

        if (args.Source == ECurrencySource.EItemPurchase)
        {
            OnItemPurchase(pawn);
            return HookResult.Continue;
        }

        if (args.Source == ECurrencySource.EStartingAmount)
        {
            pawn.SetCurrency(ECurrencyType.EGold, MaxSouls);
            return HookResult.Stop;
        }

        uint pawnHandle = pawn.EntityHandle;
        this.server.ScheduleNextTick(() =>
        {
            var current = CBaseEntity.FromHandle(pawnHandle)?.As<CCitadelPlayerPawn>();
            if (current is { IsValid: true })
            {
                TopUpSouls(current);
            }
        });
        return HookResult.Continue;
    }

    public bool ToggleBotMirror(CCitadelPlayerController player)
    {
        if (this.botMirrorOff.Remove(player.PlayerSteamId))
        {
            return true;
        }

        this.botMirrorOff.Add(player.PlayerSteamId);
        return false;
    }

    public bool AllowsDuelBuying(CCitadelPlayerController player) =>
        this.duelBuyingAllowed.Contains(player.PlayerSteamId);

    public void SetAllowDuelBuying(CCitadelPlayerController player, bool allow)
    {
        if (allow)
        {
            this.duelBuyingAllowed.Add(player.PlayerSteamId);
        }
        else
        {
            this.duelBuyingAllowed.Remove(player.PlayerSteamId);
        }
    }

    /// <summary>Called when a player spends souls on an item.</summary>
    public void OnItemPurchase(CCitadelPlayerPawn pawn)
    {
        if (pawn is not { IsValid: true } || pawn.Controller is not { IsBot: false })
        {
            return;
        }

        uint pawnHandle = pawn.EntityHandle;
        if (this.pendingPurchases.ContainsKey(pawnHandle))
        {
            return;
        }

        this.pendingPurchases[pawnHandle] = GetItemNames(pawn);
        this.server.ScheduleNextTick(() => this.HandlePurchase(pawnHandle));
    }

    public void ClearInjectedSoulsFromConnectedPlayers()
    {
        foreach (var controller in Players.GetAll().Where(player => !player.IsBot).ToArray())
        {
            ClearInjectedSouls(controller, SoulsCleanupRetries);
        }
    }

    private static void TopUpSouls(CCitadelPlayerPawn pawn)
    {
        if (pawn.GetCurrency(ECurrencyType.EGold) != MaxSouls)
        {
            pawn.SetCurrency(ECurrencyType.EGold, MaxSouls);
        }
    }

    private static HashSet<string> GetItemNames(CCitadelPlayerPawn pawn)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var abilities = pawn.AbilityComponent?.Abilities;
        if (abilities == null)
        {
            return names;
        }

        foreach (var ability in abilities)
        {
            if (ability is { IsValid: true, IsItem: true })
            {
                names.Add(ability.AbilityName);
            }
        }

        return names;
    }

    // In-game name where known, otherwise the internal name tidied up
    // ("upgrade_close_range" -> "Close Range").
    private static string GetItemDisplayName(string item)
    {
        if (ItemDisplayNames.TryGetValue(item, out string? name))
        {
            return name;
        }

        string trimmed = item.StartsWith("upgrade_", StringComparison.Ordinal) ? item[8..] : item;
        var words = trimmed.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        }

        return string.Join(' ', words);
    }

    private void HandlePurchase(uint pawnHandle)
    {
        if (!this.pendingPurchases.Remove(pawnHandle, out var before))
        {
            return;
        }

        var pawn = CBaseEntity.FromHandle(pawnHandle)?.As<CCitadelPlayerPawn>();
        if (pawn is not { IsValid: true } || pawn.Controller is not { } buyer)
        {
            return;
        }

        if (
            this.server.ActiveMatches.Count == 0 ||
            !TryGetActiveMatch(buyer, out var match) ||
            match.Finished)
        {
            TopUpSouls(pawn);
            return;
        }

        var opponent = this.FindMatchParticipant(
            string.Equals(match.Player1, GetMatchParticipantName(buyer), StringComparison.OrdinalIgnoreCase)
                ? match.Player2
                : match.Player1);

        foreach (string item in GetItemNames(pawn))
        {
            if (before.Contains(item))
            {
                continue;
            }

            string itemName = GetItemDisplayName(item);
            if (opponent is { IsBot: true })
            {
                if (this.botMirrorOff.Contains(buyer.PlayerSteamId))
                {
                    continue;
                }

                var botPawn = opponent.GetHeroPawn();
                if (
                    botPawn is { IsValid: true } &&
                    botPawn.AbilityComponent?.FindAbilityByName(item) == null)
                {
                    botPawn.AddItem(item);
                    Chat.PrintToChat(buyer, $"The bot bought {itemName} too. (/botmirror to turn this off)");
                }
            }
            else if (!match.BuyingAllowed)
            {
                pawn.SellItem(item, fullRefund: true);
                Chat.PrintToChat(
                    buyer,
                    "Buying items is off in this duel. Both players need /ab on before the duel.");
            }
            else if (opponent is { IsValid: true })
            {
                Chat.PrintToChat(opponent, $"{GetCachedPlayerName(buyer)} bought {itemName}.");
            }
        }

        TopUpSouls(pawn);
    }

    private void ClearInjectedSouls(
        CCitadelPlayerController controller,
        int retriesRemaining)
    {
        if (!controller.IsValid)
        {
            return;
        }

        var pawn = controller.GetHeroPawn();
        if (pawn is { IsValid: true })
        {
            pawn.OnceHeroInitialized(() =>
            {
                if (!controller.IsValid)
                {
                    return;
                }

                var initializedPawn = controller.GetHeroPawn();
                if (initializedPawn is not { IsValid: true })
                {
                    return;
                }

                TopUpSouls(initializedPawn);
            });
            return;
        }

        if (retriesRemaining <= 0)
        {
            Console.WriteLine(
                $"[aimLOCK] Could not check injected souls for {controller.PlayerName}: hero did not load.");
            return;
        }

        this.server.ScheduleOnce(0.5.Seconds(), () =>
            this.ClearInjectedSouls(controller, retriesRemaining - 1));
    }
}
