// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;

namespace Vixen.Editor.Inspector;

/// <summary>What a member holds across a selection: one value, or the fact that they disagree.</summary>
/// <param name="Value">The value they share, or <see langword="null" /> when they do not.</param>
/// <param name="IsMixed">Whether the selected objects hold different values.</param>
/// <remarks>
///     ⚠ <b>Disagreement is a state, not an average.</b> Twenty objects with three different values
///     must not show one of them as though it were the answer — the user would change something else
///     and never know it happened.
/// </remarks>
public readonly record struct InspectorValue(object? Value, bool IsMixed) {
    /// <summary>The value if they agree, or a fallback if they do not.</summary>
    /// <typeparam name="T">What it holds.</typeparam>
    /// <param name="fallback">What to answer when they disagree.</param>
    /// <returns>The value.</returns>
    public T Or<T>(T fallback) => IsMixed || Value is not T value ? fallback : value;
}

/// <summary>One member bound to the objects being inspected: read it, write it, put it back.</summary>
/// <remarks>
///     <para>
///         A drawer is handed one of these and nothing else. It is what makes a drawer's job "turn a
///         value into controls and controls back into a value" rather than "and also find the undo
///         stack, and also handle the case where twenty objects are selected, and also decide whether
///         the reset button is shown".
///     </para>
///     <para>
///         <b>Every write goes through the document's command stack</b>, so undo works without any
///         per-drawer effort — which is the whole reason this type exists between the drawer and the
///         member. A field with no document writes directly and is not undoable; that is the case
///         where the inspector is previewing an asset nobody is editing.
///     </para>
/// </remarks>
public sealed class InspectorField {
    int refreshing;

    /// <summary>The member being edited.</summary>
    public InspectorMember Member { get; }

    /// <summary>What it is being edited on, in selection order.</summary>
    public IReadOnlyList<object> Targets { get; }

    /// <summary>Where the writes are recorded, if anywhere.</summary>
    public EditorDocument? Document { get; }

    /// <summary>What the objects were made from, for revert-to-prefab.</summary>
    public IPrefabSource? Prefab { get; }

    /// <summary>Raised after a write of any kind lands.</summary>
    /// <remarks>
    ///     What the view listens to so the row restates itself, and what a viewport listens to so a
    ///     gizmo follows a number typed into the inspector. It fires for every path that writes —
    ///     drawer, reset, paste, revert — because a view that only heard about some of them shows a
    ///     stale reset button after the others.
    /// </remarks>
    public event Action<InspectorField>? Changed;

    /// <summary>The type the whole selection has in common.</summary>
    public InspectorDescriptor Descriptor { get; }

    /// <summary>Whether the inspector will let this member be written.</summary>
    public bool CanWrite => Member.CanWrite && !Member.IsReadOnly && Targets.Count > 0;

    /// <summary>Binds a member to a selection.</summary>
    /// <param name="descriptor">The type the selection has in common.</param>
    /// <param name="member">The member.</param>
    /// <param name="targets">The objects, in selection order.</param>
    /// <param name="document">Where writes are recorded, or <see langword="null" />.</param>
    /// <param name="prefab">What the objects were made from, or <see langword="null" />.</param>
    public InspectorField(
        InspectorDescriptor descriptor,
        InspectorMember member,
        IReadOnlyList<object> targets,
        EditorDocument? document = null,
        IPrefabSource? prefab = null
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(targets);

        Descriptor = descriptor;
        Member = member;
        Targets = targets;
        Document = document;
        Prefab = prefab;
    }

    /// <summary>Reads the member off every target.</summary>
    /// <returns>The shared value, or the fact that they differ.</returns>
    public InspectorValue Read() {
        if (Targets.Count == 0) {
            return new(null, false);
        }

        var first = Member.GetBoxed(Targets[0]);

        for (var index = 1; index < Targets.Count; index++) {
            if (!Equals(first, Member.GetBoxed(Targets[index]))) {
                return new(null, true);
            }
        }

        return new(first, false);
    }

