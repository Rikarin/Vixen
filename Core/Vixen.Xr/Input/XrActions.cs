// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Xr.Input;

/// <summary>What kind of value an action carries.</summary>
public enum XrActionType : byte {
    /// <summary>Pressed or not. A button, or a trigger past its threshold.</summary>
    Boolean = 0,

    /// <summary>A scalar, usually 0 to 1. A trigger, a grip.</summary>
    Float = 1,

    /// <summary>Two axes. A thumbstick, a trackpad.</summary>
    Vector2 = 2,

    /// <summary>A tracked pose. A controller's grip or aim.</summary>
    Pose = 3,

    /// <summary>Not an input at all: something to play a rumble on.</summary>
    Haptic = 4
}

/// <summary>What an action was worth this frame.</summary>
/// <param name="IsActive">
///     Whether the runtime is currently able to produce a value. False for a controller that is not
///     turned on, an action nothing is bound to, and every action while the session lacks focus.
/// </param>
/// <param name="Changed">Whether it differs from last frame — which is what an edge-triggered read wants.</param>
/// <param name="Boolean">Its value, for a boolean action.</param>
/// <param name="Float">Its value, for a float action.</param>
/// <param name="Vector">Its value, for a two-axis action.</param>
/// <param name="Pose">Its value, for a pose action.</param>
/// <param name="IsTracked">
///     Whether a pose action's pose is being tracked right now, rather than extrapolated from where
///     the device was last seen.
/// </param>
/// <remarks>
///     One struct for every type rather than five, because the alternative is a generic action and a
///     generic action cannot live in one list that the session syncs. The unused fields are four
///     bytes each and the clarity of <c>action.State(hand).Float</c> is worth them.
/// </remarks>
public readonly record struct XrActionState(
    bool IsActive,
    bool Changed = false,
    bool Boolean = false,
    float Float = 0f,
    Vector2 Vector = default,
    XrPose Pose = default,
    bool IsTracked = false
);

/// <summary>What to play on a haptic action.</summary>
/// <param name="Duration">How long for. Zero asks the runtime for its minimum.</param>
/// <param name="Frequency">In hertz, or <c>0</c> to let the runtime choose.</param>
/// <param name="Amplitude">From 0 to 1.</param>
public readonly record struct XrHapticPulse(TimeSpan Duration, float Frequency = 0f, float Amplitude = 1f) {
    /// <summary>A short click, which is what most feedback actually is.</summary>
    public static XrHapticPulse Click => new(TimeSpan.FromMilliseconds(20), 0f, 0.6f);
}

/// <summary>One thing the game wants to know about or do, independent of what does it.</summary>
/// <remarks>
///     <para>
///         <b>Actions, not buttons, and this is the whole of OpenXR's input design.</b> A game asks
///         for "teleport" and the runtime decides that on this headset that is the right thumbstick
///         forward and on that one it is the trackpad — and the user can rebind it. A game that read
///         buttons directly would need a table per controller and would break on the next one.
///     </para>
///     <para>
///         <b>One action, both hands.</b> A grab is one action with a state per hand rather than two
///         actions, which is what makes "either hand can pick things up" one piece of code. That is
///         what OpenXR calls a subaction path, and it is why <see cref="State" /> takes a hand.
///     </para>
/// </remarks>
public sealed class XrAction {
    readonly XrActionState[] states = new XrActionState[2];

    internal XrAction(XrActionSet set, string name, XrActionType type, string localisedName) {
        Set = set;
        Name = name;
        Type = type;
        LocalisedName = string.IsNullOrEmpty(localisedName) ? name : localisedName;
    }

    /// <summary>The set it belongs to.</summary>
    public XrActionSet Set { get; }

    /// <summary>Its name, which must be lower-case and is what the runtime knows it as.</summary>
    public string Name { get; }

    /// <summary>What it is called in the runtime's own rebinding interface, in the user's language.</summary>
    public string LocalisedName { get; }

    /// <summary>What kind of value it carries.</summary>
    public XrActionType Type { get; }

    /// <summary>Whatever the backend needs to find this action again. Not for callers.</summary>
    public object? BackendHandle { get; set; }

    /// <summary>What it was worth this frame.</summary>
    /// <param name="hand">Which hand's binding.</param>
    /// <returns>The state, inactive if nothing has synced yet.</returns>
    public XrActionState State(XrHand hand) => states[(int)hand];

    /// <summary>Whether it is pressed on either hand.</summary>
    public bool IsPressed => states[0] is { IsActive: true, Boolean: true }
        || states[1] is { IsActive: true, Boolean: true };

    /// <summary>Whether it became pressed this frame, on either hand.</summary>
    /// <remarks>
    ///     The edge rather than the level, which is what almost every use of a button actually wants
    ///     and what a game otherwise reimplements per action with a field of last frame's value.
    /// </remarks>
    public bool WasPressedThisFrame => states[0] is { IsActive: true, Boolean: true, Changed: true }
        || states[1] is { IsActive: true, Boolean: true, Changed: true };

