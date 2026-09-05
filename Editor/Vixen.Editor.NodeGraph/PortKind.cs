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
    Flow = 10,

    /// <summary>
    ///     A whole raster: the thing a texture graph's nodes hand each other.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Not <see cref="Texture" />, and the difference is the reason this member exists.</b>
    ///         A <see cref="Texture" /> is a bound resource a <i>shader</i> samples one texel of;
    ///         an <see cref="Image" /> is a buffer of texels a compositing kernel reads a
    ///         neighbourhood of and writes a new one from — doc 48 § D2's row "neighbourhood access:
    ///         none / the whole point". Riding <see cref="Texture" /> would have cost nothing at the
    ///         type level and everything at the authoring one: <c>PortFilter</c> would then offer a
    ///         shader graph's <c>Sample 2D</c> when a wire is dropped off a blur's output, which is a
    ///         node that cannot run in a texture graph on a connection that cannot mean anything.
    ///         <see cref="PortKinds.Accepts" /> refuses that wire only because the two kinds differ.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Grey and colour are one kind and the difference is a <i>format</i> on it</b>
    ///         (doc 48 § Part 4), so grey promoting into a colour port and colour being refused by a
    ///         grey one are <b>not</b> decided here: a <see cref="PortKind" /> carries no format, and
    ///         <see cref="PortKinds.Accepts" /> therefore says yes to every image-to-image wire.
    ///         Naming the port a colour arrived at is the texture graph's compiler's, where the
    ///         format is known — the same division as <see cref="Dynamic" />, whose width is resolved
    ///         by a compiler rather than by the enum.
    ///     </para>
    ///     <para>
    ///         <b>It answers zero to both of the port model's questions, and neither is an
    ///         oversight.</b> <see cref="PortKinds.Lanes" /> is "how wide is this value in the emitted
    ///         source", and an image is not a float vector of any width — a dispatch over a storage
    ///         image is not an expression with lanes. <see cref="PortKinds.Fields" /> is "how many
    ///         boxes does an author type into", and there is no literal image: an unconnected image
    ///         input is a hole, which is what a source node exists to fill. That puts it with
    ///         <see cref="Texture" />, <see cref="Sampler" /> and <see cref="Flow" /> rather than with
    ///         <see cref="Bool" />, <see cref="Int" /> and <see cref="Dynamic" />, which answer zero
    ///         and <i>one</i>.
    ///     </para>
    /// </remarks>
    Image = 11
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

    /// <summary>How many numbers an author types into an unconnected port of a kind, or zero for none.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>One to four, or zero when the port takes no typed value at all.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Not <see cref="Lanes" />, and the three kinds where they differ are the point.</b>
    ///         Lanes is how wide a value <i>is</i> in the emitted source, which is zero for a boolean,
    ///         an integer and an unresolved dynamic — none of which is a float vector. What an editor
    ///         needs is how many boxes to draw, and all three of those take exactly one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A dynamic port takes one number however wide it turned out to be.</b> The
    ///         compiler pads a short constant with its last lane — see <c>NodeGraphCompiler</c> — so a
    ///         <c>0.25</c> typed into a port that resolved to a colour compiles as a grey rather than
    ///         as a red. Drawing four boxes because the node happens to have resolved to a
    ///         <c>float4</c> would make the same graph offer a different editor depending on what was
    ///         wired to a <i>different</i> port.
    ///     </para>
    /// </remarks>
    public static int Fields(PortKind kind) => kind switch {
        PortKind.Bool or PortKind.Int or PortKind.Dynamic => 1,
        _ => Lanes(kind)
    };

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
    ///     <para>
    ///         ⚠ <b>That exact match is the whole of <see cref="PortKind.Image" />'s refusal</b>, and
    ///         it is why an image is not a texture with a different name: a texture graph's image
    ///         port takes an image and nothing else, so no wire and no search-to-create result can
    ///         put a shader graph's sampler on one. It is also the whole of what this method knows
    ///         about images — grey against colour is a format the enum does not carry, and the
    ///         compiler that does carry it is what names the port.
    ///     </para>
    /// </remarks>
    public static bool Accepts(PortKind source, PortKind target) =>
        (IsVector(source) && IsVector(target)) || source == target;
}
