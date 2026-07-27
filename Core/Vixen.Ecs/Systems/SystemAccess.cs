// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Core.Collections;

namespace Vixen.Ecs.Systems;

/// <summary>
///     The component types a system reads and the ones it writes — everything the runner needs to
///     decide whether two systems may run at the same time.
/// </summary>
/// <remarks>
///     Two systems conflict when one writes something the other touches. Read against read is not a
///     conflict, which is what lets a dozen systems that all read <c>WorldTransform</c> run
///     concurrently; write against anything is.
/// </remarks>
public sealed class SystemAccess {
    /// <summary>Access to nothing, which conflicts with nothing.</summary>
    public static SystemAccess None { get; } = new([], []);

    readonly BitSet reads = new(64);
    readonly BitSet writes = new(64);

    /// <summary>The component types read, including the ones written.</summary>
    public IReadOnlyList<ComponentTypeId> Reads { get; }

    /// <summary>The component types written.</summary>
    public IReadOnlyList<ComponentTypeId> Writes { get; }

    /// <summary>Whether this declares nothing, and so conflicts with nothing.</summary>
    public bool IsEmpty => Reads.Count == 0 && Writes.Count == 0;

    /// <summary>Builds an access set.</summary>
    /// <param name="reads">What is read.</param>
    /// <param name="writes">What is written.</param>
    public SystemAccess(IEnumerable<ComponentTypeId> reads, IEnumerable<ComponentTypeId> writes) {
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(writes);

        var written = writes.Distinct().Order().ToArray();

        // A write implies a read. Otherwise a system that only writes X and one that only reads X
        // would look disjoint, which is the one combination that is definitely a race.
        var read = reads.Concat(written).Distinct().Order().ToArray();

        Writes = written;
        Reads = read;

        foreach (var id in read) {
            this.reads.Set(id.Value);
        }

        foreach (var id in written) {
            this.writes.Set(id.Value);
        }
    }

    /// <summary>Whether two systems may not run at the same time.</summary>
    /// <param name="other">The other system's access.</param>
    /// <returns>Whether one of them writes something the other touches.</returns>
    public bool ConflictsWith(SystemAccess other) {
        ArgumentNullException.ThrowIfNull(other);

        // An undeclared system conflicts with everything. That is the only safe reading of "I did
        // not say": the alternative is to assume it touches nothing, and be wrong silently.
        if (IsEmpty || other.IsEmpty) {
            return true;
        }

        return writes.Intersects(other.reads) || other.writes.Intersects(reads);
    }

    /// <summary>Reads the access a system type declares with attributes.</summary>
    /// <param name="systemType">The concrete system type.</param>
    /// <returns>What it declared, or <see cref="None" /> if it declared nothing.</returns>
    /// <remarks>
    ///     Attribute metadata on a type the program already references survives trimming and
    ///     NativeAOT — unlike an assembly scan, which reads metadata the publisher has deleted. So
    ///     this stays reflection while the generator that will emit the declarations directly is
    ///     still owed.
    /// </remarks>
    public static SystemAccess FromAttributes(Type systemType) {
        ArgumentNullException.ThrowIfNull(systemType);

        var reads = new List<ComponentTypeId>();
        var writes = new List<ComponentTypeId>();

        foreach (var attribute in systemType.GetCustomAttributes<ReadsAttribute>(inherit: true)) {
            Collect(reads, attribute.ComponentTypes);
        }

        foreach (var attribute in systemType.GetCustomAttributes<WritesAttribute>(inherit: true)) {
            Collect(writes, attribute.ComponentTypes);
        }

        return reads.Count == 0 && writes.Count == 0 ? None : new(reads, writes);
    }

    /// <summary>Declares access by naming component types, which registers them as it goes.</summary>
    /// <returns>A builder.</returns>
    /// <remarks>
    ///     The path to prefer where the set is known in code, because <c>Read&lt;Position&gt;()</c>
    ///     closes the generic and so assigns <c>Position</c> its id on the spot — where
    ///     <see cref="FromAttributes" /> can only look one up, and has nothing to look up until
    ///     something has stored one.
    /// </remarks>
    public static Builder Declare() => new();

    /// <summary>Collects reads and writes by type.</summary>
    public sealed class Builder {
        readonly List<ComponentTypeId> reads = [];
        readonly List<ComponentTypeId> writes = [];

        /// <summary>Declares a read.</summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <returns>This builder, for chaining.</returns>
        public Builder Read<T>() {
            reads.Add(ComponentType<T>.Id);
            return this;
        }

        /// <summary>Declares a write, which implies a read.</summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <returns>This builder, for chaining.</returns>
        public Builder Write<T>() {
            writes.Add(ComponentType<T>.Id);
            return this;
        }

        /// <summary>Finishes the declaration.</summary>
        /// <returns>The access set.</returns>
        public SystemAccess Build() => reads.Count == 0 && writes.Count == 0 ? None : new(reads, writes);
    }

    static void Collect(List<ComponentTypeId> into, IReadOnlyList<Type> componentTypes) {
        foreach (var componentType in componentTypes) {
            if (!ComponentRegistry.TryGet(componentType, out var component)) {
                throw new InvalidOperationException(
                    $"{componentType} is declared in a system's access but has no component id yet: "
                    + "nothing in this process has stored one. An attribute can only look an id up, "
                    + $"so either store one first or declare with {nameof(IDeclaredAccess)} and "
                    + $"{nameof(SystemAccess)}.{nameof(Declare)}(), which assigns the id as it names "
                    + "the type."
                );
            }

            into.Add(component!.Id);
        }
    }

    /// <summary>Renders the two sets, which is what a system-graph dump shows.</summary>
    /// <returns>The access in text.</returns>
    public override string ToString() {
        if (IsEmpty) {
            return "undeclared";
        }

        var readOnly = Reads.Except(Writes).Select(id => ComponentRegistry.Get(id).Type.Name).ToArray();
        var written = Writes.Select(id => ComponentRegistry.Get(id).Type.Name).ToArray();
        var parts = new List<string>();

        if (readOnly.Length > 0) {
            parts.Add($"reads({string.Join(", ", readOnly)})");
        }

        if (written.Length > 0) {
            parts.Add($"writes({string.Join(", ", written)})");
        }

        return string.Join(" ", parts);
    }
}