    /// <summary>Writes the member on every target, undoably.</summary>
    /// <param name="value">The new value, boxed.</param>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     <para>
    ///         Writing the value they all already hold does nothing and records nothing — a slider
    ///         that re-emits its position every frame of a drag it is not moving in leaves no trace,
    ///         the same rule <c>EditorProperty.Set</c> follows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A mixed field always writes.</b> The values differ, so there is no "already holds
    ///         it" to short-circuit on, and short-circuiting on the primary object's value would make
    ///         "apply to all" silently do nothing whenever the value you typed happened to match the
    ///         one you were looking at.
    ///     </para>
    /// </remarks>
    public bool Write(object? value) {
        if (!CanWrite || refreshing > 0) {
            return false;
        }

        var current = Read();

        if (!current.IsMixed && Equals(current.Value, value)) {
            return false;
        }

        // The condition decides who the edit reaches, not merely whether the row is drawn: a row is
        // shown when any object would show it, and writing it to the ones whose flag is off would be
        // editing a member the user cannot see the state of.
        var reached = WritableTargets;

        return reached.Count != 0 && Apply(reached, value);
    }

    /// <summary>Holds off writes while a drawer is being filled in from the model.</summary>
    /// <returns>A scope to dispose when the drawer is done.</returns>
    /// <remarks>
    ///     ⚠ <b>Without this a mixed field destroys the values it was showing.</b> Putting a value
    ///     into a control raises the control's own changed event, which calls <see cref="Write" />;
    ///     ordinarily that is harmless because the value is the one already held. A <i>mixed</i> field
    ///     has no such value — it parks the control at a neutral position, unchecked or empty — and
    ///     that neutral position would be written to every selected object the moment the row was
    ///     drawn. Every inspector has this bug once.
    ///     <para>
    ///         Here rather than in each drawer, so a third-party drawer gets it without knowing it
    ///         exists, and reference-counted so a composite drawer refreshing its children nests.
    ///     </para>
    /// </remarks>
    public IDisposable Refreshing() {
        refreshing++;
        return new RefreshScope(this);
    }

    /// <summary>Ends the run of edits that were collapsing into one undo entry.</summary>
    /// <remarks>
    ///     Called on mouse-up and on focus loss. A time window would make how many undo steps an edit
    ///     produced depend on how fast somebody moved a mouse — see <c>CommandStack.Seal</c>.
    /// </remarks>
    public void Seal() => Document?.Stack.Seal();

