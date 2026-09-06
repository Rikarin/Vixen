// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Plugin;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;

namespace Vixen.Editor.Texturing;

/// <summary>Lends the one evaluator a module holds to a pane that is about to dispatch.</summary>
/// <param name="device">The device the pane found on the host, this evaluation.</param>
/// <returns>The evaluator, which the caller does not own.</returns>
/// <remarks>
///     <para>
///         ⚠ <b>A lease and not a constructor argument, because the device arrives after the module
///         does.</b> <c>EditorApplication</c> builds its plugin host in its constructor and acquires a
///         device when the window can present — <a href="https://github.com/Rikarin/Vixen/issues/737">#737</a>
///         — so a pane handed an evaluator at activation would be handed one built on nothing.
///     </para>
///     <para>
///         ⚠ <b>The caller does not own it and must not dispose it</b>, which is the trap
///         <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a> records: both previews used
///         to dispose an evaluator of their own, so a shared one freed by the first pane closed would
///         destroy the pipelines the second is still dispatching through — a use-after-free on the
///         device rather than a slow first open.
///     </para>
/// </remarks>
delegate TexturePlanEvaluator TextureEvaluatorLease(IGraphicsDevice device);

/// <summary>What one attempt at a graph's picture produced, and the sentence that goes under it.</summary>
/// <param name="Image">The picture, or <see langword="null" /> when there is none.</param>
/// <param name="Status">What to say under the pane.</param>
/// <remarks>
///     ⚠ <b>No extent, unlike <see cref="LayerStackPicture" />, and the difference is real.</b> A
///     stack's authored size lives in the file it came from and a graph's lives on the open document
///     — <c>TextureGraphDocument.BaseWidth</c>, which the compiler is handed — so the pane already
///     has it and a second copy here could disagree with the one the plan was built at.
/// </remarks>
sealed record TextureGraphPicture(IEditorImage? Image, string Status) {
    /// <summary>What compiling the graph had to say, about nodes.</summary>
    /// <remarks>
    ///     ⚠ <b>Carried whether or not there is a picture</b>, for <c>LayerStackPicture</c>'s reason:
    ///     <see cref="Status" /> answers "why is there no map", which is a question with one answer,
    ///     and a compilation that produced a plan can still have plenty to say. Until this type
    ///     existed no production reader of a texture diagnostic existed at all on the graph side —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a>.
    /// </remarks>
    public ImmutableArray<NodeDiagnostic> Diagnostics { get; init; } = [];
}

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
///         ⚠ <b>It evaluates the graph the author wired, and for three batches it evaluated a fixed
///         checkerboard instead</b> — <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>
///         and <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a>. The reason written
///         here for that was <em>false</em>: it said <c>TextureGraphCompiler</c> was <c>internal</c>
///         and that "nothing here can turn a canvas into a <see cref="TexturePlan" />". It has been
///         public since <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a>, and the
///         missing piece was one call plus the external upload loop
///         <c>LayerStackPreview</c> already had — which is now
///         <see cref="TextureExternalImages" />, shared rather than copied.
///     </para>
///     <para>
///         ⚠ <b><see cref="Base" /> stays, and it is no longer what the pane shows.</b> A plan that
///         needs no compiler, no library and no project is what a device test uses to ask whether
///         the evaluate-upload-draw path works at all; folding it away would leave every failure of
///         that path looking like a failure of the compiler.
///     </para>
///     <para>
///         ⚠ <b>The evaluator is held across evaluations and that is the reason
///         <see cref="IEditorGraphics" /> lends a device rather than a call.</b> It caches one
///         compiled pipeline per kernel and output format, so an evaluator built per preview would
///         recompile every kernel the plan touches on every keystroke — and it is held by the
///         <em>module</em> rather than here, because two panes over one device were two of those
///         caches (<a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>).
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
    readonly TextureEvaluatorLease evaluators;

    IEditorImage? shown;

    /// <summary>Builds a preview over the graphics a host lent the plugin.</summary>
    /// <param name="graphics">The host's graphics.</param>
    /// <param name="evaluators">Where the one evaluator comes from — see <see cref="TextureEvaluatorLease" />.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public TextureGraphPreview(IEditorGraphics graphics, TextureEvaluatorLease evaluators) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(evaluators);

        this.graphics = graphics;
        this.evaluators = evaluators;
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

    /// <summary>Compiles a document's graph and evaluates the map it writes.</summary>
    /// <param name="document">The graph.</param>
    /// <returns>The picture and what to say under it, never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every refusal comes back as a sentence rather than an exception, including the
    ///         ones that are this build's fault</b> — <c>LayerStackPreview.Evaluate</c>'s rule
    ///         unchanged. A preview runs on every edit; a throw out of one is a throw out of a panel
    ///         build, which takes the editor's frame with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Compiled before the device is asked for, and the order is the finding.</b>
    ///         <c>TextureGraphDocument.Compile</c> allocates no texture and dispatches nothing, so
    ///         everything it has to say about an author's graph costs exactly as much on a host that
    ///         cannot draw. Asked the other way round, an editor between construction and its window
    ///         coming up would answer every mistake with a message about the window.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first output, and the sentence names which map it is.</b> A graph may write
    ///         several — one <c>Output</c> node per usage — and a pane showing one of them silently
    ///         would be a pane whose picture changes meaning when a node is added. Which one to show
    ///         is a control this panel does not have yet.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The previous image is released here rather than by the caller.</b> One live
    ///         upload per preview: a pane re-evaluated on every edit would otherwise hold a texture
    ///         and a descriptor set per keystroke, which is the leak <c>ThumbnailCache</c>'s ceiling
    ///         exists to stop and this has no ceiling.
    ///     </para>
    /// </remarks>
    public TextureGraphPicture Evaluate(TextureGraphDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var compilation = document.Compile();

        TextureGraphPicture Said(IEditorImage? drawn, string status) =>
            new(drawn, status) { Diagnostics = compilation.Diagnostics };

        // ⚠ Before the device, because a graph that does not compile does not compile on any host.
        if (compilation.Plan is not { } plan) {
            return Said(null, Refused(compilation));
        }

        if (graphics.Device is not { } device) {
            return Said(null, TexturePreview.Describe(TexturePreview.Blocking(graphics)));
        }

        if (compilation.Outputs.Length == 0) {
            return Said(
                null,
                "No preview: this graph writes no map. An Output node is what names a usage and makes "
                + "an image the bake writes, and there is none here."
            );
        }

        var output = compilation.Outputs[0];

        // ⚠ Asked for on every evaluation and owned by nobody here — #820. The module holds one for
        // both panes, because an evaluator is a pipeline cache per kernel and output format and two
        // of them over one device compile the whole overlap twice.
        var evaluator = evaluators(device);

        using TextureUploads uploads = new(device);

        // ⚠ The same loop the layers pane runs, and one loop rather than two copies — see
        // `TextureExternalImages`. A `Source/Bitmap` in a graph names a project asset exactly as a
        // texture layer does, so a second copy here would be the copy that forgot a case.
        var unresolved = TextureExternalImages.Fill(
            document.Project,
            document.AssetPath,
            uploads,
            plan,
            compilation.Externals
        );

        if (unresolved.Count > 0) {
            return Said(null, "No preview: " + string.Join(" · ", unresolved) + " Everything else compiled.");
        }

        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(output.Image);
        var image = graphics.Upload(picture.Width, picture.Height, picture.Pixels);

        shown?.Dispose();
        shown = image;

        Evaluations++;

        // The plan's cautions, on `LayerStackPreview`'s argument: a caution is a plan that bakes and
        // does not draw what the graph describes, and it reached `TextureBake.Warnings` and stopped.
        var cautions = bake.Warnings.Length > 0
            ? " ⚠ " + string.Join(" · ", bake.Warnings)
            : "";

        return Said(
            image,
            $"Preview: '{output.Usage}', compiled from this graph and evaluated on the editor's device."
            + cautions
        );
    }

    /// <summary>What to say when the compilation refused.</summary>
    /// <param name="compilation">It.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>Errors only, because this sentence answers "why is there no map"</b> — and a warning
    ///     is precisely a thing that did not stop the map. The warnings still travel, on
    ///     <see cref="TextureGraphPicture.Diagnostics" />, which is where a panel lists them whether
    ///     or not there is a picture. That division of labour is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a>'s, one type over.
    /// </remarks>
    static string Refused(TextureGraphCompilation compilation) {
        var problems = compilation.Diagnostics
            .Where(one => one.Severity == NodeSeverity.Error)
            .Select(one => one.Id + ": " + one.Message)
            .ToArray();

        return problems.Length == 0
            ? "No preview: this graph did not compile, and nothing said why — which is a compiler bug rather than yours."
            : "No preview: " + string.Join(" · ", problems);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The picture and not the evaluator.</b> The editor stops holding a texture for a plugin
    ///     that has gone — but the evaluator is the module's, lent to both panes, and a pane that
    ///     freed it would take the other pane's pipelines with it. <c>TexturingModule.Release</c> is
    ///     what disposes it, through the registration scope. #820.
    /// </remarks>
    public void Dispose() {
        shown?.Dispose();
        shown = null;
    }
}
