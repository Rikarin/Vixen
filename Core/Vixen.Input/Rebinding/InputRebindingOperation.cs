// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Input;

/// <summary>Where an interactive rebinding has got to.</summary>
public enum InputRebindingState : byte {
    /// <summary>Not started.</summary>
    Idle = 0,

    /// <summary>Waiting for the player to press something.</summary>
    Listening = 1,

    /// <summary>
    ///     The player pressed something that is already bound elsewhere, and the operation is waiting
    ///     to be told what to do about it.
    /// </summary>
    Blocked = 2,

    /// <summary>The binding was replaced.</summary>
    Completed = 3,

    /// <summary>The player backed out, or the game gave up.</summary>
    Canceled = 4
}

/// <summary>"Press the key you want to use for this."</summary>
/// <remarks>
///     <para>
///         <b>This is what the action model exists for.</b> Everything else in this assembly can be
///         approximated by polling a device; rebinding cannot, because it needs to know what every
///         action is bound to, which bindings belong to the scheme the player is configuring, and how
///         to write the answer somewhere that survives a patch.
///     </para>
///     <para>
///         <b>The actions are disabled while it runs</b>, and that is not a nicety: the key the
///         player presses to choose a new "jump" would otherwise also make the character jump, and
///         the menu behind the dialog would act on every candidate.
///     </para>
///     <para>
///         <b>A conflict stops the operation rather than resolving it.</b> Whether binding "crouch"
///         to the key "jump" already uses should refuse, swap the two, or be allowed is a game's
///         decision — a shooter and a strategy game answer it differently — so the operation reports
///         <see cref="InputRebindingState.Blocked" /> with the conflicts listed and waits for
///         <see cref="ApplyAnyway" /> or <see cref="Cancel" />.
///     </para>
/// </remarks>
public sealed class InputRebindingOperation {
    readonly List<InputControl> excluded = [];
    readonly List<InputDeviceKind> allowed = [];
    readonly List<InputBindingConflict> conflicts = [];

    InputControl candidate;
    double candidateSince;
    bool wasEnabled;

    /// <summary>Prepares a rebinding.</summary>
    /// <param name="actions">The asset the action belongs to, for conflict detection.</param>
    /// <param name="action">The action being rebound.</param>
    /// <param name="bindingIndex">Which of its bindings.</param>
    /// <param name="partIndex">Which composite part, or <c>-1</c> for a plain binding.</param>
    public InputRebindingOperation(InputActions actions, InputAction action, int bindingIndex, int partIndex = -1) {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(bindingIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bindingIndex, action.Bindings.Count);

        Actions = actions;
        Action = action;
        BindingIndex = bindingIndex;
        PartIndex = partIndex;
    }

    /// <summary>The asset the action belongs to.</summary>
    public InputActions Actions { get; }

    /// <summary>The action being rebound.</summary>
    public InputAction Action { get; }

    /// <summary>Which of its bindings.</summary>
    public int BindingIndex { get; }

    /// <summary>Which composite part, or <c>-1</c>.</summary>
    public int PartIndex { get; }

    /// <summary>Where the operation has got to.</summary>
    public InputRebindingState State { get; private set; }

    /// <summary>The control the player chose, once there is one.</summary>
    public InputControl SelectedControl { get; private set; }

    /// <summary>The actions already bound to <see cref="SelectedControl" />.</summary>
    public IReadOnlyList<InputBindingConflict> Conflicts => conflicts;

    /// <summary>How far an analogue control must move before it counts as the answer.</summary>
    public float Threshold { get; set; } = InputSettings.DefaultRebindThreshold;

    /// <summary>How long a control must stay actuated before it is accepted, in seconds.</summary>
    /// <remarks>
    ///     A stick crosses every value on its way to the corner, and a mouse button that bounces once
    ///     on a worn switch would otherwise be captured twice. Short enough that nobody notices it.
    /// </remarks>
    public float SettleTime { get; set; } = 0.05f;

    /// <summary>Whether a control already bound elsewhere is accepted without asking.</summary>
    public bool AllowConflicts { get; set; }

    /// <summary>Whether to turn the asset's maps off while listening.</summary>
    public bool DisableActionsWhileListening { get; set; } = true;

    /// <summary>
    ///     Which scheme's bindings conflicts are looked for in, or <see langword="null" /> for all.
    /// </summary>
    /// <remarks>
    ///     Normally the scheme the player is configuring: a key bound in the keyboard scheme does not
    ///     clash with a gamepad button, and reporting that it does makes the screen unusable.
    /// </remarks>
    public string? ConflictScheme { get; set; }