    /// <summary>Whether the member differs from what a fresh instance of the type has.</summary>
    public bool IsModified {
        get {
            if (!Descriptor.TryGetDefault(Member, out var initial)) {
                return false;
            }

            foreach (var target in Targets) {
                if (!Equals(initial, Member.GetBoxed(target))) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether there is a default to put back.</summary>
    public bool CanReset => CanWrite && Descriptor.TryGetDefault(Member, out _);

    /// <summary>Puts the member back to what a fresh instance of the type has.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Reset() => Descriptor.TryGetDefault(Member, out var initial) && Write(initial);

    /// <summary>Whether any target overrides this member against the prefab it came from.</summary>
    public bool IsOverridden {
        get {
            if (Prefab is null) {
                return false;
            }

            foreach (var target in Targets) {
                if (Prefab.IsOverridden(target, Member)) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Puts the member back to the prefab's value, per object.</summary>
    /// <returns>Whether anything changed.</returns>
    /// <remarks>
    ///     ⚠ <b>Per object, so this is not one <see cref="Write" />.</b> Two instances of one prefab
    ///     that were both overridden revert to the <i>same</i> prefab value, but two instances of
    ///     <i>different</i> prefabs do not — and an inspector that reverted them both to the primary
    ///     object's source would quietly rewrite the other one. The edits are wrapped in a
    ///     transaction so the whole revert is still one undo step.
    /// </remarks>
    public bool RevertToPrefab() {
        if (Prefab is null || !CanWrite) {
            return false;
        }

        using var transaction = Document?.Stack.BeginTransaction($"Revert {Member.DisplayName}");
        var changed = false;

        foreach (var target in Targets) {
            if (!Prefab.TryGetPrefabValue(target, Member, out var original)) {
                continue;
            }

            if (Equals(Member.GetBoxed(target), original)) {
                continue;
            }

            changed |= Apply([target], original);
        }

        return changed;
    }

    /// <summary>Whether the member is shown at all, given its condition.</summary>
    /// <remarks>
    ///     ⚠ <b>Any target showing it is enough.</b> Hiding a row because one of twenty objects has
    ///     the flag off is how an edit silently misses the other nineteen.
    /// </remarks>
    public bool IsVisible {
        get {
            if (Member.Condition is not { } condition || Targets.Count == 0) {
                return true;
            }

            if (!Descriptor.TryGetMember(condition, out var flag)) {
                // A condition naming a member that is not described is a mistake the generator
                // reports. At run time the row is shown, because a hidden row nobody can find is a
                // worse outcome than a row that should not have been there.
                return true;
            }

            foreach (var target in Targets) {
                if (flag.GetBoxed(target) is bool value && value != Member.ConditionNegated) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The targets whose condition is satisfied, which are the ones an edit reaches.</summary>
    public IReadOnlyList<object> WritableTargets {
        get {
            if (Member.Condition is not { } condition || !Descriptor.TryGetMember(condition, out var flag)) {
                return Targets;
            }

            List<object> writable = [];

            foreach (var target in Targets) {
                if (flag.GetBoxed(target) is bool value && value != Member.ConditionNegated) {
                    writable.Add(target);
                }
            }

            return writable;
        }
    }

    /// <summary>Records the edit on the document's stack, or writes straight through when there is none.</summary>
    /// <remarks>
    ///     ⚠ <b>An unbound field writes and is not undoable.</b> That is the case where the inspector
    ///     is previewing an asset nobody has open — a material thumbnail, a fixture in a test — and
    ///     the alternatives are refusing to draw it or manufacturing a document for it. Both are
    ///     worse, and it is the same rule <c>EditorProperty.Set</c> follows for an object with no
    ///     document.
    /// </remarks>
    bool Apply(IReadOnlyList<object> reached, object? value) {
        if (Document?.Stack is { } stack) {
            stack.Execute(Member.CreateSetCommand(reached, value, Document));
            Changed?.Invoke(this);

            return true;
        }

        foreach (var target in reached) {
            Member.SetBoxed(target, value);
        }

        Changed?.Invoke(this);
        return true;
    }

    sealed class RefreshScope(InspectorField field) : IDisposable {
        bool done;

        public void Dispose() {
            if (!done) {
                done = true;
                field.refreshing--;
            }
        }
    }
}

/// <summary>Where an inspected object came from, for override indication and revert.</summary>
/// <remarks>
///     An interface rather than something this assembly implements, because what a prefab
///     <i>is</i> belongs to the scene document — this assembly only needs two questions answered
///     about it, and asking them through a contract is what keeps the inspector usable in a test
///     with no project on disk.
/// </remarks>
public interface IPrefabSource {
    /// <summary>Whether an object's member differs from the prefab it was made from.</summary>
    /// <param name="target">The object.</param>
    /// <param name="member">The member.</param>
    /// <returns>Whether it is an override.</returns>
    bool IsOverridden(object target, InspectorMember member);

    /// <summary>The value the prefab has for a member.</summary>
    /// <param name="target">The instance.</param>
    /// <param name="member">The member.</param>
    /// <param name="value">The prefab's value.</param>
    /// <returns>Whether the object came from a prefab that has this member.</returns>
    bool TryGetPrefabValue(object target, InspectorMember member, out object? value);
}
