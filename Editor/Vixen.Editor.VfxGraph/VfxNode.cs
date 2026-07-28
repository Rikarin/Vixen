// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.NodeGraph;
using Vixen.Vfx;

namespace Vixen.Editor.VfxGraph;

/// <summary>
///     What a VFX node contributes to the graph being built.
/// </summary>
/// <remarks>
///     <para>
///         <b>Blocks, not expressions.</b> A shader node emits a line of source; a VFX node adds an
///         <i>operation</i> to one of three lists, or names the renderer. That is the difference
///         between the two graphs, and it is the whole difference: everything else — the ports, the
///         typing, the ordering, the diagnostics — is the same framework.
///     </para>
///     <para>
///         <b>Parameters are numbers, not text.</b> A <see cref="VfxOperation" /> holds two
///         <c>Vector4</c>s, so a node reads its ports through
///         <see cref="NodeBinding.Value" /> rather than through the expression a shader node would
///         interpolate. That is why the framework hands over both forms.
///     </para>
/// </remarks>
public sealed class VfxGraphBuilder {
    internal VfxGraphBuilder() { }

    /// <summary>The spawners, in the order the graph produced them.</summary>
    public List<VfxSpawner> Spawners { get; } = [];

    /// <summary>The initializers.</summary>
    public List<VfxOperation> Initializers { get; } = [];

    /// <summary>The updaters.</summary>
    public List<VfxOperation> Updaters { get; } = [];

    /// <summary>How the particles are drawn, when a node has said.</summary>
    public VfxRenderer? Renderer { get; set; }

    /// <summary>The most particles that may be alive at once.</summary>
    /// <remarks>
    ///     A property of the effect rather than of any block, and the one number an author has to
    ///     choose: it is the memory budget, and the module refuses to guess it — see
    ///     <see cref="ParticleBuffer" />'s capacity policy.
    /// </remarks>
    public int Capacity { get; set; } = 1024;

    /// <summary>The custom attributes the graph declares.</summary>
    public List<VfxCustomAttribute> Customs { get; } = [];
}

/// <summary>
///     A node of a VFX graph: something that contributes a block.
/// </summary>
public abstract class VfxNode : Node {
    /// <summary>Adds whatever this node contributes.</summary>
    /// <param name="builder">What is being built.</param>
    protected internal abstract void Contribute(VfxGraphBuilder builder);

    /// <summary>One port's value, as a vector, padded from however many lanes it has.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>Its value.</returns>
    /// <remarks>
    ///     Padded rather than refused, because a three-lane port feeding a <c>Vector4</c> parameter is
    ///     the normal case — the fourth component of a position is not a component the author has an
    ///     opinion about.
    /// </remarks>
    protected Vector4 Vector(string port) {
        var lanes = Binding.Value(port);

        return new(
            lanes.Length > 0 ? lanes[0] : 0f,
            lanes.Length > 1 ? lanes[1] : 0f,
            lanes.Length > 2 ? lanes[2] : 0f,
            lanes.Length > 3 ? lanes[3] : 0f
        );
    }

    /// <summary>One port's value, as a number.</summary>
    /// <param name="port">The port's name.</param>
    /// <returns>Its first lane, or zero.</returns>
    protected float Number(string port) {
        var lanes = Binding.Value(port);

        return lanes.Length > 0 ? lanes[0] : 0f;
    }
}
