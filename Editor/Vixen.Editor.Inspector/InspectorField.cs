// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;

namespace Vixen.Editor.Inspector;

/// <summary>One member bound to the objects being inspected, with the inspector's own vocabulary on top.</summary>
/// <remarks>
///     <para>
///         A drawer is handed one of these and nothing else. It is what makes a drawer's job "turn a
///         value into controls and controls back into a value" rather than "and also find the undo
///         stack, and also handle the case where twenty objects are selected, and also decide whether
///         the reset button is shown".
///     </para>
///     <para>
///         <b>Read, write, mixed, undo and the refresh guard are <see cref="EditProperty" />'s</b>,
///         which is the pipeline every editing surface in the editor writes through — doc 36 § D1. A
///         drawer therefore gets exactly what a scene-view tool or a plugin's panel gets, and the
///         four things below are what an <i>inspector</i> adds to it: a type's defaults to reset to,
///         a prefab to revert to, a condition that decides who an edit reaches, and the typed
///         <see cref="Member" /> the generated metadata hangs off.
///     </para>
/// </remarks>
public sealed class InspectorField : EditProperty {
    /// <summary>Whether the write in flight is a revert, which must not re-claim what it gives back.</summary>
    bool reverting;

    /// <summary>The type the whole selection has in common.</summary>
    public InspectorDescriptor Descriptor { get; }

    /// <summary>What the objects were made from, for revert-to-prefab.</summary>
    public IPrefabSource? Prefab { get; }

    /// <summary>The member being edited, with everything the generator recorded about it.</summary>
    /// <remarks>
    ///     ⚠ <b>Hides <see cref="EditProperty.Member" /> rather than being a second property.</b> It
    ///     is the same instance, narrowed: the pipeline only needs a name, a type and a write, and a
    ///     drawer needs the range, the header and the attribute list as well. C# has no covariant
    ///     property return, so this is what a narrowing looks like.
    /// </remarks>
    public new InspectorMember Member { get; }

    /// <inheritdoc />
    public override bool CanWrite => base.CanWrite && !Member.IsReadOnly;

