// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using JoltPhysicsSharp;
using Vixen.Core;

namespace Vixen.Physics;

/// <summary>Which layer a body is on. Layers decide what may collide with what.</summary>
/// <param name="Index">The layer number, from 0 to <see cref="PhysicsLayers.MaxLayers" /> − 1.</param>
[DataContract]
public readonly record struct PhysicsLayer(byte Index) {
    /// <summary>The layer everything is on until it is put somewhere else.</summary>
    public static PhysicsLayer Default => new(0);

    /// <summary>This layer on its own, as a mask.</summary>
    public PhysicsLayerMask AsMask => new(1u << Index);

    /// <summary>Renders the layer number.</summary>
    /// <returns>The layer in text.</returns>
    public override string ToString() => $"layer {Index}";
}

/// <summary>A set of layers, one bit each.</summary>
/// <param name="Bits">The set.</param>
/// <remarks>
///     What a query filters with. Thirty-two layers is the same budget Unity and Unreal settled on,
///     and it is what lets a mask be one word, which is what lets a filter test be one <c>and</c>.
/// </remarks>
[DataContract]
public readonly record struct PhysicsLayerMask(uint Bits) {
    /// <summary>No layers. A query with this hits nothing.</summary>
    public static PhysicsLayerMask None => new(0u);

    /// <summary>Every layer.</summary>
    public static PhysicsLayerMask All => new(uint.MaxValue);

    /// <summary>Whether a layer is in the set.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns><see langword="true" /> if it is.</returns>
    public bool Contains(PhysicsLayer layer) => (Bits & (1u << layer.Index)) != 0;

    /// <summary>The set with a layer added.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The larger set.</returns>
    public PhysicsLayerMask With(PhysicsLayer layer) => new(Bits | (1u << layer.Index));

    /// <summary>The set with a layer taken out.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The smaller set.</returns>
    public PhysicsLayerMask Without(PhysicsLayer layer) => new(Bits & ~(1u << layer.Index));

    /// <summary>The union of two sets.</summary>
    /// <param name="left">One set.</param>
    /// <param name="right">The other.</param>
    /// <returns>Everything in either.</returns>
    public static PhysicsLayerMask operator |(PhysicsLayerMask left, PhysicsLayerMask right) =>
        new(left.Bits | right.Bits);

    /// <summary>The intersection of two sets.</summary>
    /// <param name="left">One set.</param>
    /// <param name="right">The other.</param>
    /// <returns>Everything in both.</returns>
    public static PhysicsLayerMask operator &(PhysicsLayerMask left, PhysicsLayerMask right) =>
        new(left.Bits & right.Bits);

    /// <summary>The complement of a set.</summary>
    /// <param name="value">The set.</param>
    /// <returns>Everything not in it.</returns>
    public static PhysicsLayerMask operator ~(PhysicsLayerMask value) => new(~value.Bits);

    /// <inheritdoc cref="op_BitwiseOr" />
    /// <param name="left">One set.</param>
    /// <param name="right">The other.</param>
    /// <returns>Everything in either.</returns>
    public static PhysicsLayerMask Union(PhysicsLayerMask left, PhysicsLayerMask right) => left | right;

    /// <inheritdoc cref="op_BitwiseAnd" />
    /// <param name="left">One set.</param>
    /// <param name="right">The other.</param>
    /// <returns>Everything in both.</returns>
    public static PhysicsLayerMask Intersect(PhysicsLayerMask left, PhysicsLayerMask right) => left & right;

    /// <inheritdoc cref="op_OnesComplement" />
    /// <param name="value">The set.</param>
    /// <returns>Everything not in it.</returns>
    public static PhysicsLayerMask Complement(PhysicsLayerMask value) => ~value;

    /// <summary>Renders the set as a hexadecimal mask.</summary>
    /// <returns>The set in text.</returns>
    public override string ToString() => $"0x{Bits:x8}";
}

/// <summary>
///     Whether bodies on a layer move. The broad phase is split on this, and nothing else.
/// </summary>
/// <remarks>
///     Jolt keeps one bounding-volume tree per broad-phase layer and never tests a pair drawn from
///     the same static tree, so putting the level geometry on a <see cref="Static" /> layer is what
///     stops a hundred thousand immobile triangles from being considered against each other every
///     step. It is a performance classification and not a collision one: what collides with what is
///     the layer matrix below.
/// </remarks>
public enum PhysicsBroadPhase {
    /// <summary>Bodies that do not move. Level geometry, triggers bolted to a wall.</summary>
    Static,

    /// <summary>Bodies that do. Anything dynamic or kinematic.</summary>
    Moving
}

