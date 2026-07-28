// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Xr.Input;
using Xunit;

namespace Vixen.Xr.Tests;

/// <summary>The action model: what a game declares before a session exists.</summary>
public sealed class XrActionSetTests {
    [Fact]
    public void AnActionSetHoldsItsActions() {
        var set = new XrActionSet("gameplay", "Gameplay", priority: 3);
        var fire = set.CreateAction("fire", XrActionType.Boolean, "Fire");

        Assert.Equal("gameplay", set.Name);
        Assert.Equal("Gameplay", set.LocalisedName);
        Assert.Equal(3, set.Priority);
        Assert.Same(set, fire.Set);
        Assert.Equal([fire], set.Actions);
    }

    [Fact]
    public void ALocalisedNameDefaultsToTheName() {
        var set = new XrActionSet("gameplay");

        Assert.Equal("gameplay", set.LocalisedName);
        Assert.Equal("fire", set.CreateAction("fire", XrActionType.Boolean).LocalisedName);
    }

    [Fact]
    public void TwoActionsCannotShareAName() {
        var set = new XrActionSet("gameplay");

        set.CreateAction("fire", XrActionType.Boolean);

        Assert.Throws<ArgumentException>(() => set.CreateAction("fire", XrActionType.Float));
    }

    [Fact]
    public void ABindingForAnotherSetsActionIsRefused() {
        var gameplay = new XrActionSet("gameplay");
        var menu = new XrActionSet("menu");
        var fire = gameplay.CreateAction("fire", XrActionType.Boolean);

        Assert.Throws<ArgumentException>(
            () => menu.SuggestBinding(XrInteractionProfiles.Simple, fire, XrPaths.SelectClick)
        );
    }

    [Fact]
    public void BothHandsGetTheSameBindingWithTheirOwnPath() {
        var set = new XrActionSet("gameplay");
        var grab = set.CreateAction("grab", XrActionType.Float);

        set.SuggestBindingForBothHands(XrInteractionProfiles.OculusTouch, grab, XrPaths.SqueezeValue);

        Assert.Equal(
            ["/user/hand/left/input/squeeze/value", "/user/hand/right/input/squeeze/value"],
            set.Bindings.Select(binding => binding.BindingPath)
        );

        Assert.All(
            set.Bindings,
            binding => Assert.Equal(XrInteractionProfiles.OculusTouch, binding.InteractionProfile)
        );
    }

    [Fact]
    public void APressIsSeenOnEitherHand() {
        var set = new XrActionSet("gameplay");
        var grab = set.CreateAction("grab", XrActionType.Boolean);

        grab.Publish(XrHand.Right, new XrActionState(IsActive: true, Boolean: true));

        Assert.True(grab.IsPressed);
        Assert.False(grab.State(XrHand.Left).Boolean);
    }

    [Fact]
    public void AnEdgeIsThePressAndTheChangeTogether() {
        // What almost every use of a button actually wants, and what a game otherwise reimplements
        // per action with a field holding last frame's value.
        var set = new XrActionSet("gameplay");
        var jump = set.CreateAction("jump", XrActionType.Boolean);

        jump.Publish(XrHand.Left, new XrActionState(IsActive: true, Changed: true, Boolean: true));
        Assert.True(jump.WasPressedThisFrame);

        jump.Publish(XrHand.Left, new XrActionState(IsActive: true, Boolean: true));
        Assert.True(jump.IsPressed);
        Assert.False(jump.WasPressedThisFrame);
    }

    [Fact]
    public void AnInactiveActionIsNotPressedEvenWhenItsValueSaysItIs() {
        // What a controller that has been put down looks like: the last value is still there and it
        // must not count.
        var set = new XrActionSet("gameplay");
        var fire = set.CreateAction("fire", XrActionType.Boolean);

        fire.Publish(XrHand.Right, new XrActionState(IsActive: false, Boolean: true));

        Assert.False(fire.IsPressed);
    }

    [Fact]
    public void DeactivatingClearsBothHands() {
        var set = new XrActionSet("gameplay");
        var fire = set.CreateAction("fire", XrActionType.Boolean);

        fire.Publish(XrHand.Left, new XrActionState(IsActive: true, Boolean: true));
        fire.Publish(XrHand.Right, new XrActionState(IsActive: true, Boolean: true));
        fire.Deactivate();

        Assert.False(fire.State(XrHand.Left).IsActive);
        Assert.False(fire.State(XrHand.Right).IsActive);
    }

    [Fact]
    public void AnEmptyNameIsRefused() {
        Assert.Throws<ArgumentException>(() => new XrActionSet(""));
        Assert.Throws<ArgumentException>(() => new XrActionSet("gameplay").CreateAction("", XrActionType.Boolean));
    }
}
