// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Inspector;

/// <summary>The member types the inspector edits in place, and therefore has to compare and copy itself.</summary>
/// <remarks>
///     <para>
///         <b>A reference-typed member is one of two things and the inspector cannot tell them apart
///         from the type alone.</b> A <i>reference</i> — a material, an asset id's target, another
///         object — is shared on purpose: pasting one into twenty objects so that all twenty point at
///         the same thing is the whole point of the row. An <i>owned</i> value —
///         <see cref="AnimationCurve" />, <see cref="Gradient" /> — is a model the row edits in place,
///         and twenty objects sharing one instance is not "they all have the same curve" but "editing
///         any of them edits all of them", silently, for the rest of the session.
///     </para>
///     <para>
///         ⚠ <b>Registering here is the declaration that a type is owned</b>, and both halves follow
///         from it: values of an owned type compare structurally (so a member initialised
///         <c>= new AnimationCurve()</c> does not read as mixed the moment a second object is
///         selected) and every object gets its own copy on a write (so a paste is an agreement rather
///         than an alias). Anything unregistered keeps <c>Equals</c> and keeps sharing the instance,
///         which is the right answer for a reference and for every value type and string.
///     </para>
///     <para>
///         ⚠ <b>Not <c>Equals</c>/<c>GetHashCode</c> on the types themselves, deliberately.</b> Value
///         equality obliges a matching hash code, and these are mutable models that raise
///         <c>Changed</c> and whose parts sit in a <c>HashSet</c> inside their editor's selection — a
///         hash that moved when a key was dragged would take the dragged key out of the set tracking
///         it. Whether two curves count as the same value is an editing question, so it is answered
///         where the editing is.
///     </para>
///     <para>
///         <b>Static because <see cref="Core.EditProperty" /> is constructed everywhere</b> — by the
///         inspector, by a graph editor, by a plugin's panel, by a test — and threading a registry
///         through every one of those construction sites to answer a question about a <i>type</i>
///         would put the burden on every caller to know this exists. The single-threaded contract the
///         rest of the editor runs under makes registration a load-time act; the map is concurrent
///         anyway because reads outnumber writes by every frame the inspector draws.
///     </para>
///     <para>
///         ⚠ <b>Internal, and that is a decision rather than an oversight.</b> A plugin with its own
///         mutable model type is the obvious caller for <see cref="Register{T}" /> and there is not
///         one — publishing the seam now would add a public type nothing outside this assembly
///         reaches, which is this repository's commonest defect. The day a plugin needs it, it goes
///         public with a guide page in the same commit.
///     </para>
/// </remarks>
static class OwnedValues {
    static readonly ConcurrentDictionary<Type, Semantics> Registered = new();

    static OwnedValues() {
        Register<AnimationCurve>(SameKeys, CopyCurve);
        Register<Gradient>(SameStops, CopyGradient);
    }

    /// <summary>Declares that a type is edited in place, and says how its values compare and copy.</summary>
    /// <typeparam name="T">The owned type.</typeparam>
    /// <param name="areEqual">Whether two values are the same value. Both may be <see langword="null" />.</param>
    /// <param name="copy">An independent copy of a value.</param>
    /// <remarks>
    ///     What a plugin calls for its own mutable model type. Registering twice replaces, so a host
    ///     can override a built-in's comparison without the built-in knowing.
    /// </remarks>
    internal static void Register<T>(Func<T?, T?, bool> areEqual, Func<T, T> copy) where T : class {
        ArgumentNullException.ThrowIfNull(areEqual);
        ArgumentNullException.ThrowIfNull(copy);

        Registered[typeof(T)] = new(
            (left, right) => areEqual(left as T, right as T),
            value => value is T typed ? copy(typed) : value
        );
    }

    /// <summary>Whether a type is edited in place rather than referred to.</summary>
    /// <param name="type">The member's type.</param>
    /// <returns>Whether it is registered.</returns>
    internal static bool IsOwned(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        return Registered.ContainsKey(type);
    }

