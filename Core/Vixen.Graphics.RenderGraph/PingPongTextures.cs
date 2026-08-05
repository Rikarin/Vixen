// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Graphics.RenderGraph;

/// <summary>The two halves of a ping-pong, as this frame sees them.</summary>
/// <param name="Read">Last frame's result. The step's input.</param>
/// <param name="Write">Where this frame's result goes.</param>
/// <remarks>
///     Named for what the step does with them rather than by parity, because parity is the thing a
///     caller should never have to hold: a step that reads <c>textures[frame &amp; 1]</c> is a step
///     that reads the wrong one the first time somebody adds a second dispatch.
/// </remarks>
public readonly record struct PingPongPair(GraphTexture Read, GraphTexture Write);

/// <summary>
///     Two textures a simulation alternates between, imported into a graph and swapped per step.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § B5](../../docs/plan/35-water.md#b5-there-is-no-ping-pong-compute-target-helper).</b>
///         A height field advanced by a pass that reads frame N and writes frame N + 1 needs two
///         targets and a rotation between them, and the render graph has to be told about the
///         dependency or the barrier between the write and the next frame's read is one somebody has
///         to remember. Compute exists and is spent; this is the small piece that did not.
///     </para>
///     <para>
///         <b>Imported, not declared, and that is the whole reason this type exists rather than two
///         <see cref="RenderGraph.CreateTexture" /> calls.</b> The graph's transients are recycled at
///         the end of the frame precisely because their lifetime ends inside it — a ping-pong's does
///         not, by definition. Declaring them would give the read target whatever memory the pool
///         happened to hand back, which is the previous frame's contents about as often as it is not,
///         and therefore looks almost right.
///     </para>
///     <para>
///         ⚠ <b>The first read is undefined until something has written it, and
///         <see cref="HasHistory" /> is how a caller knows.</b> The graph cannot catch this: an import
///         counts as produced, so reading one no pass has written is legal and silent. A simulation
///         step should either skip the read on its first frame or clear the pair — see
///         <see cref="Clear" />, which is the same decision made once rather than per consumer.
///     </para>
///     <para>
///         <b>Not a subclass of anything and not held by the graph.</b> A ping-pong outlives the graph
///         it is imported into, and several graphs in one frame may import the same one; ownership
///         belongs to whoever runs the simulation.
///     </para>
/// </remarks>
public sealed class PingPongTextures : IDisposable {
    /// <summary>What an idle ping-pong's textures are left in between frames.</summary>
    /// <remarks>
    ///     <para>
    ///         One resting state rather than two, and the exit state of every import, so the entry
    ///         state of the next one is knowable without the graph telling anybody what it did. A pair
    ///         left in whatever the last pass used would need the caller to track it — which is the
    ///         bookkeeping this type exists to remove.
    ///     </para>
    ///     <para>
    ///         <see cref="ResourceState.ShaderRead" /> because that is what the next frame's read
    ///         wants, so the common path costs one transition on the write target and none on the
    ///         read.
    ///     </para>
    /// </remarks>
    public const ResourceState RestingState = ResourceState.ShaderRead;

    readonly IGraphicsDevice device;
    readonly TextureHandle[] textures = new TextureHandle[2];
    readonly TextureViewHandle[] views = new TextureViewHandle[2];
    readonly ResourceState[] states = [ResourceState.Undefined, ResourceState.Undefined];

    int parity;
    bool disposed;

    /// <summary>Creates a pair from a description.</summary>
    /// <param name="device">The device the two textures come from.</param>
    /// <param name="description">What each of them is. Both are identical by construction.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The usage has to cover both halves of what the pair is for.</b> A simulation step that
    ///     samples the read target and writes the write one needs <see cref="TextureUsage.Sampled" />
    ///     and whichever of <see cref="TextureUsage.Storage" /> or
    ///     <see cref="TextureUsage.ColourTarget" /> the step uses — and the two alternate, so both
    ///     textures need both. There is no half of a ping-pong that is only ever read.
    /// </remarks>
    public PingPongTextures(IGraphicsDevice device, in TextureDescription description) {
        ArgumentNullException.ThrowIfNull(device);
        description.Validate();

        this.device = device;
        Description = description;

        for (var index = 0; index < 2; index++) {
            var named = description with {
                Name = string.IsNullOrEmpty(description.Name)
                    ? $"pingpong {index}"
                    : $"{description.Name} {index}"
            };

            textures[index] = device.CreateTexture(named);
            views[index] = device.CreateTextureView(textures[index]);
        }
    }

    /// <summary>What each of the two textures is.</summary>
    /// <remarks>The name is the pair's; each texture's own has an index appended.</remarks>
    public TextureDescription Description { get; }

    /// <summary>How many steps have been taken.</summary>
    public long StepCount { get; private set; }

    /// <summary>Whether anything has been written yet.</summary>
    /// <remarks>
    ///     ⚠ <b>False means the read target holds nothing, and nothing will say so.</b> An import is
    ///     produced as far as <c>RenderGraph</c>'s read validation is concerned — it has to be, since
    ///     the whole point of importing is that a previous frame filled it — so a first-frame read
    ///     passes validation and samples uninitialised memory. On most drivers that is zeroes, which
    ///     is exactly what a settled height field looks like, and so the bug survives to the first
    ///     machine whose driver hands back something else.
    /// </remarks>
    public bool HasHistory { get; private set; }

