// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;

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

/// <summary>What each view mode means to the editor's own tool renderer.</summary>
/// <remarks>
///     <para>
///         <b>Two answers to "show me the normals" and this is the near one.</b>
///         <see cref="ViewModes" /> is the real one — a mode is a compositor tree, which is what doc
///         06 made the compositor data for — and it needs the viewport to be driven by
///         <c>RenderSystem</c> through a <c>GraphicsCompositor</c>, which is Phase 7's material-system
///         wiring rather than this document's. What the editor draws today is
///         <c>SceneMeshes</c> through <c>MeshInstanceRenderer</c>: device-resident shapes, one instance
///         per entity, one colour and one directional term, and no material anywhere. This table is
///         what that path can honestly express.
///     </para>
///     <para>
///         ⚠ <b>The three it cannot express say so rather than falling back.</b>
///         <see cref="ViewModes.Resolve" /> falls back to shaded for a mode with no tree registered,
///         which is right for a compositor that has not been authored and wrong for a menu item: a
///         line that draws the same picture as the line above it is a control the user tries twice
///         and then stops trusting. <see cref="IsSupported" /> is what lets the Scene menu register
///         those three as declared-and-disabled with the reason, which is doc 20's first bar.
///     </para>
/// </remarks>
public static class ViewShading {
    /// <summary>Whether the editor's tool renderer can draw a mode at all.</summary>
    /// <remarks>
    ///     <para>
    ///         Roughness needs a material to read one off, and there are none: the shape colour is a
    ///         constant chosen by <c>SceneMeshes</c>. Light complexity needs the tiled light list,
    ///         which belongs to the clustered path. Overdraw needs an additive blend with the depth
    ///         test off, which is a third pipeline in <c>MeshInstanceRenderer</c> — a change to a
    ///         shipping renderer in <c>Vixen.Rendering</c> for a debug view of an editor path, made
    ///         obsolete by the compositor swap that replaces the whole of this.
    ///     </para>
    /// </remarks>
    /// <param name="mode">The mode.</param>
    /// <returns>Whether it draws something the mode means.</returns>
    public static bool IsSupported(ViewMode mode) =>
        mode is not (ViewMode.Roughness or ViewMode.Overdraw or ViewMode.LightComplexity);

    /// <summary>Whether a mode draws surfaces.</summary>
    public static bool DrawsSurfaces(ViewMode mode) => mode != ViewMode.Wireframe;

    /// <summary>Whether a mode draws the edges of those surfaces.</summary>
    public static bool DrawsEdges(ViewMode mode) => mode is ViewMode.Wireframe or ViewMode.ShadedWireframe;

    /// <summary>How much light a surface facing away from the key still receives.</summary>
    /// <remarks>
    ///     ⚠ <b>One for the three modes that are about the surface's own value rather than about its
    ///     shape.</b> Shading an albedo view is showing the light and the albedo multiplied together,
    ///     which is the picture the mode exists to take apart.
    /// </remarks>
    /// <param name="mode">The mode.</param>
    /// <param name="shaded">What the renderer's own ambient term is, for the modes that keep it.</param>
    /// <returns>The ambient term to draw with.</returns>
    public static float AmbientFor(ViewMode mode, float shaded) =>
        mode is ViewMode.Unlit or ViewMode.Albedo or ViewMode.Normal ? 1f : shaded;

    /// <summary>Whether a surface is coloured by its normal rather than by what it is.</summary>
    /// <remarks>
    ///     ⚠ <b>A style lane on the instance rather than a shader of its own.</b> The vertex stage
    ///     already computes the world normal, so a normal view is one flag on a
    ///     <c>MeshInstance</c> and a branch in a stage that is already running — no new module, no new
    ///     pipeline and no new push constant. It used to be a colour written per vertex at collect
    ///     time, which was the same trade before there were no vertices here to write one on.
    /// </remarks>
    /// <param name="mode">The mode.</param>
    /// <returns>Whether it is.</returns>
    public static bool ColoursByNormal(ViewMode mode) => mode == ViewMode.Normal;

    /// <summary>Whether a mode ignores the selection's colour.</summary>
    /// <remarks>
    ///     ⚠ <b>A normal view has to.</b> Painting the selected object orange in a view whose whole
    ///     content is "this pixel's colour <i>is</i> the normal" makes the one object being looked at
    ///     the one the view cannot answer for. The outline still marks it, which is what the outline
    ///     is for.
    /// </remarks>
    /// <param name="mode">The mode.</param>
    /// <returns>Whether it does.</returns>
    public static bool IgnoresSelectionColour(ViewMode mode) => mode == ViewMode.Normal;

    /// <summary>What a mode is called in a menu.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>Its label.</returns>
    public static string NameOf(ViewMode mode) =>
        mode switch {
            ViewMode.ShadedWireframe => "Shaded Wireframe",
            ViewMode.LightComplexity => "Light Complexity",
            _ => mode.ToString()
        };

    /// <summary>What a mode is called in a command id.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>A lower-case, hyphenated name.</returns>
    public static string SlugOf(ViewMode mode) =>
        mode switch {
            ViewMode.ShadedWireframe => "shaded-wireframe",
            ViewMode.LightComplexity => "light-complexity",
            _ => mode.ToString().ToLowerInvariant()
        };

    /// <summary>Every mode, in the order the View Mode menu offers them.</summary>
    public static IReadOnlyList<ViewMode> All { get; } = [
        ViewMode.Shaded,
        ViewMode.ShadedWireframe,
        ViewMode.Wireframe,
        ViewMode.Unlit,
        ViewMode.Albedo,
        ViewMode.Normal,
        ViewMode.Roughness,
        ViewMode.Overdraw,
        ViewMode.LightComplexity
    ];
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