    /// <summary>Raised when the operation reaches a terminal state, whichever one.</summary>
    public event Action<InputRebindingOperation>? Finished;

    /// <summary>Raised each time a candidate control is accepted, before conflicts are considered.</summary>
    public event Action<InputRebindingOperation>? CandidateSelected;

    /// <summary>Refuses a control — <c>Escape</c>, the pointer, whatever the game reserves.</summary>
    /// <param name="control">The control.</param>
    /// <returns>This operation, for chaining.</returns>
    public InputRebindingOperation Excluding(InputControl control) {
        excluded.Add(control);
        return this;
    }

    /// <summary>Limits the operation to one family of device.</summary>
    /// <param name="kind">The family. Called more than once to allow several.</param>
    /// <returns>This operation, for chaining.</returns>
    /// <remarks>
    ///     What a per-scheme rebinding screen sets. Without it, a player configuring their keyboard
    ///     bindings who rests a thumb on a controller gets the controller.
    /// </remarks>
    public InputRebindingOperation WithDevice(InputDeviceKind kind) {
        allowed.Add(kind);
        return this;
    }

    /// <summary>Starts listening.</summary>
    /// <returns>This operation, for chaining.</returns>
    public InputRebindingOperation Start() {
        if (State == InputRebindingState.Listening) {
            return this;
        }

        conflicts.Clear();
        candidate = InputControl.None;
        SelectedControl = InputControl.None;
        State = InputRebindingState.Listening;

        if (DisableActionsWhileListening) {
            wasEnabled = AnyMapEnabled();
            Actions.Disable();
        }

        return this;
    }

    /// <summary>Looks at what is actuated and decides whether it is the answer.</summary>
    /// <param name="devices">The devices, already given this frame's events.</param>
    /// <param name="time">The time now, in seconds, from a clock that only goes forward.</param>
    /// <remarks>Called once a frame while <see cref="State" /> is
    /// <see cref="InputRebindingState.Listening" />.</remarks>
    public void Update(InputDeviceSet devices, double time) {
        ArgumentNullException.ThrowIfNull(devices);

        if (State != InputRebindingState.Listening) {
            return;
        }

        var found = InputControl.None;
        var actuated = devices.Actuated(Threshold);

        for (var index = 0; index < actuated.Count; index++) {
            if (IsAcceptable(actuated[index])) {
                found = actuated[index];
                break;
            }
        }

        if (!found.IsValid) {
            candidate = InputControl.None;
            return;
        }

        if (found != candidate) {
            candidate = found;
            candidateSince = time;
            return;
        }

        if (time - candidateSince < SettleTime) {
            return;
        }

        Select(found);
    }

    /// <summary>Applies a binding that conflicts with another, having asked the player.</summary>
    /// <remarks>Only meaningful in <see cref="InputRebindingState.Blocked" />.</remarks>
    public void ApplyAnyway() {
        if (State == InputRebindingState.Blocked) {
            Apply();
        }
    }

    /// <summary>Gives up, leaving the binding as it was.</summary>
    public void Cancel() {
        if (State is InputRebindingState.Completed or InputRebindingState.Canceled) {
            return;
        }

        State = InputRebindingState.Canceled;
        Restore();
        Finished?.Invoke(this);
    }

    bool IsAcceptable(InputControl control) {
        if (allowed.Count > 0 && !allowed.Contains(control.Device)) {
            return false;
        }

        return !excluded.Contains(control);
    }

    void Select(InputControl control) {
        SelectedControl = control;
        CandidateSelected?.Invoke(this);

        conflicts.Clear();
        Actions.FindConflicts(control, conflicts, Action, ConflictScheme);

        if (conflicts.Count > 0 && !AllowConflicts) {
            // Deliberately still disabled. The dialog asking "replace the existing binding?" is on
            // screen and the control the player just pressed is still held; re-enabling here would
            // fire the action it clashes with while they read the question.
            State = InputRebindingState.Blocked;
            Finished?.Invoke(this);
            return;
        }

        Apply();
    }

    void Apply() {
        Action.OverrideBinding(BindingIndex, InputControlPath.Format(SelectedControl), PartIndex);
        State = InputRebindingState.Completed;
        Restore();
        Finished?.Invoke(this);
    }

    void Restore() {
        if (DisableActionsWhileListening && wasEnabled) {
            Actions.Enable();
        }
    }

    bool AnyMapEnabled() {
        var maps = Actions.Maps;

        for (var index = 0; index < maps.Count; index++) {
            if (maps[index].IsEnabled) {
                return true;
            }
        }

        return false;
    }
}