    /// <summary>The texture this step reads.</summary>
    public TextureHandle ReadTexture => textures[parity];

    /// <summary>The texture this step writes.</summary>
    public TextureHandle WriteTexture => textures[parity ^ 1];

    /// <summary>Brings both textures into a graph as this step's read and write.</summary>
    /// <param name="graph">The graph.</param>
    /// <returns>The pair.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Both are imported, every step, whether or not the step touches them.</b> A graph told
    ///         about only the one it uses cannot place the barrier between this step's write and the
    ///         next step's read of the same texture — which is the dependency this whole type is for,
    ///         and the one that is invisible in a picture: an unsynchronised read of a compute write
    ///         produces a field that is one step stale in some tiles and current in others, which
    ///         reads as noise in the simulation rather than as a race.
    ///     </para>
    ///     <para>
    ///         <b>What the step needs each half to be in is the pass's declaration, not this one's.</b>
    ///         An import's states say what the resource <em>is</em> on the way in and must be on the
    ///         way out; a pass's say what it is used as in between, and a step that reads through a
    ///         sampler on one backend and a storage image on another declares that itself.
    ///     </para>
    ///     <para>
    ///         Entry states are tracked here rather than assumed, so the first import of a fresh pair
    ///         transitions from <see cref="ResourceState.Undefined" /> — legal from any state, and the
    ///         honest description of memory nothing has written.
    ///     </para>
    /// </remarks>
    public PingPongPair Import(RenderGraph graph) {
        ArgumentNullException.ThrowIfNull(graph);
        ObjectDisposedException.ThrowIf(disposed, this);

        return new(Bring(graph, parity), Bring(graph, parity ^ 1));
    }

    /// <summary>Declares a pass that clears both textures, so the first read is defined.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="clear">What to clear to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graph" /> is null.</exception>
    /// <exception cref="RenderGraphException">The textures cannot be colour attachments.</exception>
    /// <remarks>
    ///     <para>
    ///         The one decision <see cref="HasHistory" /> leaves the caller, made once. Called before
    ///         <see cref="Import" /> in the frame that needs it — normally the first, and again after
    ///         anything that invalidates the simulation, which for a sliding window is the window
    ///         having jumped rather than scrolled.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A render pass rather than a copy, and it therefore needs
    ///         <see cref="TextureUsage.ColourTarget" />.</b> There is no clear-texture operation on
    ///         <see cref="ICommandList" /> and deliberately so — every backend spells one differently
    ///         and half of them implement it as this. A pair declared for storage alone is refused
    ///         here rather than silently left dirty.
    ///     </para>
    /// </remarks>
    public void Clear(RenderGraph graph, Color4 clear = default) {
        ArgumentNullException.ThrowIfNull(graph);
        ObjectDisposedException.ThrowIf(disposed, this);

        if ((Description.Usage & TextureUsage.ColourTarget) == 0) {
            throw new RenderGraphException(
                $"The ping-pong '{Description.Name}' cannot be cleared, because clearing is a render "
                + "pass and its textures were not declared as colour targets. Add ColourTarget to the "
                + "usage, or have the simulation's first step write every texel rather than reading "
                + "one it has not written."
            );
        }

        for (var index = 0; index < 2; index++) {
            var texture = Bring(graph, index);

            graph.AddPass(
                $"{Description.Name} clear {index}",
                pass => {
                    pass.ColourAttachment(texture, LoadAction.Clear, clear);

                    // Nothing in this frame reads what the clear wrote — the next step does — so
                    // culling would remove both passes and the pair would stay undefined.
                    pass.SideEffect();
                    pass.Execute(_ => { });
                }
            );
        }

        HasHistory = true;
    }

    /// <summary>Swaps the two, so this step's result becomes the next one's input.</summary>
    /// <remarks>
    ///     ⚠ <b>Called after the graph has been executed, not after it has been declared.</b> Both
    ///     halves are imported by index, so swapping mid-declaration would give two passes in one
    ///     frame two different opinions about which texture is the input — and the second one would be
    ///     reading what it had just written, which is the exact undefined-behaviour a ping-pong exists
    ///     to avoid.
    /// </remarks>
    public void Advance() {
        ObjectDisposedException.ThrowIf(disposed, this);

        parity ^= 1;
        StepCount++;
        HasHistory = true;
    }

    /// <summary>Destroys both textures and their views.</summary>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        for (var index = 0; index < 2; index++) {
            if (views[index].IsValid) {
                device.Destroy(views[index]);
            }

            if (textures[index].IsValid) {
                device.Destroy(textures[index]);
            }
        }
    }

    /// <summary>Imports one of the two, from the state it is in to the state it rests in.</summary>
    GraphTexture Bring(RenderGraph graph, int index) {
        var imported = graph.ImportTexture(
            textures[index],
            views[index],
            Description with { Name = $"{Description.Name} {index}" },
            states[index],
            RestingState
        );

        // An exit state is a promise the graph keeps whether or not any pass touched the resource —
        // RestoreImports transitions anything whose current state differs, and an untouched import's
        // current state is the entry one. So by the time this graph has executed the texture is
        // resting, and the next import can say so.
        states[index] = RestingState;

        return imported;
    }
}