    /// <inheritdoc />
    protected override IReadOnlyList<object> Reached => WritableTargets;

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
    ) : base(member, targets, document) {
        ArgumentNullException.ThrowIfNull(descriptor);

        Descriptor = descriptor;
        Member = member;
        Prefab = prefab;
    }

    /// <summary>Whether the member differs from what a fresh instance of the type has.</summary>
    public bool IsModified {
        get {
            if (!Descriptor.TryGetDefault(Member, out var initial)) {
                return false;
            }

            foreach (var target in Objects) {
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

            foreach (var target in Objects) {
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
    ///     ⚠ <b>Per object, so this is not one <see cref="EditProperty.Write" />.</b> Two instances of
    ///     one prefab that were both overridden revert to the <i>same</i> prefab value, but two
    ///     instances of <i>different</i> prefabs do not — and an inspector that reverted them both to
    ///     the primary object's source would quietly rewrite the other one. The edits are wrapped in a
    ///     transaction so the whole revert is still one undo step.
    /// </remarks>
    public bool RevertToPrefab() {
        if (Prefab is null || !CanWrite) {
            return false;
        }

        using var transaction = Document?.Stack.BeginTransaction($"Revert {Member.DisplayName}");
        var changed = false;

        // ⚠ Every write inside this loop is the template's value being handed back, so none of them
        // is the author claiming anything — see `Apply`. Without the flag a revert would write the
        // prefab's value and then record that the instance had chosen it, which is a revert button
        // that marks the row it just cleared.
        reverting = true;

        try {
            foreach (var target in Objects) {
                if (!Prefab.TryGetPrefabValue(target, Member, out var original)) {
                    continue;
                }

                if (!Equals(Member.GetBoxed(target), original)) {
                    changed |= Apply([target], original);
                }

                // ⚠⚠ Outside the value comparison above, and that placement is the whole point. An
                // override *to the template's own value* — the case doc 47 § 4 says the format exists
                // to express — writes nothing, because there is nothing to write; if dropping the
                // claim were inside the `if`, reverting one would do nothing at all and report that it
                // had. This is the line the zero-value sabotage kills.
                changed |= Prefab.Release(target, Member);
            }
        } finally {
            reverting = false;
        }

        return changed;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <b>Where an edit becomes an override.</b> The list of claimed members is the file's, not
    ///         a comparison — doc 47 § 4 — so somebody has to say "this one is the instance's own" at
    ///         the moment it is written, and this is the only moment that knows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A transaction only when a claim is actually new, which is what keeps a slider drag
    ///         one undo step.</b> A committed transaction records a <c>CompositeCommand</c>, and a
    ///         <c>SetMembersCommand</c> cannot merge with one — so wrapping <i>every</i> write would
    ///         turn a three-hundred-frame drag into three hundred entries. The predicate is
    ///         <see cref="IPrefabSource.TryGetPrefabValue" /> rather than
    ///         <see cref="IPrefabSource.IsOverridden" /> because the latter is false for every object
    ///         that never came from a prefab, which is nearly all of them.
    ///     </para>
    ///     <para>
    ///         The cost is one extra undo entry on the first edit of a member an instance had not
    ///         claimed: "Override Intensity" and then "Set Intensity". Every later edit of that member
    ///         merges as it always did.
    ///     </para>
    /// </remarks>
    protected override bool Apply(IReadOnlyList<object> reached, object? value) {
        if (reverting || Prefab is not { } source || !Claims(source, reached)) {
            return base.Apply(reached, value);
        }

        using var transaction = Document?.Stack.BeginTransaction($"Set {Member.DisplayName}");
        var written = base.Apply(reached, value);

        foreach (var target in reached) {
            source.Claim(target, Member);
        }

        return written;
    }

    /// <summary>Whether writing these objects would record a claim that is not already recorded.</summary>
    bool Claims(IPrefabSource source, IReadOnlyList<object> reached) {
        foreach (var target in reached) {
            if (!source.IsOverridden(target, Member) && source.TryGetPrefabValue(target, Member, out _)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the member is shown at all, given its condition.</summary>
    /// <remarks>
    ///     ⚠ <b>Any target showing it is enough.</b> Hiding a row because one of twenty objects has
    ///     the flag off is how an edit silently misses the other nineteen.
    /// </remarks>
    public bool IsVisible {
        get {
            if (Member.Condition is not { } condition || Objects.Count == 0) {
                return true;
            }

            if (!Descriptor.TryGetMember(condition, out var flag)) {
                // A condition naming a member that is not described is a mistake the generator
                // reports. At run time the row is shown, because a hidden row nobody can find is a
                // worse outcome than a row that should not have been there.
                return true;
            }

            foreach (var target in Objects) {
                if (flag.GetBoxed(target) is bool value && value != Member.ConditionNegated) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The targets whose condition is satisfied, which are the ones an edit reaches.</summary>
    /// <remarks>
    ///     ⚠ <b>The condition decides who the edit reaches, not merely whether the row is drawn.</b> A
    ///     row is shown when any object would show it, and writing it to the ones whose flag is off
    ///     would be editing a member the user cannot see the state of. This is what
    ///     <see cref="EditProperty.Reached" /> exists for.
    /// </remarks>
    public IReadOnlyList<object> WritableTargets {
        get {
            if (Member.Condition is not { } condition || !Descriptor.TryGetMember(condition, out var flag)) {
                return Objects;
            }

            List<object> writable = [];

            foreach (var target in Objects) {
                if (flag.GetBoxed(target) is bool value && value != Member.ConditionNegated) {
                    writable.Add(target);
                }
            }

            return writable;
        }
    }
}

/// <summary>Where an inspected object came from, for override indication and revert.</summary>
/// <remarks>
///     <para>
///         An interface rather than something this assembly implements, because what a prefab
///         <i>is</i> belongs to the scene document — this assembly only needs four questions answered
///         about it, and asking them through a contract is what keeps the inspector usable in a test
///         with no project on disk.
///     </para>
///     <para>
///         ⚠⚠ <b>"Overridden" is a claim the instance records, not a value comparison</b>, and the
///         two halves below are what make that expressible from a panel.
///         <see href="../../docs/plan/47-prefab-overrides-and-nested-prefabs.md">Doc 47</see> § 3
///         rejected the comparison outright: an author who turns a lamp's intensity down to <c>0</c>
///         — or up to exactly the value the template already had — has said something a comparison
///         cannot see, so the row would stop being marked and the revert button would grey out on the
///         one edit the author most wants to take back. An implementation that answers
///         <see cref="IsOverridden" /> by comparing values is that defect, reintroduced at the layer
///         nobody tests.
///     </para>
///     <para>
///         <see cref="Claim" /> and <see cref="Release" /> both default to doing nothing, so a source
///         that only <i>displays</i> — a stub in a test, a read-only view of somebody else's document
///         — implements two methods as it always did. The default is "no", for
///         <see cref="IEditorCommand.TryMergeWith" />'s reason: recording an authoring decision is a
///         claim about a file, and a source that has not thought about it should not be making one.
///     </para>
/// </remarks>
public interface IPrefabSource {
    /// <summary>Whether an object's member is the instance's own rather than the prefab's.</summary>
    /// <param name="target">The object.</param>
    /// <param name="member">The member.</param>
    /// <returns>Whether it is an override.</returns>
    /// <remarks>
    ///     ⚠ <b>Answered from what the instance claims, never from what it holds.</b> See the type's
    ///     own remarks: a value comparison is a different — and rejected — model that happens to agree
    ///     most of the time, which is what makes it hard to notice.
    /// </remarks>
    bool IsOverridden(object target, InspectorMember member);

    /// <summary>The value the prefab has for a member, in the space the object reads it in.</summary>
    /// <param name="target">The instance.</param>
    /// <param name="member">The member.</param>
    /// <param name="value">The prefab's value.</param>
    /// <returns>Whether the object came from a prefab that has this member.</returns>
    /// <remarks>
    ///     ⚠ <b>In the object's space, which is the implementation's problem and not the caller's.</b>
    ///     <see cref="RevertToPrefab" /> feeds this straight back into the member's setter, so a
    ///     source that handed back a parent-relative position for a world-space property would move
    ///     the entity somewhere nobody asked for — see <c>PrefabSource</c>.
    /// </remarks>
    bool TryGetPrefabValue(object target, InspectorMember member, out object? value);

    /// <summary>Records that a member has just been given a value of the instance's own.</summary>
    /// <param name="target">The object that was written.</param>
    /// <param name="member">The member that was written.</param>
    /// <returns>Whether this was a claim the instance had not already made.</returns>
    /// <remarks>
    ///     Called by <see cref="InspectorField" /> after a write lands, which is the only moment at
    ///     which "the author chose this value" is a fact rather than a guess. A source that does not
    ///     record anything says so by returning <see langword="false" />.
    /// </remarks>
    bool Claim(object target, InspectorMember member) => false;

    /// <summary>Gives a member back to the template, which is the half of a revert a value cannot say.</summary>
    /// <param name="target">The object.</param>
    /// <param name="member">The member.</param>
    /// <returns>Whether the instance had been claiming it.</returns>
    /// <remarks>
    ///     ⚠⚠ <b>This is what makes reverting an override <i>to the template's own value</i> do
    ///     anything at all.</b> Writing the prefab's value over a value already equal to it changes
    ///     nothing, so a revert that consisted only of the write would leave the row marked, the file
    ///     still claiming the member, and the next template change still blocked — a button that
    ///     looks like it worked and did not.
    /// </remarks>
    bool Release(object target, InspectorMember member) => false;
}