/// <summary>
///     The layer table: what each layer is called, whether it moves, and which layers it collides
///     with.
/// </summary>
/// <remarks>
///     <para>
///         Built once and handed to a <see cref="PhysicsWorld" />, which turns it into the three
///         filter objects Jolt actually wants — an object-layer pair table, a broad-phase layer
///         table, and the mapping between them. Those are native objects with a lifetime tied to the
///         physics system, which is why this type is a description and not the filters themselves:
///         the same table can configure any number of worlds.
///     </para>
///     <para>
///         <b>The matrix is symmetric and kept that way.</b> Jolt asks "may layer A collide with
///         layer B" in whichever order the broad phase produced the pair, so a matrix that said yes
///         one way and no the other would give a collision that depends on body creation order. Every
///         mutator writes both halves.
///     </para>
/// </remarks>
public sealed class PhysicsLayers {
    /// <summary>The most layers there can be, fixed by <see cref="PhysicsLayerMask" /> being one word.</summary>
    public const int MaxLayers = 32;

    readonly string[] names;
    readonly PhysicsBroadPhase[] broadPhases;
    readonly uint[] matrix;

    /// <summary>How many layers the table declares.</summary>
    public int Count { get; }

    /// <summary>
    ///     The table a world gets when it is not given one: layer 0 <c>Static</c>, layer 1
    ///     <c>Moving</c>, and everything collides with everything.
    /// </summary>
    /// <remarks>
    ///     Two layers rather than one, because a world with a single broad-phase layer puts the level
    ///     geometry in the same tree as the crates and pays for it on every step. The smallest table
    ///     that is not a performance trap is this one.
    /// </remarks>
    public static PhysicsLayers Default { get; } = CreateDefault();

    PhysicsLayers(string[] names, PhysicsBroadPhase[] broadPhases, uint[] matrix) {
        this.names = names;
        this.broadPhases = broadPhases;
        this.matrix = matrix;
        Count = names.Length;
    }

    /// <summary>Starts building a table.</summary>
    /// <returns>The builder.</returns>
    public static Builder Define() => new();

    /// <summary>What a layer is called.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>Its name.</returns>
    public string NameOf(PhysicsLayer layer) => names[Check(layer)];

    /// <summary>Whether a layer's bodies move.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>Its broad-phase class.</returns>
    public PhysicsBroadPhase BroadPhaseOf(PhysicsLayer layer) => broadPhases[Check(layer)];

    /// <summary>Everything a layer collides with.</summary>
    /// <param name="layer">The layer.</param>
    /// <returns>The mask.</returns>
    public PhysicsLayerMask CollidesWith(PhysicsLayer layer) => new(matrix[Check(layer)]);

    /// <summary>Whether two layers collide.</summary>
    /// <param name="first">One layer.</param>
    /// <param name="second">The other.</param>
    /// <returns><see langword="true" /> if a pair drawn from them is considered.</returns>
    public bool Collide(PhysicsLayer first, PhysicsLayer second) =>
        (matrix[Check(first)] & (1u << Check(second))) != 0;

    /// <summary>The layer with a name, if there is one.</summary>
    /// <param name="name">The name, compared ordinally.</param>
    /// <param name="layer">The layer.</param>
    /// <returns><see langword="true" /> if the name is declared.</returns>
    public bool TryFind(string name, out PhysicsLayer layer) {
        for (var index = 0; index < names.Length; index++) {
            if (string.Equals(names[index], name, StringComparison.Ordinal)) {
                layer = new((byte)index);
                return true;
            }
        }

        layer = default;
        return false;
    }

    /// <summary>Turns the table into the filter objects a Jolt physics system is constructed with.</summary>
    /// <returns>The filters, which the caller owns and must dispose after the physics system.</returns>
    /// <remarks>
    ///     <para>
    ///         Two broad-phase layers always, because <see cref="PhysicsBroadPhase" /> has two values.
    ///         Object layers are one per declared layer.
    ///     </para>
    ///     <para>
    ///         Order matters on the way out as much as on the way in: <c>ObjectVsBroadPhaseLayerFilterTable</c>
    ///         reads the other two at construction, so they are built first and disposed last.
    ///     </para>
    /// </remarks>
    internal JoltLayerFilters CreateFilters() {
        var objectPairs = new ObjectLayerPairFilterTable((uint)Count);

        for (var first = 0; first < Count; first++) {
            for (var second = first; second < Count; second++) {
                if ((matrix[first] & (1u << second)) != 0) {
                    objectPairs.EnableCollision(new((uint)first), new((uint)second));
                }
            }
        }

        var broadPhase = new BroadPhaseLayerInterfaceTable((uint)Count, JoltLayerFilters.BroadPhaseLayerCount);

        for (var index = 0; index < Count; index++) {
            broadPhase.MapObjectToBroadPhaseLayer(new((uint)index), new((byte)broadPhases[index]));
        }

        var objectVsBroadPhase = new ObjectVsBroadPhaseLayerFilterTable(
            broadPhase,
            JoltLayerFilters.BroadPhaseLayerCount,
            objectPairs,
            (uint)Count
        );

        return new(objectPairs, broadPhase, objectVsBroadPhase);
    }

