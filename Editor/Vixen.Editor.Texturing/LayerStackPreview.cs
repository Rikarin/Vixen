// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;

namespace Vixen.Editor.Texturing;

/// <summary>What one attempt at a stack's picture produced, and the sentence that goes under it.</summary>
/// <param name="Image">The picture, or <see langword="null" /> when there is none.</param>
/// <param name="Usage">Which map it is.</param>
/// <param name="Width">The stack's authored width, whether or not there is a picture.</param>
/// <param name="Height">Its height.</param>
/// <param name="Status">What to say under the pane.</param>
/// <remarks>
///     ⚠ <b>The extent is carried even when the image is null, and that is the same decision
///     <c>TextureGraphView.Show</c> makes.</b> The zoom, the fit and the pointer readout are about
///     the texels an author is authoring; a pane that lost them when a bake failed would rescale
///     itself every time somebody typed a bad number into a layer.
/// </remarks>
sealed record LayerStackPicture(IEditorImage? Image, string Usage, int Width, int Height, string Status);

/// <summary>Turns a layer stack into pixels on the editor's device.</summary>
/// <remarks>
///     <para>
///         <b>The same path a graph takes, which is doc 48 § D1's whole claim and is now checkable
///         rather than asserted.</b> A stack becomes a <c>NodeGraphModel</c>
///         (<see cref="LayerStackGraph" />), the graph becomes a <see cref="TexturePlan" /> through
///         the <em>public</em> <c>TextureGraphCompiler</c>, and the plan goes to the one
///         <c>TexturePlanEvaluator</c>. There is no second evaluator, no second emitter and no
///         arithmetic here for either to disagree with.
///     </para>
///     <para>
///         ⚠ <b>This is what <c>TextureGraphPreview</c> was written before it could do.</b> That
///         type still evaluates a fixed checkerboard and its status line still cites
///         <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a> — "the compiler is
///         internal, so this plugin can offer every node and compile none of them". #738 is closed
///         and <c>TextureGraphCompiler</c> is public: <see cref="LayerStackCompiler" /> in this very
///         assembly compiles through it. The graph pane is showing a checkerboard and a message
///         naming a closed issue, which is filed rather than fixed here because the file is not this
///         slice's.
///     </para>
///     <para>
///         ⚠ <b>An imported image is the one thing this cannot fill, and it is reported rather than
///         skipped.</b> <c>TextureGraphExternals.Upload</c> hands back the entries naming an asset,
///         and an evaluation missing exactly one texture throws about an image index rather than
///         drawing a map with a hole in it. Resolving them wants an <c>AssetDatabase</c> read on the
///         panel's thread, which is <a href="https://github.com/Rikarin/Vixen/issues/818">#818</a>;
///         until then the pane says which layer's picture it is short of.
///     </para>
///     <para>
///         ⚠ <b>Never called from inside the host's own frame</b>, for
///         <c>TextureGraphPreview</c>'s reason unchanged: <c>TexturePlanEvaluator.Evaluate</c> drives
///         <c>BeginFrame</c>, <c>EndFrame</c> and <c>WaitIdle</c> on the device itself. Every route
///         here is a command handler or a panel build.
///     </para>
/// </remarks>
sealed class LayerStackPreview : IDisposable {
    /// <summary>Which map the pane shows when nothing else is asked for.</summary>
    public const string DefaultUsage = "baseColor";

    readonly IEditorGraphics graphics;

    TexturePlanEvaluator? evaluator;
    IEditorImage? shown;

    /// <summary>Builds a preview over the graphics a host lent the plugin.</summary>
    /// <param name="graphics">The host's graphics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="graphics" /> is null.</exception>
    public LayerStackPreview(IEditorGraphics graphics) {
        ArgumentNullException.ThrowIfNull(graphics);

        this.graphics = graphics;
    }

    /// <summary>How many plans have been evaluated over this preview's life.</summary>
    /// <remarks>
    ///     A counter rather than a flag, for <c>TextureGraphPreview.Evaluations</c>'s reason: the
    ///     defect it counts against is a pane showing a stale picture, which leaves every structural
    ///     claim about the image true and only this one false.
    /// </remarks>
    public int Evaluations { get; private set; }

