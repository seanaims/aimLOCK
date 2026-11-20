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

namespace aimLOCK;

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
        if (initialSpawnRecoverySweepStarted)
            return;

        initialSpawnRecoverySweepStarted = true;
        RecoverInitialSpawns(initializeExistingPawns: true);
    }

    private void RecoverInitialSpawns(bool initializeExistingPawns = false)
    {
        bool needsAnotherSweep = false;
        foreach (var controller in Players.GetAll().Where(player => !player.IsBot).ToArray())
        {
            if (!controller.IsValid)
                continue;

            var pawn = controller.GetHeroPawn();
            if (initializeExistingPawns && pawn is { IsValid: true })
            {
                ApplyHazeLoadout(pawn);
                GrantInitialAbilityCurrencies(controller, pawn);
            }

            if (pawn is { IsValid: true, IsAlive: true })
                continue;

            uint controllerHandle = controller.EntityHandle;
            MarkInitialHeroSelectionPending(controller);
            if (
                initialHeroSelectionPending.Contains(controllerHandle) &&
                !initialHeroSelectionIssued.Contains(controllerHandle)
            )
                needsAnotherSweep = true;

            BeginInitialHazeSpawn(controller);
        }

        if (needsAnotherSweep)
            server.ScheduleOnce(1.Seconds(), () => RecoverInitialSpawns());
    }

    public void MarkInitialHeroSelectionPending(
        CCitadelPlayerController controller
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (!controller.IsBot && !initialHeroSelectionCompleted.Contains(controllerHandle))
            initialHeroSelectionPending.Add(controllerHandle);
    }

    public bool IsInitialHeroSelectionPending(
        CCitadelPlayerController controller
    ) => initialHeroSelectionPending.Contains(controller.EntityHandle);

    private void BeginInitialHazeSpawn(
        CCitadelPlayerController controller
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !initialHeroSelectionPending.Contains(controllerHandle) ||
            !initialHeroSelectionIssued.Add(controllerHandle)
        )
            return;

        controller.ChangeTeam(2);
        Console.WriteLine(
            $"[aimLOCK] Selecting a temporary hero for {controller.PlayerName} on connect."
        );
        RequestInitialSwapHero(controller);
    }

    private void RequestInitialSwapHero(CCitadelPlayerController controller)
    {
        if (!HeroTypeExtensions.TryParse(TemporaryConnectHeroName, out var temporaryHero))
        {
            Console.WriteLine(
                "[aimLOCK] Could not resolve hero_inferno for the initial hero swap."
            );
            return;
        }

        controller.SelectHero(temporaryHero);
        WaitForInitialSwapHero(controller, 120);
    }

    private void WaitForInitialSwapHero(
        CCitadelPlayerController controller,
        int retriesRemaining
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !initialHeroSelectionPending.Contains(controllerHandle) ||
            !initialHeroSelectionIssued.Contains(controllerHandle) ||
            initialHeroHazeSelectionStarted.Contains(controllerHandle)
        )
            return;

        var pawn = controller.GetHeroPawn();
        if (pawn is { IsValid: true, IsAlive: true })
        {
            if (
                !initialHeroPawnInitializationHandles.TryGetValue(
                    controllerHandle,
                    out uint registeredPawnHandle
                ) ||
                registeredPawnHandle != pawn.EntityHandle
            )
            {
                uint pawnHandle = pawn.EntityHandle;
                initialHeroPawnInitializationHandles[controllerHandle] =
                    pawnHandle;
                pawn.OnceHeroInitialized(() =>
                    BeginInitialHazeSelection(controller, pawnHandle));
            }

            if (retriesRemaining <= 0)
            {
                RetryInitialSwapHero(controller);
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                WaitForInitialSwapHero(controller, retriesRemaining - 1));
            return;
        }

        if (retriesRemaining <= 0)
        {
            RetryInitialSwapHero(controller);
            return;
        }

        server.ScheduleOnce(0.25.Seconds(), () =>
            WaitForInitialSwapHero(controller, retriesRemaining - 1));
    }

    private void BeginInitialHazeSelection(
        CCitadelPlayerController controller,
        uint initializedPawnHandle
    )
    {
        if (!controller.IsValid)
            return;

        uint controllerHandle = controller.EntityHandle;
        var pawn = controller.GetHeroPawn();
        if (
            pawn is not { IsValid: true, IsAlive: true } ||
            pawn.EntityHandle != initializedPawnHandle ||
            !initialHeroSelectionPending.Contains(controllerHandle) ||
            !initialHeroSelectionIssued.Contains(controllerHandle) ||
            !initialHeroHazeSelectionStarted.Add(controllerHandle)
        )
            return;

        initialHeroPawnInitializationHandles.Remove(controllerHandle);
        RequestInitialHazeSelection(controller);
    }

    private void RequestInitialHazeSelection(
        CCitadelPlayerController controller
    )
    {
        controller.SelectHero(Heroes.Haze);
        WaitForInitialHazeSpawn(controller, 120);
    }

    private void WaitForInitialHazeSpawn(
        CCitadelPlayerController controller,
        int retriesRemaining
    )
    {
        uint controllerHandle = controller.EntityHandle;
        if (
            !controller.IsValid ||
            !initialHeroSelectionPending.Contains(controllerHandle) ||
            !initialHeroSelectionIssued.Contains(controllerHandle) ||
            !initialHeroHazeSelectionStarted.Contains(controllerHandle)
        )
            return;

        var pawn = controller.GetHeroPawn();
        if (pawn is { IsValid: true, IsAlive: true })
        {
            if (
                !initialHeroPawnInitializationHandles.TryGetValue(
                    controllerHandle,
                    out uint registeredPawnHandle
                ) ||
                registeredPawnHandle != pawn.EntityHandle
            )
            {
                uint pawnHandle = pawn.EntityHandle;
                initialHeroPawnInitializationHandles[controllerHandle] =
                    pawnHandle;
                pawn.OnceHeroInitialized(() =>
                    CompleteInitialHazeSpawn(controller, pawnHandle));
            }

            if (retriesRemaining <= 0)
            {
                Console.WriteLine(
                    $"[aimLOCK] Haze initialization is still pending for {controller.PlayerName}; retrying selection."
                );
                RetryInitialHazeSelection(controller);
                return;
            }

            server.ScheduleOnce(0.25.Seconds(), () =>
                WaitForInitialHazeSpawn(controller, retriesRemaining - 1));
            return;
        }

        if (retriesRemaining <= 0)
        {
            Console.WriteLine(
                $"[aimLOCK] Haze has not spawned for {controller.PlayerName}; retrying selection."
            );
            RetryInitialHazeSelection(controller);
            return;
        }

        server.ScheduleOnce(0.25.Seconds(), () =>
            WaitForInitialHazeSpawn(controller, retriesRemaining - 1));
    }

    private void RetryInitialHazeSelection(CCitadelPlayerController controller)
    {
        server.ScheduleOnce(2.Seconds(), () =>
        {
            uint controllerHandle = controller.EntityHandle;
            if (
                !controller.IsValid ||
                !initialHeroSelectionPending.Contains(controllerHandle) ||
                !initialHeroSelectionIssued.Contains(controllerHandle) ||
                !initialHeroHazeSelectionStarted.Contains(controllerHandle)
            )
                return;

            RequestInitialHazeSelection(controller);
        });
    }

    private void RetryInitialSwapHero(CCitadelPlayerController controller)
    {
        server.ScheduleOnce(2.Seconds(), () =>
        {
            uint controllerHandle = controller.EntityHandle;
            if (
                !controller.IsValid ||
                !initialHeroSelectionPending.Contains(controllerHandle) ||
                !initialHeroSelectionIssued.Contains(controllerHandle) ||
                initialHeroHazeSelectionStarted.Contains(controllerHandle)
            )
                return;

            RequestInitialSwapHero(controller);
        });
    }

    private void CompleteInitialHazeSpawn(
        CCitadelPlayerController controller,
        uint initializedPawnHandle
    )
    {
        if (!controller.IsValid)
            return;

        uint controllerHandle = controller.EntityHandle;
        var pawn = controller.GetHeroPawn();
        if (
            pawn is not { IsValid: true, IsAlive: true } ||
            pawn.EntityHandle != initializedPawnHandle ||
            !initialHeroSelectionPending.Remove(controllerHandle)
        )
            return;

        initialHeroSelectionIssued.Remove(controllerHandle);
        initialHeroSelectionCompleted.Add(controllerHandle);
        initialHeroHazeSelectionStarted.Remove(controllerHandle);
        initialHeroPawnInitializationHandles.Remove(controllerHandle);
        pawn.Teleport(aimLOCK.MidtownSpawnPosition, null, null);
        ApplyHazeLoadout(pawn);
        GrantInitialAbilityCurrencies(controller, pawn);
        ClearInjectedSouls(controller, SoulsCleanupRetries);
        server.PlayerTagManager.RefreshPlayerTag(controller);
        server.UIManager.SetServerInfoVisibility(controller, true);
        Console.WriteLine(
            $"[aimLOCK] Spawned {controller.PlayerName} as Haze at Midtown."
        );
    }
}
