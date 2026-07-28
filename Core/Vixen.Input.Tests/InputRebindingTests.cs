// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Input.Tests;

/// <summary>Interactive rebinding, conflict detection, and what a player's saved overrides do.</summary>
public class InputRebindingTests {
    readonly InputDeviceSet devices = new();
    readonly InputActions actions = Sample.Load();

    double now;

    [Fact]
    public void CapturesTheKeyThePlayerPressed() {
        var fire = actions.FindAction("Player/Fire");
        var operation = new InputRebindingOperation(actions, fire, 0).Start();

        devices.SubmitKey(InputKey.Space, true);
        Settle(operation);

        Assert.Equal(InputRebindingState.Completed, operation.State);
        Assert.Equal(InputControl.Key(InputKey.Space), operation.SelectedControl);
        Assert.Equal("<Keyboard>/space", fire.Bindings[0].EffectivePath);
        Assert.Equal(InputControl.Key(InputKey.Space), fire.Bindings[0].Control);
    }

    [Fact]
    public void WaitsForTheControlToSettle() {
        var operation = new InputRebindingOperation(actions, actions.FindAction("Player/Fire"), 0).Start();

        devices.SubmitKey(InputKey.Space, true);
        operation.Update(devices, now);

        // Seen once, but not yet for long enough. A stick crosses every value on its way to a corner.
        Assert.Equal(InputRebindingState.Listening, operation.State);

        operation.Update(devices, now + 1.0);
        Assert.Equal(InputRebindingState.Completed, operation.State);
    }

    [Fact]
    public void RefusesAnExcludedControl() {
        var operation = new InputRebindingOperation(actions, actions.FindAction("Player/Fire"), 0)
            .Excluding(InputControl.Key(InputKey.Escape))
            .Start();

        devices.SubmitKey(InputKey.Escape, true);
        Settle(operation);

        Assert.Equal(InputRebindingState.Listening, operation.State);
    }

    [Fact]
    public void RefusesAControlFromAnotherFamilyOfDevice() {
        var operation = new InputRebindingOperation(actions, actions.FindAction("Player/Fire"), 0)
            .WithDevice(InputDeviceKind.Gamepad)
            .Start();

        devices.SubmitKey(InputKey.Space, true);
        Settle(operation);
        Assert.Equal(InputRebindingState.Listening, operation.State);

        var pad = devices.ConnectGamepad(1);
        devices.SubmitGamepadButton(pad.DeviceId, GamepadControl.North, true);
        Settle(operation);

        Assert.Equal(InputRebindingState.Completed, operation.State);
        Assert.Equal(InputControl.Gamepad(GamepadControl.North, 1), operation.SelectedControl);
    }

    [Fact]
    public void TurnsTheActionsOffWhileListening() {
        // Otherwise the key chosen for "jump" also makes the character jump on the way past.
        var operation = new InputRebindingOperation(actions, actions.FindAction("Player/Fire"), 0).Start();

        Assert.False(actions["Player"].IsEnabled);

        devices.SubmitKey(InputKey.Space, true);
        Settle(operation);

        Assert.True(actions["Player"].IsEnabled);
    }

    [Fact]
    public void StopsOnAControlThatIsAlreadyBound() {
        var crouch = actions.FindAction("Player/Crouch");
        var operation = new InputRebindingOperation(actions, crouch, 0).Start();

        // The key the composite's `up` part already uses.
        devices.SubmitKey(InputKey.W, true);
        Settle(operation);

        Assert.Equal(InputRebindingState.Blocked, operation.State);
        Assert.NotEmpty(operation.Conflicts);
        Assert.Equal("Player/Move", operation.Conflicts[0].Action.Path);
        Assert.Equal("<Keyboard>/leftControl", crouch.Bindings[0].EffectivePath);
    }

    [Fact]
    public void AppliesAConflictingBindingWhenToldTo() {
        var crouch = actions.FindAction("Player/Crouch");
        var operation = new InputRebindingOperation(actions, crouch, 0).Start();

        devices.SubmitKey(InputKey.W, true);
        Settle(operation);
        operation.ApplyAnyway();

        Assert.Equal(InputRebindingState.Completed, operation.State);
        Assert.Equal("<Keyboard>/w", crouch.Bindings[0].EffectivePath);
    }

    [Fact]
    public void IgnoresAConflictInAnotherControlScheme() {
        var crouch = actions.FindAction("Player/Crouch");

        var operation = new InputRebindingOperation(actions, crouch, 0) { ConflictScheme = "Gamepad" }
            .Start();

        devices.SubmitKey(InputKey.W, true);
        Settle(operation);

        Assert.Equal(InputRebindingState.Completed, operation.State);
    }

    [Fact]
    public void LeavesTheBindingAloneWhenCancelled() {
        var fire = actions.FindAction("Player/Fire");
        var operation = new InputRebindingOperation(actions, fire, 0).Start();

        operation.Cancel();

        Assert.Equal(InputRebindingState.Canceled, operation.State);
        Assert.Equal("<Mouse>/primary", fire.Bindings[0].EffectivePath);
        Assert.True(actions["Player"].IsEnabled);
    }

