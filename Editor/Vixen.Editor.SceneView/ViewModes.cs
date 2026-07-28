// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.SceneView;

/// <summary>What a viewport is showing.</summary>
/// <remarks>
///     Every one of these is the scene drawn a different way rather than a different picture with an
///     overlay on it, which is what makes them all reachable by swapping the compositor rather than by
///     adding a branch to the renderer.
/// </remarks>
public enum ViewMode {
    /// <summary>The scene as it will ship.</summary>
    Shaded,

    /// <summary>Edges only.</summary>
    Wireframe,

    /// <summary>Shaded, with the edges over it.</summary>
    ShadedWireframe,

    /// <summary>Materials with no lighting.</summary>
    Unlit,

    /// <summary>Base colour alone.</summary>
    Albedo,

    /// <summary>Shading normals as colour.</summary>
    Normal,

    /// <summary>Roughness as greyscale.</summary>
    Roughness,

    /// <summary>How many times each pixel was written.</summary>
    Overdraw,

    /// <summary>How many lights each pixel was shaded by.</summary>
    LightComplexity
}

/// <summary>The compositor a viewport uses for each way of looking at the scene.</summary>
/// <remarks>
///     <para>
///         <b>A mode is a compositor, not a flag.</b> Doc 06 made the compositor data precisely so
///         that "show me the normals" is a different tree rather than a branch inside the renderer,
///         and this is where that is collected. A host registers the trees it has; a mode with no
///         tree registered falls back to <see cref="ViewMode.Shaded" /> rather than showing nothing,
///         because a menu item that appears to do nothing is worse than one that does the safe thing.
///     </para>
///     <para>
///         <b>The two modes that are not a different tree are here as stage state.</b> Wireframe and
///         overdraw are the same geometry drawn with a different rasterizer and a different blend, so
///         a host that has not authored a compositor for them can have them by handing over the stage
///         they draw — which is what <see cref="ApplyTo" /> is for. Everything else genuinely needs a
///         different shader and therefore a different tree.
///     </para>
/// </remarks>
public sealed class ViewModes {
    readonly Dictionary<ViewMode, SceneRenderer> trees = [];
    ViewMode current = ViewMode.Shaded;

    /// <summary>Which mode the viewport is in.</summary>
    public ViewMode Current {
        get => current;

        set {
            if (current == value) {
                return;
            }

            current = value;
            Changed?.Invoke(this, value);
        }
    }

    /// <summary>The modes a compositor has been registered for.</summary>
    public IReadOnlyCollection<ViewMode> Registered => trees.Keys;

    /// <summary>Raised when the mode changes.</summary>
    public event Action<ViewModes, ViewMode>? Changed;

    /// <summary>Registers the compositor tree for a mode.</summary>
    /// <param name="mode">The mode.</param>
    /// <param name="renderer">The tree.</param>
    public void Register(ViewMode mode, SceneRenderer renderer) {
        ArgumentNullException.ThrowIfNull(renderer);

        trees[mode] = renderer;
    }

    /// <summary>The tree for the current mode.</summary>
    /// <returns>The tree, or the shaded one, or <see langword="null" /> if neither is registered.</returns>
    public SceneRenderer? Resolve() => Resolve(Current);

    /// <summary>The tree for a mode.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The tree, or the shaded one, or <see langword="null" /> if neither is registered.</returns>
    public SceneRenderer? Resolve(ViewMode mode) =>
        trees.TryGetValue(mode, out var renderer) ? renderer
        : trees.TryGetValue(ViewMode.Shaded, out var shaded) ? shaded
        : null;

    /// <summary>Puts the current mode's stage state onto a stage.</summary>
    /// <param name="stage">The stage the scene's geometry is drawn in.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This mutates the stage, and the stage is shared.</b> A viewport that set this and
    ///         a second viewport in shaded mode would both draw wireframe, because a stage belongs to
    ///         the render system rather than to a view. A four-pane layout with independent render
    ///         modes therefore needs a stage per pane, which is what <see cref="ViewportLayout" />
    ///         does — said here because the alternative fails silently and only in the layout nobody
    ///         tests first.
    ///     </para>
    ///     <para>
    ///         Overdraw is additive with the depth test off, so every fragment that <i>would</i> have
    ///         been drawn adds to the total rather than only the ones that survived — which is the
    ///         question being asked. Testing depth would draw the answer to a different question,
    ///         and one that is always "one".
    ///     </para>
    /// </remarks>
    public void ApplyTo(RenderStage stage) {
        ArgumentNullException.ThrowIfNull(stage);

        switch (Current) {
            case ViewMode.Wireframe:
            case ViewMode.ShadedWireframe:
                stage.Rasterizer = stage.Rasterizer with { Fill = FillMode.Wireframe, Cull = CullMode.None };
                stage.Blend = BlendState.Opaque;
                stage.DepthStencil = DepthStencilState.Default;

                break;

            case ViewMode.Overdraw:
                stage.Rasterizer = RasterizerState.Default with { Cull = CullMode.None };
                stage.Blend = BlendState.Additive;
                stage.DepthStencil = DepthStencilState.Disabled;

                break;

            default:
                stage.Rasterizer = RasterizerState.Default;
                stage.Blend = BlendState.Opaque;
                stage.DepthStencil = DepthStencilState.Default;

                break;
        }
    }
}
