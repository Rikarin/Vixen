// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>The seven primitives a body is described with.</summary>
/// <remarks>
///     ⚠ <b><c>Y</c> is every shape's own long axis</b>, and everything in this file assumes it. A
///     capsule runs up Y, a cone's apex is at <c>+Y</c>, a box's height is <c>Y</c>. A limb that runs
///     along X in the rig is oriented by its <see cref="ProxyShape.Offset" />, which is one rotation
///     stored once against a convention that never has to be looked up again.
/// </remarks>
public enum ShapeKind : byte {
    /// <summary>Six flat faces.</summary>
    Box,

    /// <summary>A box whose cross-section changes with height. A forearm, a thigh.</summary>
    TaperedBox,

    /// <summary>A ball. A head, a shoulder, a belly.</summary>
    Sphere,

    /// <summary>A cylinder with hemispherical ends. The workhorse for a limb.</summary>
    Capsule,

    /// <summary>A capsule whose radius changes with height.</summary>
    TaperedCapsule,

    /// <summary>Flat-ended. A torso section, a prop.</summary>
    Cylinder,

    /// <summary>A base disc and an apex.</summary>
    Cone
}

/// <summary>How big a shape is.</summary>
/// <param name="Extents">
///     Half-extents at the base. For a round kind, <c>X</c> and <c>Z</c> are the radius and
///     <c>Y</c> is the half-height.
/// </param>
/// <param name="TopExtents">
///     Half-extents at the top, for the tapered kinds. The same as <paramref name="Extents" /> for
///     the rest.
/// </param>
/// <remarks>
///     <para>
///         One shape, two vectors, whatever the kind — which is not tidiness. <b>Resizing a shape for
///         a different body is multiplying these</b>, and the whole of why proxy shapes exist is that
///         a coordinate on a shape means the same place on a body of any size. A parameterisation
///         with a different field set per kind would need a resize path per kind, and one of them
///         would be wrong.
///     </para>
/// </remarks>
public readonly record struct ShapeParams(Vector3 Extents, Vector3 TopExtents) {
    /// <summary>The radius at the base.</summary>
    public float Radius => Extents.X;

    /// <summary>The radius at the top.</summary>
    public float TopRadius => TopExtents.X;

    /// <summary>Half the height, along the shape's own <c>Y</c>.</summary>
    public float HalfHeight => Extents.Y;

    /// <summary>A box.</summary>
    /// <param name="halfExtents">Half its size on each axis.</param>
    /// <returns>The dimensions.</returns>
    public static ShapeParams Box(Vector3 halfExtents) => new(halfExtents, halfExtents);

    /// <summary>A box whose cross-section changes with height.</summary>
    /// <param name="baseExtents">Half its size at the bottom.</param>
    /// <param name="topExtents">Half its size at the top. <c>Y</c> is ignored.</param>
    /// <returns>The dimensions.</returns>
    public static ShapeParams TaperedBox(Vector3 baseExtents, Vector3 topExtents) =>
        new(baseExtents, new Vector3(topExtents.X, baseExtents.Y, topExtents.Z));

    /// <summary>A ball.</summary>
    /// <param name="radius">Its radius.</param>
    /// <returns>The dimensions.</returns>
    public static ShapeParams Sphere(float radius) => Box(new Vector3(radius));

    /// <summary>A capsule.</summary>
    /// <param name="radius">Its radius.</param>
    /// <param name="halfHeight">Half the length of the straight part, not counting the caps.</param>
    /// <returns>The dimensions.</returns>
    public static ShapeParams Capsule(float radius, float halfHeight) =>
        Box(new Vector3(radius, halfHeight, radius));

    /// <summary>A capsule whose radius changes with height.</summary>
    /// <param name="radius">Its radius at the bottom.</param>
    /// <param name="topRadius">Its radius at the top.</param>
    /// <param name="halfHeight">Half the length of the straight part.</param>
    /// <returns>The dimensions.</returns>
    public static ShapeParams TaperedCapsule(float radius, float topRadius, float halfHeight) =>
        new(new(radius, halfHeight, radius), new(topRadius, halfHeight, topRadius));

    /// <summary>A cylinder.</summary>
    /// <param name="radius">Its radius.</param>
    /// <param name="halfHeight">Half its height.</param>
    /// <returns>The dimensions.</returns>
    public static ShapeParams Cylinder(float radius, float halfHeight) => Capsule(radius, halfHeight);

    /// <summary>A cone, apex up.</summary>
    /// <param name="radius">The radius of its base.</param>
    /// <param name="halfHeight">Half its height.</param>
    /// <returns>The dimensions.</returns>
    public static ShapeParams Cone(float radius, float halfHeight) =>
        new(new(radius, halfHeight, radius), new(0f, halfHeight, 0f));

    /// <summary>The same shape, resized.</summary>
    /// <param name="scale">What to multiply it by, per axis.</param>
    /// <returns>The dimensions.</returns>
    public ShapeParams Scaled(Vector3 scale) => new(Extents * scale, TopExtents * scale);
}