    /// <summary>Sets a hand's state. Called by the session during a sync.</summary>
    /// <param name="hand">Which hand.</param>
    /// <param name="state">Its state.</param>
    public void Publish(XrHand hand, in XrActionState state) => states[(int)hand] = state;

    /// <summary>Marks every hand inactive, which is what an unfocused session's actions are.</summary>
    public void Deactivate() {
        states[0] = default;
        states[1] = default;
    }
}

/// <summary>A binding a game suggests to the runtime, which the runtime may or may not honour.</summary>
/// <param name="InteractionProfile">Which controller it is about — see <see cref="XrInteractionProfiles" />.</param>
/// <param name="Action">The action.</param>
/// <param name="BindingPath">The input it should come from — see <see cref="XrPaths" />.</param>
public readonly record struct XrSuggestedBinding(string InteractionProfile, XrAction Action, string BindingPath);

/// <summary>A group of actions that are relevant at the same time.</summary>
/// <remarks>
///     <para>
///         Sets exist so that a game can turn its whole input scheme over at once: the menu's set is
///         active while the menu is up and the gameplay set is not, so the trigger means two things
///         and neither piece of code has to know about the other.
///     </para>
///     <para>
///         <b>Everything is declared before the session attaches them and nothing after.</b> That is
///         OpenXR's rule, not this module's — attaching is when the runtime resolves bindings, and it
///         is also when the user's own remapping is applied. An action created afterwards can never
///         be bound to anything.
///     </para>
/// </remarks>
public sealed class XrActionSet {
    readonly List<XrAction> actions = [];
    readonly List<XrSuggestedBinding> bindings = [];

    /// <summary>Creates a set.</summary>
    /// <param name="name">Its name, lower-case, as the runtime knows it.</param>
    /// <param name="localisedName">What to call it in the runtime's rebinding interface.</param>
    /// <param name="priority">
    ///     Which set wins when two bind the same physical input. Higher wins; the runtime resolves it.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public XrActionSet(string name, string localisedName = "", int priority = 0) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        Name = name;
        LocalisedName = string.IsNullOrEmpty(localisedName) ? name : localisedName;
        Priority = priority;
    }

    /// <summary>Its name.</summary>
    public string Name { get; }

    /// <summary>What it is called in the user's language.</summary>
    public string LocalisedName { get; }

    /// <summary>Which set wins a contested binding. Higher is stronger.</summary>
    public int Priority { get; }

    /// <summary>Whether it is currently being synced.</summary>
    /// <remarks>
    ///     Settable at any time, unlike everything else here: this is the switch a game flips when
    ///     the menu opens, and it is the reason sets exist.
    /// </remarks>
    public bool IsActive { get; set; } = true;

    /// <summary>Whether a session has attached it, after which it is frozen.</summary>
    public bool IsAttached { get; private set; }

    /// <summary>Whatever the backend needs to find this set again. Not for callers.</summary>
    public object? BackendHandle { get; set; }

    /// <summary>Every action in it.</summary>
    public IReadOnlyList<XrAction> Actions => actions;

    /// <summary>Every binding suggested for it.</summary>
    public IReadOnlyList<XrSuggestedBinding> Bindings => bindings;

    /// <summary>Declares an action.</summary>
    /// <param name="name">Its name, lower-case.</param>
    /// <param name="type">What it carries.</param>
    /// <param name="localisedName">What to call it in the user's language.</param>
    /// <returns>The action.</returns>
    /// <exception cref="ArgumentException">The name is empty or already used.</exception>
    /// <exception cref="InvalidOperationException">The set has been attached.</exception>
    public XrAction CreateAction(string name, XrActionType type, string localisedName = "") {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfAttached();

        foreach (var existing in actions) {
            if (existing.Name == name) {
                throw new ArgumentException($"'{Name}' already has an action called '{name}'.", nameof(name));
            }
        }

        var action = new XrAction(this, name, type, localisedName);

        actions.Add(action);

        return action;
    }

    /// <summary>Suggests where an action should come from on a particular controller.</summary>
    /// <param name="interactionProfile">Which controller — an <see cref="XrInteractionProfiles" /> path.</param>
    /// <param name="action">The action, which must belong to this set.</param>
    /// <param name="bindingPath">Which input — an <see cref="XrPaths" /> path.</param>
    /// <exception cref="ArgumentException">The action belongs to another set.</exception>
    /// <exception cref="InvalidOperationException">The set has been attached.</exception>
    /// <remarks>
    ///     A suggestion, and the word is the specification's. The runtime is free to ignore it — and
    ///     will, if the user has rebound the action or if the profile does not have that input. A game
    ///     that assumed its suggestion was honoured would be a game that could not be rebound, which
    ///     is the thing this design exists to prevent.
    /// </remarks>
    public void SuggestBinding(string interactionProfile, XrAction action, string bindingPath) {
        ArgumentException.ThrowIfNullOrEmpty(interactionProfile);
        ArgumentException.ThrowIfNullOrEmpty(bindingPath);
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfAttached();

        if (action.Set != this) {
            throw new ArgumentException(
                $"Action '{action.Name}' belongs to '{action.Set.Name}', not to '{Name}'.",
                nameof(action)
            );
        }

        bindings.Add(new XrSuggestedBinding(interactionProfile, action, bindingPath));
    }

    /// <summary>Suggests the same binding for both hands, with <c>/user/hand/{left,right}</c> filled in.</summary>
    /// <param name="interactionProfile">Which controller.</param>
    /// <param name="action">The action.</param>
    /// <param name="component">
    ///     The part after the hand — <c>input/trigger/value</c>, <c>output/haptic</c>.
    /// </param>
    /// <remarks>
    ///     The overwhelmingly common case, and worth a method: writing the two paths out is two lines
    ///     that differ by four characters, which is exactly the shape of a typo nothing catches until
    ///     a play test finds that the left hand does nothing.
    /// </remarks>
    public void SuggestBindingForBothHands(string interactionProfile, XrAction action, string component) {
        SuggestBinding(interactionProfile, action, $"{XrPaths.LeftHand}/{component}");
        SuggestBinding(interactionProfile, action, $"{XrPaths.RightHand}/{component}");
    }

    /// <summary>Marks the set attached. Called by the session.</summary>
    public void MarkAttached() => IsAttached = true;

    void ThrowIfAttached() {
        if (IsAttached) {
            throw new InvalidOperationException(
                $"Action set '{Name}' has been attached to a session, and a session's actions are fixed "
                + "for its lifetime — the runtime resolved the bindings, and the user's own remapping "
                + "with them. Declare every action before the session starts."
            );
        }
    }
}