    /// <summary>Whether two values of a member type count as the same value.</summary>
    /// <param name="type">The member's type.</param>
    /// <param name="left">One value, boxed.</param>
    /// <param name="right">The other, boxed.</param>
    /// <returns>Whether writing one over the other would change anything.</returns>
    internal static bool AreEqual(Type type, object? left, object? right) {
        ArgumentNullException.ThrowIfNull(type);

        return Registered.TryGetValue(type, out var semantics)
            ? semantics.AreEqual(left, right)
            : Equals(left, right);
    }

    /// <summary>An independent copy of a value, for a write that must not alias.</summary>
    /// <param name="type">The member's type.</param>
    /// <param name="value">The value, boxed.</param>
    /// <returns>A copy for an owned type; the value itself for anything else.</returns>
    internal static object? Copy(Type type, object? value) {
        ArgumentNullException.ThrowIfNull(type);

        return value is not null && Registered.TryGetValue(type, out var semantics)
            ? semantics.Copy(value)
            : value;
    }

    /// <summary>Whether two curves have the same keys, which is what "the same curve" means here.</summary>
    static bool SameKeys(AnimationCurve? left, AnimationCurve? right) {
        if (ReferenceEquals(left, right)) {
            return true;
        }

        if (left is null || right is null || left.Keys.Count != right.Keys.Count) {
            return false;
        }

        for (var index = 0; index < left.Keys.Count; index++) {
            var a = left.Keys[index];
            var b = right.Keys[index];

            if (!a.Time.Equals(b.Time)
                || !a.Value.Equals(b.Value)
                || !a.InTangent.Equals(b.InTangent)
                || !a.OutTangent.Equals(b.OutTangent)
                || a.Mode != b.Mode) {
                return false;
            }
        }

        return true;
    }

    static AnimationCurve CopyCurve(AnimationCurve source) {
        var keys = new CurveKey[source.Keys.Count];

        for (var index = 0; index < keys.Length; index++) {
            var key = source.Keys[index];

            keys[index] = new(key.Time, key.Value, key.Mode) {
                InTangent = key.InTangent,
                OutTangent = key.OutTangent
            };
        }

        return new(keys);
    }

    /// <summary>Whether two gradients have the same stops and the same interpolation.</summary>
    static bool SameStops(Gradient? left, Gradient? right) {
        if (ReferenceEquals(left, right)) {
            return true;
        }

        if (left is null
            || right is null
            || left.Interpolation != right.Interpolation
            || left.ColorStops.Count != right.ColorStops.Count
            || left.AlphaStops.Count != right.AlphaStops.Count) {
            return false;
        }

        for (var index = 0; index < left.ColorStops.Count; index++) {
            var a = left.ColorStops[index];
            var b = right.ColorStops[index];

            if (!a.Position.Equals(b.Position) || !a.Color.Equals(b.Color)) {
                return false;
            }
        }

        for (var index = 0; index < left.AlphaStops.Count; index++) {
            var a = left.AlphaStops[index];
            var b = right.AlphaStops[index];

            if (!a.Position.Equals(b.Position) || !a.Alpha.Equals(b.Alpha)) {
                return false;
            }
        }

        return true;
    }

    static Gradient CopyGradient(Gradient source) {
        // ⚠ The parameterless constructor is the *empty* gradient, not the white-to-white one — so
        // there is nothing to clear first, which matters because `Remove` refuses to take the last
        // stop and a clear-then-fill loop would never terminate on a one-stop gradient.
        var copy = new Gradient { Interpolation = source.Interpolation };

        foreach (var stop in source.ColorStops) {
            copy.AddColorStop(stop.Position, stop.Color);
        }

        foreach (var stop in source.AlphaStops) {
            copy.AddAlphaStop(stop.Position, stop.Alpha);
        }

        return copy;
    }

    readonly record struct Semantics(Func<object?, object?, bool> AreEqual, Func<object, object?> Copy);
}
