// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>One attribute a vertex buffer holds, under the name a shader asks for it by.</summary>
/// <param name="Name">
///     The shader's parameter or stream name — <c>position</c>, <c>normal</c>, <c>texcoord</c>.
/// </param>
/// <param name="Format">How the bytes at <paramref name="Offset" /> are read.</param>
/// <param name="Offset">Where the attribute starts inside one vertex, in bytes.</param>
public readonly record struct VertexChannel(string Name, VertexFormat Format, int Offset);

/// <summary>
///     What a vertex buffer holds, by name — the half of a vertex layout the geometry knows.
/// </summary>
/// <remarks>
///     <para>
///         <b>A vertex layout is a join of two facts, and only one of them belongs to the mesh.</b>
///         The buffer decides which attributes exist, how they are packed and how far apart two
///         vertices are; the <em>shader</em> decides which of them it reads and at which location.
///         <see cref="VertexLocations" /> is the older half-answer to the same problem — a caller
///         hands over the numbers a shader's reflection reported, once, for one shader. That works
///         while every draw goes through one shader and stops working the moment a second pass
///         draws the same mesh: a shadow caster declares three attributes where the forward pass
///         declares four, and they are at different locations because <c>StreamPlan</c> numbers a
///         stage's parameters after the shader's streams.
///     </para>
///     <para>
///         So the numbers are not written down here. <see cref="Layout" /> takes the effect that is
///         about to be drawn with, reads the locations off its reflection, and matches them to
///         these attributes <em>by name</em> — which makes one description of the mesh correct for
///         every pass that draws it.
///     </para>
///     <para>
///         ⚠ <b>An attribute the shader declares and this does not have is refused, and that is the
///         point.</b> Binding nothing to a declared location is not a validation error on most
///         drivers — it is a stage reading whatever was left in the slot — and describing a location
///         the pipeline has no variable at <em>is</em> one. Both are silent failures a long way from
///         their cause, so the join is checked here where both halves are in hand.
///     </para>
/// </remarks>
public sealed class VertexSchema {
    readonly VertexChannel[] attributes;

    /// <summary>Describes a vertex format.</summary>
    /// <param name="stride">Bytes between consecutive vertices.</param>
    /// <param name="attributes">What one vertex holds, in any order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="attributes" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The stride is not positive.</exception>
    public VertexSchema(int stride, params VertexChannel[] attributes) {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);

        Stride = stride;
        this.attributes = attributes;
    }

    /// <summary>Bytes between consecutive vertices.</summary>
    public int Stride { get; }

    /// <summary>What one vertex holds.</summary>
    public IReadOnlyList<VertexChannel> Attributes => attributes;

    /// <summary>A second buffer, advanced once per <em>instance</em> rather than per vertex.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What makes one layout index describe an instanced draw.</b> An instanced renderer
    ///         binds two buffers — the shape, once per vertex, and the per-instance record beside it —
    ///         and the difference between them is a step mode the pipeline declares, not anything
    ///         either buffer knows. So the join is here, where both halves are named: an attribute the
    ///         stage declares is looked for in this format first and in the instance stream second,
    ///         and lands in whichever buffer holds it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The step mode is the whole mechanism, and getting it wrong draws.</b> A per-instance
    ///         record read at the vertex rate hands every vertex of one shape a different instance's
    ///         transform, which is a picture — one exploded object — rather than an error.
    ///     </para>
    ///     <para>
    ///         Null for the ordinary case, which is every mesh in a scene: one buffer, one rate, and
    ///         <see cref="Layout" /> returns the single element it always did.
    ///     </para>
    /// </remarks>
    public VertexSchema? Instances { get; init; }

    /// <summary>The vertex layout for drawing this format with one effect.</summary>
    /// <param name="effect">The effect whose vertex stage is about to read it.</param>
    /// <returns>
    ///     One buffer layout holding an element per input the stage declares, or two when
    ///     <see cref="Instances" /> is set — the shape first, the per-instance stream second.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="effect" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The stage declares an attribute this format has no data for.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         Every input the reflection lists, and only those. A variant that folded an attribute
    ///         away does not declare it — Raven drops an input its code never reads, because a
    ///         parameter cannot appear inside a permutation — so the layout shrinks with the shader
    ///         rather than describing an attribute the module has no variable for.
    ///     </para>
    ///     <para>
    ///         The format's own <see cref="VertexChannel.Format" /> decides how the bytes are
    ///         read, not the reflection's type. They should agree, and where they do not it is the
    ///         buffer that is right: the reflection says a stage wants four floats, and a packed
    ///         format is entitled to hand it four bytes and let the input assembler convert — which
    ///         is exactly what a bone index is.
    ///     </para>
    /// </remarks>
    public VertexBufferLayout[] Layout(Effect effect) {
        ArgumentNullException.ThrowIfNull(effect);

        var perVertex = new List<VertexElement>(effect.VertexInputs.Length);
        var perInstance = Instances is null ? null : new List<VertexElement>(effect.VertexInputs.Length);

        foreach (var input in effect.VertexInputs) {
            if (Find(input.Name) is { } source) {
                perVertex.Add(new((uint)input.Location, source.Format, source.Offset));

                continue;
            }

            // The instance stream second, so a name held by both is the vertex one. Nothing in the
            // engine spells a channel both ways, and the order has to be decided rather than left to
            // whichever list is searched first.
            if (Instances?.Find(input.Name) is { } instanced) {
                perInstance!.Add(new((uint)input.Location, instanced.Format, instanced.Offset));

                continue;
            }

            throw new InvalidOperationException(
                $"'{effect.Key.ShaderName}' reads a vertex attribute called '{input.Name}' at location "
                + $"{input.Location}, and this vertex format holds "
                + $"{string.Join(", ", attributes.Select(a => a.Name))}"
                + (Instances is null
                    ? string.Empty
                    : $" with an instance stream holding {string.Join(", ", Instances.attributes.Select(a => a.Name))}")
                + ". A pipeline that described no attribute there would be refused, and one that "
                + "described the wrong bytes would draw."
            );
        }

        if (Instances is not { } stream) {
            return [new VertexBufferLayout(Stride, perVertex.ToArray())];
        }

        // Both buffers, always, even where the stage read nothing from one of them: the feature binds
        // two and a description that named one would leave the other bound to a layout with no
        // binding at that index.
        return [
            new VertexBufferLayout(Stride, perVertex.ToArray()),
            new VertexBufferLayout(stream.Stride, perInstance!.ToArray(), VertexStepMode.Instance)
        ];
    }

    VertexChannel? Find(string name) {
        foreach (var attribute in attributes) {
            if (string.Equals(attribute.Name, name, StringComparison.Ordinal)) {
                return attribute;
            }
        }

        return null;
    }
}