    int Check(PhysicsLayer layer) =>
        layer.Index < Count
            ? layer.Index
            : throw new ArgumentOutOfRangeException(
                nameof(layer),
                layer.Index,
                $"The table declares {Count} layers."
            );

    static PhysicsLayers CreateDefault() =>
        Define()
            .Add("Static", PhysicsBroadPhase.Static)
            .Add("Moving", PhysicsBroadPhase.Moving)
            .Build();

    /// <summary>Collects layers and the collision matrix, then freezes them.</summary>
    /// <remarks>
    ///     Everything collides with everything until told otherwise, which is the default a person
    ///     expects and the one that fails visibly — a matrix that started empty would produce a world
    ///     in which nothing touches anything and no error anywhere.
    /// </remarks>
    public sealed class Builder {
        readonly List<string> names = [];
        readonly List<PhysicsBroadPhase> broadPhases = [];
        readonly List<uint> matrix = [];

        /// <summary>Adds a layer.</summary>
        /// <param name="name">What it is called.</param>
        /// <param name="broadPhase">Whether its bodies move.</param>
        /// <returns>This builder, for chaining.</returns>
        public Builder Add(string name, PhysicsBroadPhase broadPhase = PhysicsBroadPhase.Moving) {
            ArgumentException.ThrowIfNullOrEmpty(name);

            if (names.Count == MaxLayers) {
                throw new InvalidOperationException($"A layer table holds at most {MaxLayers} layers.");
            }

            if (names.Contains(name, StringComparer.Ordinal)) {
                throw new ArgumentException($"Layer '{name}' is already declared.", nameof(name));
            }

            var index = names.Count;
            names.Add(name);
            broadPhases.Add(broadPhase);
            matrix.Add(0u);

            // Collides with everything declared so far, and everything so far collides with it.
            for (var other = 0; other < index; other++) {
                matrix[other] |= 1u << index;
                matrix[index] |= 1u << other;
            }

            matrix[index] |= 1u << index;
            return this;
        }

        /// <summary>Stops two layers from colliding, in both directions.</summary>
        /// <param name="first">One layer.</param>
        /// <param name="second">The other.</param>
        /// <returns>This builder, for chaining.</returns>
        public Builder Separate(string first, string second) {
            var left = IndexOf(first);
            var right = IndexOf(second);

            matrix[left] &= ~(1u << right);
            matrix[right] &= ~(1u << left);
            return this;
        }

        /// <summary>Lets two layers collide again, in both directions.</summary>
        /// <param name="first">One layer.</param>
        /// <param name="second">The other.</param>
        /// <returns>This builder, for chaining.</returns>
        public Builder Join(string first, string second) {
            var left = IndexOf(first);
            var right = IndexOf(second);

            matrix[left] |= 1u << right;
            matrix[right] |= 1u << left;
            return this;
        }

        /// <summary>Freezes the table.</summary>
        /// <returns>The table.</returns>
        public PhysicsLayers Build() {
            if (names.Count == 0) {
                throw new InvalidOperationException("A layer table needs at least one layer.");
            }

            return new([.. names], [.. broadPhases], [.. matrix]);
        }

        [SuppressMessage(
            "Globalization",
            "CA1307:Specify StringComparison for clarity",
            Justification = "IndexOf(string) on List<string> is ordinal; the comparer overload does not exist."
        )]
        int IndexOf(string name) {
            var index = names.IndexOf(name);

            return index >= 0
                ? index
                : throw new ArgumentException($"No layer is called '{name}'.", nameof(name));
        }
    }
}

/// <summary>The three native filter objects a Jolt physics system needs, kept together so they die together.</summary>
/// <param name="ObjectPairs">Which object layers collide.</param>
/// <param name="BroadPhase">Which broad-phase layer each object layer lives in.</param>
/// <param name="ObjectVsBroadPhase">Which broad-phase layers an object layer must be tested against.</param>
sealed record JoltLayerFilters(
    ObjectLayerPairFilterTable ObjectPairs,
    BroadPhaseLayerInterfaceTable BroadPhase,
    ObjectVsBroadPhaseLayerFilterTable ObjectVsBroadPhase
) : IDisposable {
    /// <summary>How many broad-phase layers there are — one per <see cref="PhysicsBroadPhase" />.</summary>
    public const uint BroadPhaseLayerCount = 2;

    /// <inheritdoc />
    /// <remarks>
    ///     Reverse construction order. <c>ObjectVsBroadPhaseLayerFilterTable</c> holds the other two,
    ///     and freeing them first leaves it pointing at released memory for as long as the physics
    ///     system that owns it is still shutting down.
    /// </remarks>
    public void Dispose() {
        ObjectVsBroadPhase.Dispose();
        BroadPhase.Dispose();
        ObjectPairs.Dispose();
    }
}
