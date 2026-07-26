// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.IO;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Platform.Headless.Tests;

public sealed class HeadlessLifecycleTests : IDisposable {
    readonly TemporaryFileSystemHost files = new();
    readonly HeadlessPlatform platform;

    public HeadlessLifecycleTests() {
        platform = new(new() { FileSystem = files });
    }

    [Fact]
    public void ItStartsRunningWithNothingWrong() {
        Assert.Equal(ApplicationState.Running, platform.Lifecycle.State);
        Assert.Equal(MemoryPressure.Normal, platform.Lifecycle.MemoryPressure);
        Assert.False(platform.Lifecycle.IsQuitRequested);
    }

    /// <summary>
    ///     A quit is a request the frame loop answers, which is what lets an unsaved-changes prompt
    ///     exist at all.
    /// </summary>
    [Fact]
    public void QuittingIsARequestThatCanBeWithdrawn() {
        platform.Lifecycle.RequestQuit();

        Assert.True(platform.Lifecycle.IsQuitRequested);
        Assert.Single(platform.PumpEvents().ToArray(), item => item.Kind == PlatformEventKind.Quit);

        platform.Lifecycle.CancelQuit();
        Assert.False(platform.Lifecycle.IsQuitRequested);
    }

    /// <summary>
    ///     <c>docs/plan/10 § Android</c> puts lifecycle at the top of the list of platform bug
    ///     sources and asks for a suspend/resume fault-injection loop in CI. This is where it runs:
    ///     on a phone it needs a phone, here it costs milliseconds.
    /// </summary>
    [Fact]
    public void AHundredSuspendResumeCyclesProduceExactlyTheEventsTheyShould() {
        platform.PumpEvents();

        for (var cycle = 0; cycle < 100; cycle++) {
            platform.Simulation.Suspend();
            Assert.Equal(ApplicationState.Suspended, platform.Lifecycle.State);

            platform.Simulation.Resume();
            Assert.Equal(ApplicationState.Running, platform.Lifecycle.State);
        }

        var kinds = platform.PumpEvents().ToArray().Select(item => item.Kind).ToArray();

        Assert.Equal(200, kinds.Length);

        for (var index = 0; index < kinds.Length; index += 2) {
            Assert.Equal(PlatformEventKind.Suspending, kinds[index]);
            Assert.Equal(PlatformEventKind.Resumed, kinds[index + 1]);
        }
    }

    /// <summary>
    ///     A real platform does not suspend an already-suspended process. Neither does this one, so
    ///     a fault-injection loop does not have to remember which state it left things in.
    /// </summary>
    [Fact]
    public void SuspendingTwiceSaysItOnce() {
        platform.PumpEvents();

        platform.Simulation.Suspend();
        platform.Simulation.Suspend();
        platform.Simulation.Resume();
        platform.Simulation.Resume();

        var kinds = platform.PumpEvents().ToArray().Select(item => item.Kind).ToArray();

        Assert.Equal([PlatformEventKind.Suspending, PlatformEventKind.Resumed], kinds);
    }

    [Fact]
    public void MemoryPressureIsLatchedSoASubsystemCanReactNextFrame() {
        platform.PumpEvents();

        platform.Simulation.ReportMemoryPressure(MemoryPressure.Critical);

        Assert.Equal(MemoryPressure.Critical, platform.Lifecycle.MemoryPressure);
        Assert.Single(platform.PumpEvents().ToArray(), item => item.Kind == PlatformEventKind.LowMemory);

        // Still latched a frame later, so code that reads it rather than the event still sees it.
        Assert.Equal(MemoryPressure.Critical, platform.Lifecycle.MemoryPressure);
    }

    [Fact]
    public void ReturningToNormalPressureIsNotALowMemoryWarning() {
        platform.PumpEvents();

        platform.Simulation.ReportMemoryPressure(MemoryPressure.Normal);

        Assert.Empty(platform.PumpEvents().ToArray());
    }

    [Fact]
    public void BackgroundIsNotSuspended() {
        platform.Simulation.SetBackground(true);
        Assert.Equal(ApplicationState.Background, platform.Lifecycle.State);

        platform.Simulation.SetBackground(false);
        Assert.Equal(ApplicationState.Running, platform.Lifecycle.State);
    }

