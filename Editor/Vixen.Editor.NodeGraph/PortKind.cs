// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.NodeGraph;

/// <summary>What a port carries.</summary>
/// <remarks>
///     <para>
///         A closed set, and deliberately a small one. Every kind here is something the three graphs
///         can all agree on the meaning of; a node that needs a type nobody else has is a node that
///         should be passing an index or a name through a scalar.
///     </para>
///     <para>
///         The vector kinds are ordered by width, which is not decoration:
///         <see cref="PortKinds.Resolve" /> picks the widest of a set, and "widest" is
///         <c>Max</c> over these values.
///     </para>
/// </remarks>
public enum PortKind {
    /// <summary>Unresolved. Only <see cref="Dynamic" /> ports are ever this, and only before typing.</summary>
    None = 0,

    /// <summary>A boolean.</summary>
    Bool = 1,

    /// <summary>A 32-bit signed integer.</summary>
    Int = 2,

    /// <summary>One float.</summary>
    Float = 3,

    /// <summary>Two.</summary>
    Float2 = 4,

    /// <summary>Three.</summary>
    Float3 = 5,

    /// <summary>Four.</summary>
    Float4 = 6,

    /// <summary>A 2D texture.</summary>
    Texture = 7,

    /// <summary>A sampler.</summary>
    Sampler = 8,

    /// <summary>
    ///     A vector whose width is whatever it is connected to.
    /// </summary>
    /// <remarks>
    ///     The one interesting thing in the type system, and the reason doc 11 says it belongs in the
    ///     port model from the start rather than being bolted onto one graph. A <c>Lerp</c> node works
    ///     on floats, on colours and on positions, and authoring three of it is what a shader graph
    ///     without this looks like.
    /// </remarks>
    Dynamic = 9,

    /// <summary>
    ///     No value at all: an edge that means "after".
    /// </summary>
    /// <remarks>
    ///     What a VFX graph's blocks are wired with. A block does not hand the next one a number, it
    ///     runs before it, and the order is the whole content of the connection. Giving that its own
    ///     kind rather than reusing an integer port means the compiler can refuse a wire between a
    ///     value and an ordering, and means an unconnected one has no default to invent.
    /// </remarks>
    Flow = 10
}

/// <summary>The rules a port's type follows.</summary>
public static class PortKinds {
    /// <summary>How many float lanes a kind occupies, or zero when it is not a vector at all.</summary>
    public static int Lanes(PortKind kind) => kind switch {
        PortKind.Float => 1,
        PortKind.Float2 => 2,
        PortKind.Float3 => 3,
        PortKind.Float4 => 4,
        _ => 0
    };

    /// <summary>Whether a kind is one of the float vectors, which is what <see cref="PortKind.Dynamic" /> resolves to.</summary>
    public static bool IsVector(PortKind kind) => Lanes(kind) > 0;

    /// <summary>The vector kind with a given number of lanes.</summary>
    /// <param name="lanes">One to four.</param>
    /// <returns>The kind.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lanes" /> is not one to four.</exception>
    public static PortKind OfLanes(int lanes) => lanes switch {
        1 => PortKind.Float,
        2 => PortKind.Float2,
        3 => PortKind.Float3,
        4 => PortKind.Float4,
        _ => throw new ArgumentOutOfRangeException(nameof(lanes), lanes, "A vector port has one to four lanes.")
    };

    /// <summary>
    ///     What a node's dynamic ports resolve to, given what its inputs are connected to.
    /// </summary>
    /// <param name="connected">The kinds arriving at its dynamic inputs. Unconnected ones are absent.</param>
    /// <returns>The resolved kind, or <see cref="PortKind.Float" /> when nothing is connected.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The widest wins, and everything narrower is promoted.</b> A <c>Lerp</c> with a
    ///         <c>float3</c> and a <c>float</c> is a <c>float3</c> lerp with the scalar splatted, which
    ///         is what an author means and what every shader language already does for
    ///         <c>float3 * float</c>. The alternative — refusing the mixture — turns the common case
    ///         into an error and a manual splat node.
    ///     </para>
    ///     <para>
    ///         <b>A node with nothing connected is a float.</b> It has to be something: the emitted
    ///         source needs a type, and the narrowest is the one that promotes into anything later.
    ///     </para>
    ///     <para>
    ///         Only vector kinds take part. A texture arriving at a dynamic port is a type error that
    ///         <see cref="NodeGraphCompiler" /> reports against the port rather than something to
    ///         widen — there is no width a texture and a float agree on.
    ///     </para>
    /// </remarks>
    public static PortKind Resolve(ReadOnlySpan<PortKind> connected) {
        var widest = PortKind.None;

        foreach (var kind in connected) {
            if (IsVector(kind) && kind > widest) {
                widest = kind;
            }
        }

        return widest == PortKind.None ? PortKind.Float : widest;
    }

    /// <summary>Whether a value of one kind can be fed to a port of another.</summary>
    /// <param name="source">What the edge carries.</param>
    /// <param name="target">What the port wants.</param>
    /// <returns><see langword="true" /> if it can.</returns>
    /// <remarks>
    ///     <para>
    ///         Vectors convert to each other freely, in both directions: widening splats or pads and
    ///         narrowing takes a prefix, which is the swizzle rule every shader language has and the
    ///         one authors already expect. Refusing to narrow would mean a <c>Split</c> node between
    ///         a colour and anything that wanted its red.
    ///     </para>
    ///     <para>
    ///         Everything else has to match exactly. A texture is not a float however many lanes are
    ///         involved, and a bool that silently became a float is a condition that is always true.
    ///     </para>
    /// </remarks>
    public static bool Accepts(PortKind source, PortKind target) =>
        (IsVector(source) && IsVector(target)) || source == target;
}