    [Fact]
    public void RebindsOnePartOfAComposite() {
        var move = actions.FindAction("Player/Move");
        var operation = new InputRebindingOperation(actions, move, 0, 0).Start();

        devices.SubmitKey(InputKey.Home, true);
        Settle(operation);

        Assert.Equal(InputRebindingState.Completed, operation.State);
        Assert.Equal("<Keyboard>/home", move.Bindings[0].Parts[0].EffectivePath);

        // And only that part. Rebinding "up" must not disturb the other three.
        Assert.Equal("<Keyboard>/s", move.Bindings[0].Parts[1].EffectivePath);
    }

    [Fact]
    public void SavesOnlyWhatWasChanged() {
        Assert.Equal(string.Empty, actions.SaveBindingOverrides());

        actions.FindAction("Player/Fire").OverrideBinding(0, "<Keyboard>/space");
        actions.FindAction("Player/Move").OverrideBinding(0, "<Keyboard>/up", 0);

        var saved = actions.SaveBindingOverrides();

        Assert.Contains("Player/Fire[0]=<Keyboard>/space", saved, StringComparison.Ordinal);
        Assert.Contains("Player/Move[0][0]=<Keyboard>/up", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadsWhatItSaved() {
        actions.FindAction("Player/Fire").OverrideBinding(0, "<Keyboard>/space");
        actions.FindAction("Player/Move").OverrideBinding(0, "<Keyboard>/up", 0);
        var saved = actions.SaveBindingOverrides();

        var fresh = Sample.Load();
        Assert.Equal(2, fresh.LoadBindingOverrides(saved));

        Assert.Equal("<Keyboard>/space", fresh.FindAction("Player/Fire").Bindings[0].EffectivePath);
        Assert.Equal("<Keyboard>/up", fresh.FindAction("Player/Move").Bindings[0].Parts[0].EffectivePath);
    }

    [Fact]
    public void SkipsAnOverrideForSomethingThatNoLongerExists() {
        // A player's saved file was written against a previous version of the game's asset. One
        // renamed action must not cost them every other binding they configured.
        var applied = actions.LoadBindingOverrides(
            """
            # rebindings
            Player/Sprint[0]=<Keyboard>/leftShift
            Player/Fire[9]=<Keyboard>/f
            Player/Move[0][7]=<Keyboard>/up
            Player/Fire[0]=<Keyboard>/space
            """
        );

        Assert.Equal(1, applied);
        Assert.Equal("<Keyboard>/space", actions.FindAction("Player/Fire").Bindings[0].EffectivePath);
    }

    [Fact]
    public void PutsEveryBindingBack() {
        actions.FindAction("Player/Fire").OverrideBinding(0, "<Keyboard>/space");
        actions.FindAction("Player/Move").OverrideBinding(0, "<Keyboard>/up", 0);

        actions.RemoveAllBindingOverrides();

        Assert.Equal(string.Empty, actions.SaveBindingOverrides());
        Assert.Equal("<Mouse>/primary", actions.FindAction("Player/Fire").Bindings[0].EffectivePath);
        Assert.Equal("<Keyboard>/w", actions.FindAction("Player/Move").Bindings[0].Parts[0].EffectivePath);
    }

    [Fact]
    public void ReadsARebindingImmediately() {
        var fire = actions.FindAction("Player/Fire");
        fire.OverrideBinding(0, "<Keyboard>/space");

        devices.SubmitKey(InputKey.Space, true);
        actions.Update(devices, now);

        Assert.True(fire.IsPressed);
    }

    [Fact]
    public void FindsEveryActionSharingAControl() {
        var conflicts = new List<InputBindingConflict>();
        actions.FindConflicts(InputControl.Key(InputKey.W), conflicts);

        Assert.Single(conflicts);
        Assert.Equal("Player/Move", conflicts[0].Action.Path);
        Assert.Equal(0, conflicts[0].BindingIndex);
        Assert.Equal(0, conflicts[0].PartIndex);
    }

    [Fact]
    public void DoesNotReportAnActionConflictingWithItself() {
        var conflicts = new List<InputBindingConflict>();
        actions.FindConflicts(InputControl.Key(InputKey.W), conflicts, actions.FindAction("Player/Move"));

        Assert.Empty(conflicts);
    }

    [Fact]
    public void DrivesARebindingThroughTheService() {
        var service = new InputService(devices);
        service.Add(actions);

        var operation = new InputRebindingOperation(actions, actions.FindAction("Player/Fire"), 0).Start();
        service.Rebinding = operation;

        devices.SubmitKey(InputKey.Space, true);
        service.Update(now);
        service.Update(now + 1.0);

        Assert.Equal(InputRebindingState.Completed, operation.State);
        Assert.Null(service.Rebinding);
    }

    /// <summary>Presses through the settle window: seen once, then seen long enough.</summary>
    void Settle(InputRebindingOperation operation) {
        operation.Update(devices, now);
        now += 1.0;
        operation.Update(devices, now);
    }
}
