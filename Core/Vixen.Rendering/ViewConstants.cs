// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering;

/// <summary>
///     One uniform block per view, bound before that view's work.
/// </summary>
/// <remarks>
///     <para>
///         Set 1 of the four-set convention, and the thing whose absence was the largest hole in the
///         renderer: nothing could tell a shader where it was being drawn <em>from</em>.
///         <see cref="Features.TransformRenderFeature" /> pushes a world matrix and there was no
///         second half — so a shadow caster could not know which cascade's projection to use, and a
///         golden fixture had to compose the cascade's matrix into the object's world transform to
///         draw anything at all.
///     </para>
///     <para>
///         <strong>One block per view, not per draw.</strong> A frame has a camera and a dozen views —
///         four cascades, six probe faces — and every object in a view shares its matrix. Binding it
///         once per <c>(view, stage)</c> rather than per object is the whole reason the convention
///         orders its sets by how often they change.
///     </para>
///     <para>
///         <strong>The layout is shared, and that is what makes set 1 work at all.</strong> A
///         descriptor set is only bindable across pipelines whose layouts agree up to that set, so
///         every shader in a frame reads the same per-view block by construction. That is why the
///         members are configured here once rather than taken from an effect: the block belongs to
///         the frame, not to any shader in it.
///     </para>
///     <para>
///         The view's own matrix and position are written for you, so the common case is to leave
///         <see cref="Members" /> at its default and set nothing. Anything else a project wants in
///         set 1 — the time, an exposure, a light environment — is a member and a key it sets on
///         <see cref="Of" />.
///     </para>
/// </remarks>
public sealed class ViewConstants(IGraphicsDevice device, string name = "View") : IDisposable {
    /// <summary>The view-projection, at byte 0 of the default layout.</summary>
    public static readonly ParameterKey<Matrix4x4> ViewProjection =
        ParameterKeys.New<Matrix4x4>("Vixen.ViewProjection");

    /// <summary>Where the view is, at byte 64 of the default layout.</summary>
    public static readonly ParameterKey<Vector3> ViewPosition =
        ParameterKeys.New<Vector3>("Vixen.ViewPosition");

    /// <summary>World to view, at byte 80 of the default layout.</summary>
    /// <remarks>
    ///     Not the same thing as <see cref="ViewProjection" /> without the projection: it is what
    ///     places a fragment in <em>view</em> space, which is where a cluster grid is built. A shader
    ///     that reads it and a host that never wrote it agree on a matrix of zeros, and every fragment
    ///     lands in the froxel at the origin.
    /// </remarks>
    public static readonly ParameterKey<Matrix4x4> View = ParameterKeys.New<Matrix4x4>("Vixen.View");

    readonly Dictionary<RenderView, Block> blocks = [];
    bool disposed;

    /// <summary>Where the descriptor sets come from. Without one, nothing is bound.</summary>
    public DescriptorAllocator? Descriptors { get; set; }

    /// <summary>The set's shape. Without one, nothing is bound.</summary>
    public DescriptorSetLayoutHandle Layout { get; set; }

    /// <summary>Which of the four conventional sets this is.</summary>
    public DescriptorSetSlot Slot { get; set; } = DescriptorSetSlot.PerView;

    /// <summary>Which binding within the set the block occupies.</summary>
    public uint Binding { get; set; }

    /// <summary>How large the block is, in bytes.</summary>
    /// <remarks>
    ///     <para>
    ///         A hundred and forty-four: a <c>mat4</c>, a <c>float3</c> and a second <c>mat4</c> under
    ///         std140, which puts the position at 64, the view matrix at 80 and rounds the block to
    ///         144. A project that adds members sets this to match.
    ///     </para>
    ///     <para>
    ///         It is what <c>ForwardPlus.rvn</c> declares for set 1, and that is not a coincidence to
    ///         be tidied away later: set 1 is a <em>contract between shaders</em>, so the engine's own
    ///         pass is what defines it. It was eighty here while the shader said 144 — a descriptor
    ///         range shorter than the block it points at, which the Vulkan validation layers report
    ///         and a release driver reads past.
    ///     </para>
    /// </remarks>
    public int Size { get; set; } = 144;

    /// <summary>Where each value goes in the block.</summary>
    public IList<EffectParameter> Members { get; } = [
        new(ViewProjection, 0, 64),
        new(ViewPosition, 64, 12),
        new(View, 80, 64)
    ];

    /// <summary>How many views have a block.</summary>
    public int Count => blocks.Count;

    /// <summary>Whether there is enough here to bind anything.</summary>
    public bool IsConfigured => Descriptors is not null && Layout.IsValid && Size > 0;

    /// <summary>The values for one view, for a caller adding members of its own.</summary>
    public ParameterCollection Of(RenderView view) {
        ArgumentNullException.ThrowIfNull(view);
        return BlockFor(view).Parameters;
    }

    /// <summary>Takes the set layout off a resolved shader, if it has not got one.</summary>
    /// <param name="effect">The pass about to be drawn, whose set this is.</param>
    /// <returns>Whether there is a layout now.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="effect" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Because a host cannot set <see cref="Layout" /> before there is a shader, and a
    ///         frame that binds nothing is not recoverable.</b> A set is allocated against a layout
    ///         that has to match the pipeline's exactly, so only the shader has one — and the first
    ///         shader resolves inside the first frame, after every constructor a host has run. A host
    ///         adopting one between frames is a frame late, and one refused frame is a GPU fault on
    ///         Metal rather than a dark frame, so there is no second frame to be right in.
    ///     </para>
    ///     <para>
    ///         <c>SceneConstants.Bind</c> takes the effect and reads its own set off it for the same
    ///         reason, and so, now, does
    ///         <see cref="Bind(ICommandList, RenderView, Effect?)" /> — which is the better answer and
    ///         makes this one a fallback. A host still <em>may</em> set <see cref="Layout" /> itself,
    ///         and one that has is left alone.
    ///     </para>
    /// </remarks>
    public bool AdoptLayout(Effect effect) {
        ArgumentNullException.ThrowIfNull(effect);

        if (Layout.IsValid) {
            return true;
        }

        var slot = (int)Slot;

        if (effect.SetLayouts.Length > slot && effect.SetLayouts[slot].IsValid) {
            Layout = effect.SetLayouts[slot];
        }

        return Layout.IsValid;
    }