    /// <summary>
    ///     Held keys are not released by any platform on focus loss, so an input layer has to clear
    ///     them itself — and it needs a way to be tested doing so.
    /// </summary>
    [Fact]
    public void HeldKeysCanBeSetAndReleasedWithoutAKeyboard() {
        platform.SimulatedInput.SetKey(Key.W, down: true);
        platform.SimulatedInput.SetMouseButton(MouseButton.Primary, down: true);

        Assert.True(platform.Input.IsKeyDown(Key.W));
        Assert.True(platform.Input.IsMouseButtonDown(MouseButton.Primary));

        platform.SimulatedInput.ReleaseAll();

        Assert.False(platform.Input.IsKeyDown(Key.W));
        Assert.False(platform.Input.IsMouseButtonDown(MouseButton.Primary));
    }

    [Fact]
    public void ThereAreNoGamepadsAndAskingForOneIsNotAnError() {
        Assert.Empty(platform.Input.Gamepads);
        Assert.False(platform.Input.TryGetGamepad(0, out _));
    }

    /// <summary>
    ///     An in-process buffer pretending to be a clipboard would make copy-and-paste appear to
    ///     work here and fail in the product. Refusing is the honest report.
    /// </summary>
    [Fact]
    public void TheClipboardRefusesRatherThanPretending() {
        Assert.False(platform.Clipboard.HasText);
        Assert.False(platform.Clipboard.SetText("hello"));
        Assert.False(platform.Clipboard.TryGetText(out _));
    }

    /// <summary>
    ///     The same answer a user pressing Cancel gives, so the caller's existing cancellation path
    ///     covers headless with no special case anywhere.
    /// </summary>
    [Fact]
    public async Task ADialogWithNobodyToShowItToReturnsNothingChosen() {
        var token = TestContext.Current.CancellationToken;

        Assert.Null(await platform.Dialogs.OpenFileAsync(new(), cancellationToken: token));
        Assert.Empty(await platform.Dialogs.OpenFilesAsync(new(), cancellationToken: token));
        Assert.Null(await platform.Dialogs.SaveFileAsync(new(), cancellationToken: token));
        Assert.Null(await platform.Dialogs.OpenFolderAsync(new(), cancellationToken: token));
        Assert.Equal(MessageBoxResult.None, await platform.Dialogs.ShowMessageAsync(new(), cancellationToken: token));
    }

    [Fact]
    public void TextInputRemembersWhereTheCaretWasSoTheImeCanBeTested() {
        using var window = platform.CreateWindow(new());
        var text = (HeadlessTextInput)platform.TextInput;

        platform.TextInput.Activate(window);
        platform.TextInput.SetCandidateArea(window, new(10f, 20f, 2f, 16f));

        Assert.True(platform.TextInput.IsActive);
        Assert.Equal(new Rectangle(10f, 20f, 2f, 16f), text.CandidateArea);

        platform.TextInput.Deactivate();
        Assert.False(platform.TextInput.IsActive);
    }

    [Fact]
    public void PowerIsMainsAndNothingIsOverheating() {
        Assert.Equal(PowerSource.Mains, platform.Power.Source);
        Assert.Null(platform.Power.BatteryLevel);
        Assert.Equal(ThermalState.Nominal, platform.Power.Thermal);
        Assert.False(platform.Power.IsLowPowerMode);
    }

    /// <summary>
    ///     A server shares its machine, and a process that pins itself to core 3 there fights a
    ///     scheduler that knows more than it does. The answer is "no", not an exception.
    /// </summary>
    [Fact]
    public void AffinityIsRefusedRatherThanFaked() {
        Assert.False(platform.Processors.SupportsAffinity);
        Assert.False(platform.Processors.TrySetAffinity(0));
        Assert.Equal(Environment.ProcessorCount, platform.Processors.AvailableProcessors);
        platform.Processors.ClearAffinity();
    }

    [Fact]
    public void TheStandardMountsArePresentSoEngineCodeNeverSeesANativePath() {
        var fileSystem = new VirtualFileSystem();
        platform.FileSystem.MountStandardLocations(fileSystem);

        Assert.True(fileSystem.TryResolve(MountPoints.Data / "save.bin", out _, out _));
        Assert.True(fileSystem.TryResolve(MountPoints.Cache / "shaders.bin", out _, out _));
    }

    public void Dispose() {
        platform.Dispose();
        files.Dispose();
    }
}