/// <summary>A primitive attached to a joint, with a name and what it affords.</summary>
/// <remarks>
///     <para>
///         <b>Not physics colliders, for three reasons that are all load-bearing.</b> <b>Fidelity</b>
///         — a physics body wants the cheapest shape that stops interpenetration; a contact wants the
///         shape that describes the surface a hand lands on, which is usually finer. A forearm is one
///         capsule to physics and three to a rolled-up sleeve. <b>Cost</b> — a character may carry a
///         hundred of these and posing all of them to serve two goals is waste, so
///         <see cref="ProxyShapes" /> poses only the ones an active goal names. <b>Coupling</b> —
///         <c>Vixen.Animation</c> does not reference <c>Vixen.Physics</c> and this is exactly where
///         that would break.
///     </para>
///     <para>
///         <b>Tags rather than a type hierarchy.</b> A shape is described by what it affords —
///         <c>grip-surface</c>, <c>seat</c>, <c>mountable</c> — and a constraint may name a shape by
///         tag rather than by name, which is what makes one authored sitting clip work against a
///         chair, a bench and a crate.
///     </para>
/// </remarks>
public sealed class ProxyShape {
    /// <summary>What it is called — <c>belly</c>, <c>left-palm</c>, <c>seat</c>.</summary>
    public required Symbol Name { get; init; }

    /// <summary>Which primitive.</summary>
    public required ShapeKind Kind { get; init; }

    /// <summary>Which joint it hangs off.</summary>
    public required int Joint { get; init; }

    /// <summary>Where it sits relative to that joint.</summary>
    public BoneTransform Offset { get; init; } = BoneTransform.Identity;

    /// <summary>How big it is.</summary>
    public ShapeParams Dimensions { get; init; }

    /// <summary>What it affords.</summary>
    public FacetSet Tags { get; init; } = FacetSet.Empty;

    /// <summary>Whether it survives into the coarse set.</summary>
    public bool Coarse { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Kind} on {Joint})";
}

/// <summary>Every proxy shape one body carries.</summary>
/// <remarks>
///     Authored against a skeleton and referenced the way a material is. A set is shared by every
///     character wearing that body, so nothing per-character is stored here — where the shapes
///     currently <em>are</em> belongs to <see cref="ProxyShapes" />, which is per-animator.
/// </remarks>
public sealed class ProxyShapeSet {
    readonly ProxyShape[] shapes;
    readonly FrozenDictionary<Symbol, int> byName;

    ProxyShapeSet(string name, Symbol vocabulary, ProxyShape[] shapes) {
        Name = name;
        Vocabulary = vocabulary;
        this.shapes = shapes;

        Dictionary<Symbol, int> index = [];

        for (var at = 0; at < shapes.Length; at++) {
            index.TryAdd(shapes[at].Name, at);
        }

        byName = index.ToFrozenDictionary();
    }

    /// <summary>What the set is called.</summary>
    public string Name { get; }

