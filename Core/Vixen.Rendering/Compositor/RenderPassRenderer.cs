// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;

namespace Vixen.Rendering.Compositor;

/// <summary>One colour attachment a pass renders into, and the format a pipeline needs for it.</summary>
/// <param name="View">What to render into.</param>
/// <param name="Format">
///     Its format. Carried rather than asked of the view, because a
///     <see cref="TextureViewHandle" /> is an opaque index into a table the device owns — the RHI has
///     no introspection by design, and the pipeline needs the format.
/// </param>
/// <param name="Load">What to do with it at the start of the pass.</param>
/// <param name="Store">What to do with it at the end.</param>
/// <param name="ClearColour">What to clear to, when <paramref name="Load" /> clears.</param>
public readonly record struct ColourTargetBinding(
    TextureViewHandle View,
    PixelFormat Format,
    LoadAction Load = LoadAction.Clear,
    StoreAction Store = StoreAction.Store,
    Color4 ClearColour = default
) {
    /// <summary>This binding as the attachment a render pass takes.</summary>
    public ColourAttachment ToAttachment() => new(View, Load, Store, ClearColour);
}

/// <summary>The depth attachment a pass renders into.</summary>
/// <param name="View">What to render into.</param>
/// <param name="Format">Its format, for the same reason as
/// <see cref="ColourTargetBinding.Format" />.</param>
/// <param name="Load">What to do with depth at the start of the pass.</param>
/// <param name="Store">What to do with depth at the end.</param>
/// <param name="ClearDepth">
///     What to clear depth to. Zero is <em>far</em> under the engine's reversed-Z convention, which
///     is why it is the default — clearing to one is the classic mistake and depth-tests the scene
///     away entirely.
/// </param>
/// <param name="Texture">
///     The texture behind the view, for the one thing a view cannot do: be the source or destination
///     of a copy. A cached shadow atlas is copied rather than redrawn, and a copy names a texture.
///     Optional — a pass that is only rendered into never needs it.
/// </param>
public readonly record struct DepthTargetBinding(
    TextureViewHandle View,
    PixelFormat Format,
    LoadAction Load = LoadAction.Clear,
    StoreAction Store = StoreAction.Store,
    float ClearDepth = 0f,
    TextureHandle Texture = default
) {
    /// <summary>This binding as the attachment a render pass takes.</summary>
    public DepthStencilAttachment ToAttachment() => new(View, Load, Store, ClearDepth);

    /// <summary>This binding as the attachment, with a different load action.</summary>
    /// <remarks>
    ///     What a cached atlas needs: the same target, loaded rather than cleared, because what is
    ///     already in it is the point.
    /// </remarks>
    public DepthStencilAttachment ToAttachment(LoadAction load) => new(View, load, Store, ClearDepth);
}

/// <summary>
///     A render pass, and the renderers that draw into it.
/// </summary>
/// <remarks>
///     <para>
///         The node that resolves what <c>Vixen.Rendering</c>'s README used to leave to the caller:
///         a stage does not open a pass, because one pass draws several stages and one stage feeds
///         several passes. The compositor is where the two meet, and this is that node.
///     </para>
///     <para>
///         <strong>There is no separate "clear" renderer, and that is not an omission.</strong>
///         Clearing is a load action on an attachment. Issuing it as its own operation is a D3D11-ism
///         that on a tile-based GPU costs an extra full-screen pass writing a colour that the next
///         pass immediately overwrites — the exact opposite of what a mobile-first renderer wants.
///     </para>
///     <para>
///         The <see cref="RenderOutput" /> is computed from the bindings and put on the draw context,
///         so every pipeline built inside this pass is built for the formats the pass actually has.
///         That is the link that lets <see cref="Features.MeshRenderFeature" /> stop taking a
///         pipeline-description callback from its host.
///     </para>
/// </remarks>
public sealed class RenderPassRenderer : SceneRenderer {
    ColourAttachment[] attachments = [];
    RenderOutput output;
    bool outputStale = true;

    /// <summary>The colour attachments, in the order the shader writes them.</summary>
    public IList<ColourTargetBinding> ColourTargets { get; } = [];

    /// <summary>The depth attachment, or null for a pass with none.</summary>
    public DepthTargetBinding? DepthTarget { get; set; }

    /// <summary>How many samples the attachments have.</summary>
    public int SampleCount { get; set; } = 1;

    /// <summary>The viewport to set, or null to leave whatever was set before.</summary>
    public Viewport? Viewport { get; set; }

    /// <summary>What draws into this pass.</summary>
    public IList<SceneRenderer> Children { get; } = [];

    /// <summary>Marks the attachments as changed, so the next frame recomputes the output.</summary>
    /// <remarks>
    ///     Called by a host that swapped an attachment — a new swapchain image of the same format
    ///     does not need it, a resize to a different format does. Cheap enough to call every frame.
    /// </remarks>
    public void Invalidate() => outputStale = true;

    /// <summary>The formats this pass renders into.</summary>
    public RenderOutput Output {
        get {
            if (outputStale) {
                Rebuild();
            }

            return output;
        }
    }

    /// <inheritdoc />
    protected internal override void Collect(GraphicsCompositor compositor) {
        foreach (var child in Children) {
            if (child.Enabled) {
                child.Collect(compositor);
            }
        }
    }

    /// <inheritdoc />
    protected internal override void Draw(GraphicsCompositor compositor, RenderDrawContext context) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(context);

        if (outputStale || attachments.Length != ColourTargets.Count) {
            Rebuild();
        }

        var list = context.CommandList;
        var previous = context.Output;
        context.Output = output;

        list.PushDebugGroup(ToString());
        list.BeginRenderPass(new(attachments, DepthTarget?.ToAttachment(), Name));

        if (Viewport is { } viewport) {
            list.SetViewport(viewport);
            list.SetScissor(new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height));
        }

        foreach (var child in Children) {
            if (child.Enabled) {
                child.Draw(compositor, context);
            }
        }

        list.EndRenderPass();
        list.PopDebugGroup();

        // Restored rather than cleared, so a pass nested inside another leaves the outer one's
        // formats in place for whatever draws after it.
        context.Output = previous;
    }

    void Rebuild() {
        if (attachments.Length != ColourTargets.Count) {
            attachments = new ColourAttachment[ColourTargets.Count];
        }

        Span<PixelFormat> formats = ColourTargets.Count <= 8
            ? stackalloc PixelFormat[ColourTargets.Count]
            : new PixelFormat[ColourTargets.Count];

        for (var i = 0; i < ColourTargets.Count; i++) {
            attachments[i] = ColourTargets[i].ToAttachment();
            formats[i] = ColourTargets[i].Format;
        }

        output = new(formats, DepthTarget?.Format ?? PixelFormat.Undefined, SampleCount);
        outputStale = false;
    }
}
