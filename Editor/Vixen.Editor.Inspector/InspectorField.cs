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

        foreach (var target in Objects) {
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