/// <summary>The interaction profiles a game is likely to suggest bindings for.</summary>
/// <remarks>
///     Paths, and deliberately strings rather than an enum: the set grows with every headset, a
///     runtime accepts profiles this engine has never heard of, and an enum would make supporting a
///     new controller an engine release. These are the common ones spelled correctly, which is the
///     only value a constant can add here.
/// </remarks>
public static class XrInteractionProfiles {
    /// <summary>The baseline every runtime must support: select and menu, and a pose.</summary>
    /// <remarks>
    ///     Bind everything essential here as well as to the specific profiles. It is what makes a game
    ///     work on a headset nobody had when it shipped, in the reduced form the specification
    ///     guarantees.
    /// </remarks>
    public const string Simple = "/interaction_profiles/khr/simple_controller";

    /// <summary>Meta Quest and Rift Touch controllers.</summary>
    public const string OculusTouch = "/interaction_profiles/oculus/touch_controller";

    /// <summary>Valve Index controllers.</summary>
    public const string ValveIndex = "/interaction_profiles/valve/index_controller";

    /// <summary>HTC Vive wands.</summary>
    public const string HtcVive = "/interaction_profiles/htc/vive_controller";

    /// <summary>Windows Mixed Reality motion controllers.</summary>
    public const string MixedReality = "/interaction_profiles/microsoft/motion_controller";
}

/// <summary>The input paths a binding names.</summary>
public static class XrPaths {
    /// <summary>The left hand's subaction path.</summary>
    public const string LeftHand = "/user/hand/left";

    /// <summary>The right hand's.</summary>
    public const string RightHand = "/user/hand/right";

    /// <summary>Where the controller is held — what a hand model is drawn at.</summary>
    public const string GripPose = "input/grip/pose";

    /// <summary>Where the controller points — what a laser or a raycast uses.</summary>
    /// <remarks>
    ///     Not the same as the grip, and the difference is the whole reason both exist: a Vive wand
    ///     points along its own axis and an Index controller points forwards out of a fist. Aiming
    ///     with the grip pose is the classic reason a pointer feels wrong on one controller and fine
    ///     on another.
    /// </remarks>
    public const string AimPose = "input/aim/pose";

    /// <summary>The trigger, as a scalar.</summary>
    public const string TriggerValue = "input/trigger/value";

    /// <summary>The trigger, as a button.</summary>
    public const string TriggerClick = "input/trigger/click";

    /// <summary>The grip, as a scalar.</summary>
    public const string SqueezeValue = "input/squeeze/value";

    /// <summary>The grip, as a button.</summary>
    public const string SqueezeClick = "input/squeeze/click";

    /// <summary>The thumbstick's two axes.</summary>
    public const string ThumbstickPosition = "input/thumbstick";

    /// <summary>The thumbstick pressed in.</summary>
    public const string ThumbstickClick = "input/thumbstick/click";

    /// <summary>The primary button — A on a right Touch controller, X on a left one.</summary>
    public const string PrimaryButton = "input/x/click";

    /// <summary>The menu button, which every profile has.</summary>
    public const string MenuClick = "input/menu/click";

    /// <summary>The generic select, which the simple profile has instead of a trigger.</summary>
    public const string SelectClick = "input/select/click";

    /// <summary>Where a rumble is played.</summary>
    public const string Haptic = "output/haptic";
}
