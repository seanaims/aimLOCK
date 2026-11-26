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
    private readonly HashSet<uint> initialHeroSelectionPending = new();
    private readonly HashSet<uint> initialHeroSelectionIssued = new();
    private readonly HashSet<uint> initialHeroSelectionCompleted = new();
    private readonly HashSet<uint> initialHeroHazeSelectionStarted = new();
    private readonly HashSet<uint> initialAbilityCurrenciesGranted = new();
    private readonly Dictionary<uint, uint> initialHeroPawnInitializationHandles =
        new();

    private bool initialSpawnRecoverySweepStarted;

    public void StartInitialSpawnRecoverySweep()
    {
        if (this.initialSpawnRecoverySweepStarted)
        {
            return;
        }

        this.initialSpawnRecoverySweepStarted = true;
        this.RecoverInitialSpawns(initializeExistingPawns: true);
    }

    public void MarkInitialHeroSelectionPending(
        CCitadelPlayerController controller)
    {
        uint controllerHandle = controller.EntityHandle;
        if (!controller.IsBot && !this.initialHeroSelectionCompleted.Contains(controllerHandle))
        {
            this.initialHeroSelectionPending.Add(controllerHandle);
        }
    }

    private void RecoverInitialSpawns(bool initializeExistingPawns = false)
    {
        bool needsAnotherSweep = false;
        foreach (var controller in Players.GetAll().Where(player => !player.IsBot).ToArray())
        {
            if (!controller.IsValid)
            {
                continue;
            }

            var pawn = controller.GetHeroPawn();
            if (initializeExistingPawns && pawn is { IsValid: true })
            {
                ApplyHazeLoadout(pawn);
                GrantInitialAbilityCurrencies(controller, pawn);
            }

            if (pawn is { IsValid: true, IsAlive: true })
            {
                continue;
            }

            uint controllerHandle = controller.EntityHandle;
            MarkInitialHeroSelectionPending(controller);
            if (
                this.initialHeroSelectionPending.Contains(controllerHandle) &&
                !this.initialHeroSelectionIssued.Contains(controllerHandle))
            {
                needsAnotherSweep = true;
            }

            BeginInitialHazeSpawn(controller);
        }

        if (needsAnotherSweep)
        {
            this.server.ScheduleOnce(1.Seconds(), () => this.RecoverInitialSpawns());
        }
    }

    public bool IsInitialHeroSelectionPending(
        CCitadelPlayerController controller) => this.initialHeroSelectionPending.Contains(controller.EntityHandle);

    private void BeginInitialHazeSpawn(
        CCitadelPlayerController controller)
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !this.initialHeroSelectionPending.Contains(controllerHandle) ||
            !this.initialHeroSelectionIssued.Add(controllerHandle))
        {
            return;
        }

        controller.ChangeTeam(2);
        Console.WriteLine(
            $"[aimLOCK] Selecting a temporary hero for {controller.PlayerName} on connect.");
        this.RequestInitialSwapHero(controller);
    }

    private void RequestInitialSwapHero(CCitadelPlayerController controller)
    {
        if (!HeroTypeExtensions.TryParse(TemporaryConnectHeroName, out var temporaryHero))
        {
            Console.WriteLine(
                "[aimLOCK] Could not resolve hero_inferno for the initial hero swap.");
            return;
        }

        controller.SelectHero(temporaryHero);
        this.WaitForInitialSwapHero(controller, 120);
    }

    private void WaitForInitialSwapHero(
        CCitadelPlayerController controller,
        int retriesRemaining)
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !this.initialHeroSelectionPending.Contains(controllerHandle) ||
            !this.initialHeroSelectionIssued.Contains(controllerHandle) ||
            this.initialHeroHazeSelectionStarted.Contains(controllerHandle))
        {
            return;
        }

        var pawn = controller.GetHeroPawn();
        if (pawn is { IsValid: true, IsAlive: true })
        {
            if (
                !this.initialHeroPawnInitializationHandles.TryGetValue(
                    controllerHandle,
                    out uint registeredPawnHandle) ||
                registeredPawnHandle != pawn.EntityHandle)
            {
                uint pawnHandle = pawn.EntityHandle;
                this.initialHeroPawnInitializationHandles[controllerHandle] =
                    pawnHandle;
                pawn.OnceHeroInitialized(() =>
                    this.BeginInitialHazeSelection(controller, pawnHandle));
            }

            if (retriesRemaining <= 0)
            {
                this.RetryInitialSwapHero(controller);
                return;
            }

            this.server.ScheduleOnce(0.25.Seconds(), () =>
                this.WaitForInitialSwapHero(controller, retriesRemaining - 1));
            return;
        }

        if (retriesRemaining <= 0)
        {
            this.RetryInitialSwapHero(controller);
            return;
        }

        this.server.ScheduleOnce(0.25.Seconds(), () =>
            this.WaitForInitialSwapHero(controller, retriesRemaining - 1));
    }

    private void BeginInitialHazeSelection(
        CCitadelPlayerController controller,
        uint initializedPawnHandle)
    {
        if (!controller.IsValid)
        {
            return;
        }

        uint controllerHandle = controller.EntityHandle;
        var pawn = controller.GetHeroPawn();
        if (
            pawn is not { IsValid: true, IsAlive: true } ||
            pawn.EntityHandle != initializedPawnHandle ||
            !this.initialHeroSelectionPending.Contains(controllerHandle) ||
            !this.initialHeroSelectionIssued.Contains(controllerHandle) ||
            !this.initialHeroHazeSelectionStarted.Add(controllerHandle))
        {
            return;
        }

        this.initialHeroPawnInitializationHandles.Remove(controllerHandle);
        this.RequestInitialHazeSelection(controller);
    }

    private void RequestInitialHazeSelection(
        CCitadelPlayerController controller)
    {
        controller.SelectHero(Heroes.Haze);
        this.WaitForInitialHazeSpawn(controller, 120);
    }

    private void WaitForInitialHazeSpawn(
        CCitadelPlayerController controller,
        int retriesRemaining)
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !this.initialHeroSelectionPending.Contains(controllerHandle) ||
            !this.initialHeroSelectionIssued.Contains(controllerHandle) ||
            !this.initialHeroHazeSelectionStarted.Contains(controllerHandle))
        {
            return;
        }

        var pawn = controller.GetHeroPawn();
        if (pawn is { IsValid: true, IsAlive: true })
        {
            if (
                !this.initialHeroPawnInitializationHandles.TryGetValue(
                    controllerHandle,
                    out uint registeredPawnHandle) ||
                registeredPawnHandle != pawn.EntityHandle)
            {
                uint pawnHandle = pawn.EntityHandle;
                this.initialHeroPawnInitializationHandles[controllerHandle] =
                    pawnHandle;
                pawn.OnceHeroInitialized(() =>
                    this.CompleteInitialHazeSpawn(controller, pawnHandle));
            }

            if (retriesRemaining <= 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Haze initialization is still pending for {controller.PlayerName}; retrying selection.");
                this.RetryInitialHazeSelection(controller);
                return;
            }

            this.server.ScheduleOnce(0.25.Seconds(), () =>
                this.WaitForInitialHazeSpawn(controller, retriesRemaining - 1));
            return;
        }

        if (retriesRemaining <= 0)
        {
            Console.WriteLine(
                $"[aimLOCK] Haze has not spawned for {controller.PlayerName}; retrying selection.");
            this.RetryInitialHazeSelection(controller);
            return;
        }

        this.server.ScheduleOnce(0.25.Seconds(), () =>
            this.WaitForInitialHazeSpawn(controller, retriesRemaining - 1));
    }

    private void RetryInitialHazeSelection(CCitadelPlayerController controller)
    {
        this.server.ScheduleOnce(2.Seconds(), () =>
        {
            uint controllerHandle = controller.EntityHandle;
            if (
                !controller.IsValid ||
                !this.initialHeroSelectionPending.Contains(controllerHandle) ||
                !this.initialHeroSelectionIssued.Contains(controllerHandle) ||
                !this.initialHeroHazeSelectionStarted.Contains(controllerHandle))
            {
                return;
            }

            this.RequestInitialHazeSelection(controller);
        });
    }

    private void RetryInitialSwapHero(CCitadelPlayerController controller)
    {
        this.server.ScheduleOnce(2.Seconds(), () =>
        {
            uint controllerHandle = controller.EntityHandle;
            if (
                !controller.IsValid ||
                !this.initialHeroSelectionPending.Contains(controllerHandle) ||
                !this.initialHeroSelectionIssued.Contains(controllerHandle) ||
                this.initialHeroHazeSelectionStarted.Contains(controllerHandle))
            {
                return;
            }

            this.RequestInitialSwapHero(controller);
        });
    }

    private void CompleteInitialHazeSpawn(
        CCitadelPlayerController controller,
        uint initializedPawnHandle)
    {
        if (!controller.IsValid)
        {
            return;
        }

        uint controllerHandle = controller.EntityHandle;
        var pawn = controller.GetHeroPawn();
        if (
            pawn is not { IsValid: true, IsAlive: true } ||
            pawn.EntityHandle != initializedPawnHandle ||
            !this.initialHeroSelectionPending.Remove(controllerHandle))
        {
            return;
        }

        this.initialHeroSelectionIssued.Remove(controllerHandle);
        this.initialHeroSelectionCompleted.Add(controllerHandle);
        this.initialHeroHazeSelectionStarted.Remove(controllerHandle);
        this.initialHeroPawnInitializationHandles.Remove(controllerHandle);
        pawn.Teleport(aimLOCK.MidtownSpawnPosition, null, null);
        ApplyHazeLoadout(pawn);
        GrantInitialAbilityCurrencies(controller, pawn);
        this.ClearInjectedSouls(controller, SoulsCleanupRetries);
        this.server.PlayerTagManager.RefreshPlayerTag(controller);
        this.server.UIManager.SetServerInfoVisibility(controller, true);
        Console.WriteLine(
            $"[aimLOCK] Spawned {controller.PlayerName} as Haze at Midtown.");
    }
}