    /// <summary>Compiles a stack's first texture set and evaluates one of its maps.</summary>
    /// <param name="document">The stack.</param>
    /// <param name="usage">Which map, or <see cref="DefaultUsage" />.</param>
    /// <returns>The picture and what to say under it, never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Every refusal comes back as a sentence rather than an exception, including the ones
    ///     that are this build's fault.</b> A preview runs on every edit; a throw out of one is a
    ///     throw out of a panel build, which takes the editor's frame with it. That is the same rule
    ///     <a href="https://github.com/Rikarin/Vixen/issues/805">#805</a> is about one layer down,
    ///     and it is why the terminus rule had to stop being a hard mismatch.
    /// </remarks>
    public LayerStackPicture Evaluate(LayerStackDocument document, string usage = DefaultUsage) {
        ArgumentNullException.ThrowIfNull(document);

        var stack = document.Document;
        var width = stack.BaseWidth;
        var height = stack.BaseHeight;

        if (stack.Sets.Count == 0) {
            return new(null, usage, width, height, "No preview: this stack has no texture set, so there is no map to bake.");
        }

        if (graphics.Device is not { } device) {
            return new(null, usage, width, height, TexturePreview.Describe(TexturePreview.Blocking(graphics)));
        }

        var compilation = LayerStackCompiler.Compile(stack, stack.Sets[0]);

        if (compilation.Plan is not { } plan) {
            return new(null, usage, width, height, Refused(compilation));
        }

        var image = -1;

        foreach (var output in compilation.Outputs) {
            if (string.Equals(output.Usage, usage, StringComparison.OrdinalIgnoreCase)) {
                image = output.Image;

                break;
            }
        }

        if (image < 0) {
            return new(
                null,
                usage,
                width,
                height,
                $"No preview: this stack writes no '{usage}' map. It writes "
                + $"{string.Join(", ", compilation.Outputs.Select(output => output.Usage))}."
            );
        }

        // ⚠ Built on the first evaluation rather than in the constructor, because the constructor
        // runs while the host may still have no device and an evaluator is bound to the device it
        // was made on for the life of its pipeline cache.
        evaluator ??= new TexturePlanEvaluator(device);

        using TextureUploads uploads = new(device);

        var owed = TextureGraphExternals.Upload(uploads, plan, compilation.Externals);

        if (owed.Length > 0) {
            return new(
                null,
                usage,
                width,
                height,
                $"No preview: {owed.Length} layer(s) read an imported image — {string.Join(", ", owed.Select(entry => entry.Asset))} "
                + "— and this pane resolves no assets yet (#818). Every other layer compiled."
            );
        }

        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var picture = bake.Read(image);
        var uploaded = graphics.Upload(picture.Width, picture.Height, picture.Pixels);

        // One live upload per preview: a pane re-evaluated on every edit would otherwise hold a
        // texture and a descriptor set per keystroke.
        shown?.Dispose();
        shown = uploaded;

        Evaluations++;

        return new(
            uploaded,
            usage,
            picture.Width,
            picture.Height,
            $"Preview: '{usage}', compiled from this stack and evaluated on the editor's device."
        );
    }

    /// <summary>What to say when the compilation refused.</summary>
    /// <param name="compilation">It.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>Both lists, because they are two readers' problems</b> —
    ///     <see cref="LayerStackCompilation" />'s own remarks. A layer problem names a row an artist
    ///     can select; a node diagnostic names a node in a graph nobody has exploded and is a
    ///     compiler's or a builder's fault. A pane that showed only the first would be silent on
    ///     every failure of the second.
    /// </remarks>
    static string Refused(LayerStackCompilation compilation) {
        var problems = compilation.Problems.Select(problem => problem.Message)
            .Concat(compilation.Diagnostics.Where(one => one.Severity == NodeGraph.NodeSeverity.Error)
                .Select(one => one.Id + ": " + one.Message))
            .ToArray();

        return problems.Length == 0
            ? "No preview: this stack did not compile, and nothing said why — which is a compiler bug rather than yours."
            : "No preview: " + string.Join(" · ", problems);
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
