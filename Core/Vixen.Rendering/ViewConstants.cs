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
    ///     Eighty by default: a <c>mat4</c> and a <c>float3</c> under std140, which puts the position
    ///     at 64 and rounds the block to 80. A project that adds members sets this to match.
    /// </remarks>
    public int Size { get; set; } = 80;

    /// <summary>Where each value goes in the block.</summary>
    public IList<EffectParameter> Members { get; } = [
        new(ViewProjection, 0, 64),
        new(ViewPosition, 64, 12)
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
    public bool Bind(ICommandList commands, RenderView view) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(view);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!IsConfigured) {
            return false;
        }

        var block = BlockFor(view);

        block.Parameters.Set(ViewProjection, view.ViewProjection);
        block.Parameters.Set(ViewPosition, view.Position);

        var members = Members.ToArray();

        if (!block.Constants.Update(this, Size, members, block.Parameters)) {
            return false;
        }

        var set = Descriptors!.Allocate(
            Layout,
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