    /// <summary>
    ///     Fills this view's block if it changed, and binds it.
    /// </summary>
    /// <param name="commands">Where to bind.</param>
    /// <param name="view">The view about to be drawn.</param>
    /// <returns>False when nothing was bound, which is what an unconfigured instance does.</returns>
    /// <remarks>
    ///     The view's matrix and position are written here rather than by the caller, because they are
    ///     facts about the view and a caller that had to remember to copy them is a caller that will
    ///     eventually draw a frame with last frame's camera.
    /// </remarks>
    public bool Bind(ICommandList commands, RenderView view) => Bind(commands, view, null);

    /// <summary>
    ///     The same, allocating the set against the layout of the pass about to be drawn.
    /// </summary>
    /// <param name="commands">Where to bind.</param>
    /// <param name="view">The view about to be drawn.</param>
    /// <param name="effect">The pass about to be drawn, or null to use <see cref="Layout" />.</param>
    /// <returns>False when nothing was bound.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One cached layout is not enough, and the reason is a Vulkan rule rather than a
    ///         handle-hygiene one.</b> A bound descriptor set counts for set <i>n</i> only when the
    ///         two pipeline layouts are identically defined <em>for every set up to n</em> — so
    ///         sharing set 1 between two shaders needs their set 0 to match as well. It does for
    ///         every camera pass in a frame, which is why one cached layout worked for a long time.
    ///         It does not for a shadow caster: that shader reads nothing per frame, so its set 0 is
    ///         empty where the shading pass's holds thirteen bindings, and a set 1 allocated from the
    ///         shading pass's layout is <em>not bound at all</em> as far as the caster's pipeline is
    ///         concerned. <c>uses set 1 but that set is not bound</c>, on a set that was just bound.
    ///     </para>
    ///     <para>
    ///         Taking the effect is what <c>SceneConstants.Bind</c> already does, and this is the same
    ///         answer: the set is allocated from the layout of the pipeline it is about to be bound
    ///         against, so the two agree by construction. <see cref="Layout" /> stays the answer for a
    ///         caller with no effect in hand — every device test builds one by hand.
    ///     </para>
    /// </remarks>
    public bool Bind(ICommandList commands, RenderView view, Effect? effect) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(view);
        ObjectDisposedException.ThrowIf(disposed, this);

        var slot = (int)Slot;

        var layout = effect is not null && effect.SetLayouts.Length > slot && effect.SetLayouts[slot].IsValid
            ? effect.SetLayouts[slot]
            : Layout;

        if (Descriptors is null || !layout.IsValid || Size <= 0) {
            return false;
        }

        var block = BlockFor(view);

        block.Parameters.Set(ViewProjection, view.ViewProjection);
        block.Parameters.Set(ViewPosition, view.Position);

        // Only from a view that carries a camera. A cascade or a probe face has a view-projection and
        // no camera to derive a view matrix from, and writing an identity for it would be worse than
        // leaving the member alone: a clustered fragment would place itself in world space and read
        // whichever froxel that landed in.
        if (view.Camera is { } camera) {
            block.Parameters.Set(View, camera.View);
        }

        var members = Members.ToArray();

        if (!block.Constants.Update(this, Size, members, block.Parameters)) {
            return false;
        }

        var set = Descriptors.Allocate(
            layout,
            [DescriptorWrite.Uniform(Binding, block.Constants.Buffer, block.Constants.Offset, Size)]
        );

        commands.BindDescriptorSet(Slot, set);
        return true;
    }

    /// <summary>Forgets a view's block, for a view that has gone away.</summary>
    public bool Forget(RenderView view) {
        ArgumentNullException.ThrowIfNull(view);

        if (!blocks.Remove(view, out var block)) {
            return false;
        }

        block.Constants.Dispose();
        return true;
    }

    /// <summary>
    ///     The bytes one view's block was last filled with, for checking they are the right ones.
    /// </summary>
    /// <param name="view">The view.</param>
    /// <returns>The bytes, or empty for a view that has never been bound.</returns>
    /// <remarks>
    ///     The same argument <see cref="EffectConstants.Bytes" /> makes: a device that took the bytes
    ///     cannot be asked what they were, so the only way to check a member landed at the offset the
    ///     reflection named is to look at what was sent. A set that binds and a set that holds the
    ///     right matrix are two different claims, and a frame in which the second is false looks
    ///     exactly like a frame in which the geometry is somewhere else.
    /// </remarks>
    public ReadOnlySpan<byte> BytesFor(RenderView view) {
        ArgumentNullException.ThrowIfNull(view);

        return blocks.TryGetValue(view, out var block) ? block.Constants.Bytes : default;
    }

    Block BlockFor(RenderView view) {
        if (blocks.TryGetValue(view, out var existing)) {
            return existing;
        }

        var created = new Block(new(device, $"{name}.{view.Name}"), new());
        blocks[view] = created;
        return created;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        foreach (var block in blocks.Values) {
            block.Constants.Dispose();
        }

        blocks.Clear();
    }

    /// <summary>One view's buffer and the values in it.</summary>
    readonly record struct Block(EffectConstants Constants, ParameterCollection Parameters);
}
