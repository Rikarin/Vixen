// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.Plugin;
using Vixen.Editor.TextureGraph;

namespace Vixen.Editor.Texturing;

/// <summary>Turns a graph into pixels on the editor's device, and puts them where the pane draws them.</summary>
/// <remarks>
///     <para>
///         <b>The first thing outside a test that dispatches a texture kernel, and the first
///         production caller <c>ImageView</c> has had.</b> Four batches built an evaluator and
///         forty-five kernels; this is where the editor runs one. It is also what proves
///         <see cref="IEditorGraphics" /> is sufficient rather than merely published — a contract no
///         plugin can draw through is the same gap doc 36 § F2 was written to find, one layer along.
///     </para>
///     <para>
///         ⚠ <b>What it evaluates is the graph's <i>base layer</i>, not the wired graph, and the
///         status line says so.</b> A pane that showed a made-up thumbnail would hide that; a pane
///         that stayed empty would hide whether the device half works at all. What it shows is a real
///         dispatch at the document's own resolution, and a sentence naming what is missing.
///     </para>
///     <para>
///         ⚠ <b>The reason written here was false, and it is the reason a closed issue kept being
///         cited — <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a>.</b> This said
///         <c>TextureGraphCompiler</c> was <c>internal</c> and that "nothing here can turn a canvas
///         into a <see cref="TexturePlan" />". It has been public since
///         <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a>, and two things in this
///         very assembly turn a canvas into a plan through it: <c>TextureGraphDocument.Compile</c>
///         and <c>LayerStackCompiler</c>. What is missing is one call — <see cref="Evaluate" />
///         builds <see cref="Base" /> and never asks the document for its plan, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a> and needs the external
///         upload and resolve loop <c>LayerStackPreview</c> already has.
///     </para>
///     <para>
///         ⚠ <b>The evaluator is held across evaluations and that is the reason
///         <see cref="IEditorGraphics" /> lends a device rather than a call.</b> It caches one
///         compiled pipeline per kernel and output format, so an evaluator built per preview would
///         recompile every kernel the plan touches on every keystroke.
///     </para>
///     <para>
///         ⚠ <b>Never call this from inside the host's own frame.</b>
///         <c>TexturePlanEvaluator.Evaluate</c> drives <c>BeginFrame</c>, <c>EndFrame</c> and
///         <c>WaitIdle</c> on the device itself — a nested pair would reset the pool of a slot the
///         editor still has commands executing in. Every route into this class is a command handler
///         or a panel build, both of which run from <c>EditorApplication.Update</c>, which is
///         outside <c>EditorHost.Present</c>'s pair; that is the same place <c>ThumbnailCache.Pump</c>
///         runs and for the same reason.
///     </para>
/// </remarks>
sealed class TextureGraphPreview : IDisposable {
    /// <summary>The checker's period, in cells across the image.</summary>
    /// <remarks>
    ///     A count rather than a texel size, so the base layer is the same picture at every
    ///     resolution — which is the property doc 48 § D8 asks of every node and the cheapest place
    ///     to demonstrate it.
    /// </remarks>
    public const float Cells = 8f;

    readonly IEditorGraphics graphics;

    TexturePlanEvaluator? evaluator;
    IEditorImage? shown;

    /// <summary>Builds a preview over the graphics a host lent the plugin.</summary>
    /// <param name="graphics">The host's graphics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graphics" /> is null.</exception>
    public TextureGraphPreview(IEditorGraphics graphics) {
        ArgumentNullException.ThrowIfNull(graphics);

        this.graphics = graphics;
    }

    /// <summary>How many plans have been evaluated over this preview's life.</summary>
    /// <remarks>
    ///     A counter rather than a flag, because the defect it counts against — a pane that shows a
    ///     stale picture — leaves every structural claim about the image true and only this one
    ///     false.
    /// </remarks>
    public int Evaluations { get; private set; }

    /// <summary>The plan a document's base layer is, at the resolution it is authored at.</summary>
    /// <param name="width">The base width, in texels.</param>
    /// <param name="height">The base height.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An extent is not positive.</exception>
    /// <remarks>
    ///     <c>Rgba8</c> rather than the widest thing a plan may ask for: the pane's destination is an
    ///     eight-bit-per-channel upload, so evaluating at half float would be precision the readback
    ///     throws away in the next line.
    /// </remarks>
    public static TexturePlan Base(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        return new TexturePlan {
            BaseWidth = width,
            BaseHeight = height,
            Images = [new(TextureFormat.Rgba8)],
            Ops = [
                new TextureOp {
                    Kernel = "Checker",
                    Output = 0,
                    // ⚠ Every uniform the kernel declares, including the three this plan does not
                    // care about. `TexturePlanEvaluator.Uniforms` refuses a plan that leaves one out
                    // rather than writing zero for it, because zero is a valid-looking number for
                    // almost every parameter in the catalogue — the trap this repository files under
                    // "zero often means off".
                    Parameters = ImmutableArray.Create(
                        new TextureParameter("scaleX", Cells),
                        new TextureParameter("scaleY", Cells),
                        new TextureParameter("rotation", 0f),
                        new TextureParameter("offsetX", 0f),
                        new TextureParameter("offsetY", 0f)
                    )
                }
            ],
            Outputs = [0]
        };
    }

    /// <summary>Evaluates a document's base layer and hands back the picture.</summary>
    /// <param name="document">The graph.</param>
    /// <returns>The image, or <see langword="null" /> when this host cannot make one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>The previous image is released here rather than by the caller.</b> One live upload
    ///     per preview: a pane re-evaluated on every edit would otherwise hold a texture and a
    ///     descriptor set per keystroke, which is the leak <c>ThumbnailCache</c>'s ceiling exists to
    ///     stop and this has no ceiling.
    /// </remarks>
    public IEditorImage? Evaluate(TextureGraphDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (graphics.Device is not { } device) {
            return null;
        }

        // ⚠ Built on the first evaluation rather than in the constructor, because the constructor
        // runs while the host may still have no device — see `PluginGraphics` — and an evaluator is
        // bound to the device it was made on for the life of its pipeline cache.
        evaluator ??= new TexturePlanEvaluator(device);

        var plan = Base(document.BaseWidth, document.BaseHeight);

        using var bake = evaluator.Evaluate(plan);

        var picture = bake.Read(plan.Outputs[0]);
        var image = graphics.Upload(picture.Width, picture.Height, picture.Pixels);

        shown?.Dispose();
        shown = image;

        Evaluations++;

        return image;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Both halves: the picture, so the editor stops holding a texture for a plugin that has
    ///     gone, and the evaluator, so its pipelines and modules are destroyed on the device that
    ///     made them.
    /// </remarks>
    public void Dispose() {
        shown?.Dispose();
        shown = null;

        evaluator?.Dispose();
        evaluator = null;
    }
}