    /// <summary>Which vocabulary it declares itself against, or <see cref="Symbol.None" />.</summary>
    public Symbol Vocabulary { get; }

    /// <summary>How many shapes it holds.</summary>
    public int Count => shapes.Length;

    /// <summary>The shapes.</summary>
    /// <returns>The shapes.</returns>
    public ReadOnlySpan<ProxyShape> Shapes => shapes;

    /// <summary>One shape.</summary>
    /// <param name="index">Which.</param>
    /// <returns>The shape.</returns>
    public ProxyShape this[int index] => shapes[index];

    /// <summary>Where a named shape is, or −1.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its index, or −1.</returns>
    public int IndexOf(Symbol name) => byName.TryGetValue(name, out var index) ? index : -1;

    /// <summary>Where a named shape is, or −1.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Its index, or −1.</returns>
    public int IndexOf(string name) => IndexOf(Symbol.Intern(name));

    /// <summary>The first shape carrying a tag, or −1.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Its index, or −1.</returns>
    /// <remarks>
    ///     What makes one authored sitting clip work against a chair, a bench and a crate: the clip
    ///     names <c>seat</c>, and whichever shape affords it answers.
    /// </remarks>
    public int FirstTagged(Facet tag) {
        for (var index = 0; index < shapes.Length; index++) {
            if (shapes[index].Tags.Contains(tag)) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Builds a set.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="vocabulary">
    ///     Which vocabulary it implements, or <see langword="null" /> for none.
    /// </param>
    /// <param name="shapes">The shapes.</param>
    /// <returns>The set.</returns>
    /// <exception cref="InvalidOperationException">Two shapes share a name.</exception>
    /// <remarks>
    ///     ⚠ <b>Duplicate names are refused rather than resolved.</b> A set with two shapes called
    ///     <c>left-palm</c> is a set where a clip's contact silently lands on whichever one the
    ///     authoring tool happened to write first, and the two are usually the left and the right.
    /// </remarks>
    public static ProxyShapeSet Of(string name, string? vocabulary, params ReadOnlySpan<ProxyShape> shapes) {
        ArgumentNullException.ThrowIfNull(name);

        var built = shapes.ToArray();
        HashSet<Symbol> seen = [];

        foreach (var shape in built) {
            ArgumentNullException.ThrowIfNull(shape);

            if (!seen.Add(shape.Name)) {
                throw new InvalidOperationException(
                    $"The proxy shape set '{name}' has two shapes called '{shape.Name}'. "
                    + "A contact naming it would land on whichever one was written first."
                );
            }
        }

        return new(name, Symbol.Intern(vocabulary), built);
    }

    /// <summary>The same set with every shape resized.</summary>
    /// <param name="name">What the resized set is called.</param>
    /// <param name="scale">What to multiply the dimensions and offsets by.</param>
    /// <returns>The set.</returns>
    /// <remarks>
    ///     ⚠ <b>A stand-in for a real regressor, and honest about it.</b>
    ///     [33 § D15](../../../docs/plan/33-character-creator.md) derives a body's shapes from its
    ///     archetype, which is where a set that tracks a character's proportions properly comes from.
    ///     A uniform-per-axis rescale is what a project without that has, and it is enough to prove
    ///     the property that matters: a coordinate on a shape names the same place on a body of any
    ///     size.
    /// </remarks>
    public ProxyShapeSet Resized(string name, Vector3 scale) {
        var resized = new ProxyShape[shapes.Length];

        for (var index = 0; index < shapes.Length; index++) {
            var shape = shapes[index];

            resized[index] = new() {
                Name = shape.Name,
                Kind = shape.Kind,
                Joint = shape.Joint,
                Offset = new(shape.Offset.Translation * scale, shape.Offset.Rotation, shape.Offset.Scale),
                Dimensions = shape.Dimensions.Scaled(scale),
                Tags = shape.Tags,
                Coarse = shape.Coarse
            };
        }

        return new(name, Vocabulary, resized);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({shapes.Length} shapes)";
}
