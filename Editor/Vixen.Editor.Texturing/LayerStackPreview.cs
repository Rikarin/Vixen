// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Plugin;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Graphics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.Texturing.Layers;
using Vixen.Texturing.Painting;

namespace Vixen.Editor.Texturing;

/// <summary>What one attempt at a stack's picture produced, and the sentence that goes under it.</summary>
/// <param name="Image">The picture, or <see langword="null" /> when there is none.</param>
/// <param name="Usage">Which map it is.</param>
/// <param name="Width">The stack's authored width, whether or not there is a picture.</param>
/// <param name="Height">Its height.</param>
/// <param name="Status">What to say under the pane.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The extent is carried even when the image is null, and that is the same decision
///         <c>TextureGraphView.Show</c> makes.</b> The zoom, the fit and the pointer readout are about
///         the texels an author is authoring; a pane that lost them when a bake failed would rescale
///         itself every time somebody typed a bad number into a layer.
///     </para>
///     <para>
///         ⚠ <b>Everything the compile said travels with the picture, and not just the sentence.</b>
///         <see cref="Status" /> answers "why is there no map", which is a question with one answer;
///         <see cref="Problems" /> and <see cref="Diagnostics" /> are what the compile <em>had to
///         say</em>, and a compilation that produced a plan can still say plenty. Until
///         <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a> a warning reached the panel
///         through neither: <c>Refused</c> only runs when there is no plan and filters to errors even
///         then, so <c>TG0022</c> — chosen over a silent rescale on the grounds that "it is said" —
///         was said to nothing but xunit.
///     </para>
/// </remarks>
sealed record LayerStackPicture(IEditorImage? Image, string Usage, int Width, int Height, string Status) {
    /// <summary>What building the graph had to say, about layers an artist can select.</summary>
    public ImmutableArray<LayerStackProblem> Problems { get; init; } = [];

    /// <summary>What compiling it had to say, about nodes.</summary>
    public ImmutableArray<NodeDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Which layer emitted each node, for the panel that renders those diagnostics.</summary>
    /// <remarks>
    ///     ⚠ <b>Carried beside <see cref="Diagnostics" /> and not folded into them</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/880">#880</a>. Without it the only thing
    ///     a line could name is the node, and one mistyped mask setting on one layer raises a
    ///     character-identical diagnostic from every node the layer's per-channel walk emitted — so
    ///     naming the node turns two mistakes into fourteen lines, which is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/842">#842</a> made worse rather than
    ///     fixed. Naming the <em>layer</em> collapses the fourteen to two.
    /// </remarks>
    public ImmutableDictionary<NodeId, string> Layers { get; init; } =
        ImmutableDictionary<NodeId, string>.Empty;
}

/// <summary>The plan's picture of one channel for the paint panes, or why there is none.</summary>
/// <param name="Image">The picture, which <see cref="LayerStackPreview" /> keeps current, or null.</param>
/// <param name="Refusal">Why there is none, for the sentence under the pane; empty when there is.</param>
sealed record LayerStackLive(PaintImage? Image, string Refusal);

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
///         ⚠ <b>This is what <c>TextureGraphPreview</c> was written before it could do, and it can
///         now.</b> That type evaluated a fixed checkerboard for three batches while its status line
///         cited <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a> — "the compiler is
///         internal, so this plugin can offer every node and compile none of them" — which was false
///         the day it was written. <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a> and
///         <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a> closed by giving it the one
///         missing call and this pane's external loop, which is now
///         <see cref="TextureExternalImages" /> rather than three private statics here.
///     </para>
///     <para>
///         ⚠ <b>An imported image is read on the panel's thread, and a picture that will not read is
///         a sentence rather than an exception —
///         <a href="https://github.com/Rikarin/Vixen/issues/818">#818</a>.</b> Skipping one was never
///         an option: <c>TexturePlanEvaluator.Evaluate</c> refuses a plan with an external nothing
///         supplied, and it refuses it by throwing about an image index out of a panel build. What a
///         mesh map, a <c>vxpaint:</c> and a project asset each do is
///         <see cref="TextureExternalImages" />'s, because the graph pane needs the same answers.
///     </para>
///     <para>
///         ⚠ <b>Never called from inside the host's own frame</b>, for
///         <c>TextureGraphPreview</c>'s reason unchanged: <c>TexturePlanEvaluator.Evaluate</c> drives
///         <c>BeginFrame</c>, <c>EndFrame</c> and <c>WaitIdle</c> on the device itself. Every route
///         here is a command handler, a panel build, a pointer event or the plugin's update, and all
///         four run before the host's <c>BeginFrame</c>.
///     </para>
///     <para>
///         <b>One <see cref="LayerStackEvaluator" />, held from edit to edit, over the module's
///         evaluator (plan § 3.2).</b> A value moves numbers in the plan it has; its uploads stay on
///         the device and a stroke repairs them a tile at a time. This used to compile and upload
///         everything per call, which an opacity drag paid once per frame.
///     </para>
///     <para>
///         ⚠ <b>What the paint panes show is this type's too, and it is the plan's output rather
///         than anything computed here.</b> <see cref="Live" /> starts following a channel, and every
///         evaluation from then on copies what it rewrote into <see cref="Picture" />: the bake says
///         which rectangles those were, because a stroke's own rectangle does not. A filter over the
///         painted layer spreads it, and an undo brings back a whole cached picture.
///     </para>
/// </remarks>
sealed class LayerStackPreview : IDisposable {
    /// <summary>Which map the pane shows when nothing else is asked for.</summary>
    public const string DefaultUsage = "baseColor";

    readonly IEditorGraphics graphics;
    readonly TextureEvaluatorLease evaluators;
    readonly PaintCanvasStore canvases;
    readonly List<PaintRect> moved = [];

    IEditorImage? shown;
    Bound? bound;
    string followed = "";
    TextureResultKey followedKey;

    /// <summary>Builds a preview over the graphics a host lent the plugin.</summary>
    /// <param name="graphics">The host's graphics.</param>
    /// <param name="evaluators">Where the one evaluator comes from — see <see cref="TextureEvaluatorLease" />.</param>
    /// <param name="canvases">
    ///     The session's open <c>.vxpaint</c> canvases. ⚠ The module's and not this pane's, which is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>'s finding rather than a
    ///     wiring detail: a cache this pane owned would serve the picture from before the stroke,
    ///     because a live paint session writes texels in memory and does not touch the file until
    ///     pointer-up.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public LayerStackPreview(
        IEditorGraphics graphics,
        TextureEvaluatorLease evaluators,
        PaintCanvasStore canvases
    ) {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(evaluators);
        ArgumentNullException.ThrowIfNull(canvases);

        this.graphics = graphics;
        this.evaluators = evaluators;
        this.canvases = canvases;
    }

    /// <summary>How many plans have been evaluated over this preview's life.</summary>
    /// <remarks>
    ///     A counter rather than a flag, for <c>TextureGraphPreview.Evaluations</c>'s reason: the
    ///     defect it counts against is a pane showing a stale picture, which leaves every structural
    ///     claim about the image true and only this one false.
    /// </remarks>
    public int Evaluations { get; private set; }

    /// <summary>How many times a stack actually compiled here, which a value edit must not move.</summary>
    public int Compilations { get; private set; }

    /// <summary>The channel being followed, as the plan last produced it, or null.</summary>
    public PaintImage? Picture { get; private set; }

    /// <summary>Whether a stroke can be shown by repairing the plan.</summary>
    public bool Following => Picture is not null && bound?.Run.Plan is not null;

    /// <summary>A paint layer that names no canvas yet, and the canvas its first stroke is going into.</summary>
    /// <remarks>
    ///     ⚠ <b>A layer that names no file reaches the plan as nothing</b>, and only pointer-up writes
    ///     the name down, since that is an undoable edit a missed click must not make. So for the
    ///     length of a first stroke the plan is told a name the document has not been given yet.
    /// </remarks>
    public (LayerStackDocument Document, string Layer, string Paint)? Unnamed { get; set; }

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
    public LayerStackPicture Evaluate(
        LayerStackDocument document,
        string usage = DefaultUsage,
        string setName = ""
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(setName);

        var stack = document.Document;
        var width = stack.BaseWidth;
        var height = stack.BaseHeight;

        if (stack.Sets.Count == 0) {
            return new(null, usage, width, height, "No preview: this stack has no texture set, so there is no map to bake.");
        }

        // ⚠ Published before anything compiles, and the library comes off the document rather than
        // out of an `assets:` argument, because publishing one is a recursive directory walk plus a
        // YAML parse per compound and this method runs once per frame of an opacity drag — #956.
        // `Republish` is a flag test in the ordinary case; it does the walk only after something said
        // a compound was written. The project's compounds are the whole of #924: the compiler's
        // default is the compounds this build ships, so a graph fill naming one out of
        // `Assets/Compounds` refused here while compiling in the graph panel next door.
        document.Republish();

        // ⚠ The set the panel is showing, not the first one. The rows moved when a set was chosen
        // and the picture beside them did not, so the pane drew the Head set's map under the Body
        // set's rows and printed diagnostics naming layers that were not in the list. `SetFor`
        // falls back to the first set for a name nothing matches, which is what an empty name means.
        // ⚠ Never null here — the empty-stack case returned above — but resolved rather than
        // indexed, because `SetFor` is the one function that says what a set name means and an
        // unmatched name has to mean the first set rather than an exception.
        var chosen = LayerStackEdit.SetFor(stack, setName) ?? stack.Sets[0];

        // ⚠ Compiled with no device at all, and that is the finding. `LayerStackCompiler.Compile` is
        // pure — it allocates no texture and dispatches nothing — so everything it has to say about
        // an author's stack costs exactly as much on a host that cannot draw, and a stack that does
        // not compile does not compile on any host. An editor between construction and its window
        // coming up is in this state when it starts, and it must not show a stack in silence.
        if (graphics.Device is not { } device) {
            var compilation = LayerStackCompiler.Compile(stack, chosen, document.Library);

            return new(
                null,
                usage,
                width,
                height,
                compilation.Plan is null
                    ? Refused(compilation.Problems, compilation.Diagnostics)
                    : TexturePreview.Describe(TexturePreview.Blocking(graphics))
            ) {
                Problems = compilation.Problems,
                Diagnostics = compilation.Diagnostics,
                Layers = compilation.Layers
            };
        }

        using var bake = Baked(document, chosen, device, usage, out var refusal);

        // Everything either half said travels with every answer below, because a compilation that
        // produced a plan still has things to say and the sentence is not where they fit.
        var run = bound?.Run;

        LayerStackPicture Said(IEditorImage? drawn, int drawnWidth, int drawnHeight, string status) =>
            new(drawn, usage, drawnWidth, drawnHeight, status) {
                Problems = run?.Problems ?? [],
                Diagnostics = run?.Diagnostics ?? [],
                Layers = run?.Layers ?? ImmutableDictionary<NodeId, string>.Empty
            };

        if (bake is null) {
            return Said(null, width, height, refusal);
        }

        var picture = bake.Read(Output(run!, usage));
        var uploaded = graphics.Upload(picture.Width, picture.Height, picture.Pixels);

        // One live upload per preview: a pane re-evaluated on every edit would otherwise hold a
        // texture and a descriptor set per keystroke.
        shown?.Dispose();
        shown = uploaded;

        Evaluations++;

        // ⚠ The plan's cautions, said here because until now nothing anywhere read one — the reason
        // #801 was declined twice. A caution is a plan that bakes and does not draw what the stack
        // describes: an op reading an image of a size it does not write (#801), a radius past its
        // kernel's loop (#692). `TextureGraphCompiler` surfaces `Validate()` — the refusals — and a
        // caution reached `TextureBake.Warnings` and stopped there, which made every guard of this
        // kind a finished thing nothing called.
        var cautions = bake.Warnings.Length > 0
            ? " ⚠ " + string.Join(" · ", bake.Warnings)
            : "";

        return Said(
            uploaded,
            picture.Width,
            picture.Height,
            $"Preview: '{usage}', compiled from this stack and evaluated on the editor's device." + cautions
        );
    }

    /// <summary>Starts following one channel for the paint panes, and brings it up to date.</summary>
    /// <param name="document">The stack.</param>
    /// <param name="usage">Which map.</param>
    /// <param name="setName">Which set.</param>
    /// <returns>The picture, or why a pane has to show the layer's own pixels instead.</returns>
    public LayerStackLive Live(LayerStackDocument document, string usage, string setName) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(setName);

        document.Republish();

        if (graphics.Device is not { } device) {
            Stop();

            return new(null, "this host has no device to evaluate the stack on");
        }

        if (LayerStackEdit.SetFor(document.Document, setName) is not { } chosen) {
            Stop();

            return new(null, "this stack has no texture set");
        }

        if (!string.Equals(followed, usage, StringComparison.OrdinalIgnoreCase)) {
            Stop();
            followed = usage;
        }

        using var bake = Baked(document, chosen, device, usage, out var refusal);

        if (bake is null) {
            Stop();

            // The pane's sentence has its own opening, and the reason is the part that travels.
            return new(null, refusal.Replace("No preview: ", "", StringComparison.Ordinal).TrimEnd('.'));
        }

        return new(Picture, "");
    }

    /// <summary>Stops following, for a pane that has gone back to showing a layer.</summary>
    public void Stop() {
        followed = "";
        followedKey = default;
        Picture = null;
        moved.Clear();
    }

    /// <summary>Repairs the plan over whatever strokes have dirtied since the last evaluation.</summary>
    /// <remarks>
    ///     ⚠ <b>Prepares nothing.</b> A drag edits no layer, so what this runs is the plan
    ///     <see cref="Live" /> left at pointer-down: <see cref="LayerStackEvaluator.Evaluate" /> sends
    ///     the dirty tiles up and runs what reads them. Nothing here grows with the stack's document.
    /// </remarks>
    public void Repair() {
        if (Following) {
            Step()?.Dispose();
        }
    }

    /// <summary>Hands over what <see cref="Picture" /> has had rewritten since the last call.</summary>
    /// <param name="into">Cleared and filled.</param>
    public void TakeMoved(List<PaintRect> into) {
        into.Clear();
        into.AddRange(moved);
        moved.Clear();
    }

    /// <summary>The output image of a map.</summary>
    static int Output(LayerStackEvaluator run, string usage) {
        foreach (var output in run.Outputs) {
            if (string.Equals(output.Usage, usage, StringComparison.OrdinalIgnoreCase)) {
                return output.Image;
            }
        }

        return -1;
    }

    /// <summary>Brings the plan up to the stack and evaluates what changed.</summary>
    /// <param name="document">The stack.</param>
    /// <param name="chosen">Its set.</param>
    /// <param name="device">The device.</param>
    /// <param name="usage">The map the caller wants, which has to exist.</param>
    /// <param name="refusal">The sentence, when this returns null.</param>
    /// <returns>The bake, or null.</returns>
    /// <remarks>
    ///     ⚠ <b>One retry, for a canvas that changed size under a held plan.</b> A file another tool
    ///     rewrote at another resolution cannot go through the tiles of the texture it was uploaded
    ///     to, and an upload from nothing is what turns that into a sentence.
    /// </remarks>
    TextureBake? Baked(
        LayerStackDocument document,
        TextureSetAsset chosen,
        IGraphicsDevice device,
        string usage,
        out string refusal
    ) {
        for (var attempt = 0; attempt < 2; attempt++) {
            if (Prepared(document, chosen, device) is { } why) {
                refusal = why;

                return null;
            }

            var run = bound!.Run;

            if (Output(run, usage) < 0) {
                refusal = $"No preview: this stack writes no '{usage}' map. It writes "
                    + $"{string.Join(", ", run.Outputs.Select(output => output.Usage))}.";

                return null;
            }

            if (Step() is { } bake) {
                refusal = "";

                return bake;
            }
        }

        refusal = "No preview: a painted canvas changed under this stack and would not go back up.";

        return null;
    }

    /// <summary>Holds the evaluator for this document and asks it to catch up.</summary>
    /// <returns>Why there is no plan, or null when there is.</returns>
    string? Prepared(LayerStackDocument document, TextureSetAsset chosen, IGraphicsDevice device) {
        // ⚠ Asked for on every evaluation and owned by nobody here — #820. The module holds one for
        // both panes, because an evaluator is a pipeline cache per kernel and output format and two
        // of them over one device compile the whole overlap twice.
        var evaluator = evaluators(device);

        // ⚠ A plan outlives the fill that read its pictures, so a file that moved is asked about
        // here. Dropping the held evaluator is what a recompile is: there is nothing else to clear.
        if (bound is not { } held
            || !held.Holds(document, chosen, evaluator)
            || (held.Pass is { } pass && TextureExternalImages.Moved(pass))) {
            Bind(document, chosen, evaluator);
        }

        var run = bound!.Run;
        var refusals = run.Prepare(document.Document, Named(document, chosen), document.Library.Registry, document.Library.SubGraphs);

        if (run.Compiled) {
            Compilations++;
        }

        if (run.Plan is not null) {
            return null;
        }

        // ⚠ Before anything the fill said: this sentence is about the stack, and it is the one
        // message an author can act on.
        if (run.Problems.Any(static problem => problem.Severity == NodeSeverity.Error)
            || run.Diagnostics.Any(static one => one.Severity == NodeSeverity.Error)) {
            return Refused(run.Problems, run.Diagnostics);
        }

        return refusals.Count > 0
            ? "No preview: " + string.Join(" · ", refusals) + " Every other layer compiled."
            : Refused(run.Problems, run.Diagnostics);
    }

    /// <summary>Starts over with a new evaluator for a document.</summary>
    void Bind(LayerStackDocument document, TextureSetAsset chosen, TexturePlanEvaluator evaluator) {
        // ⚠ Abandoned rather than disposed when it belongs to another evaluator, for
        // `TexturingModule.Evaluator`'s reason: that means a device went without a word, and freeing
        // textures through a destroyed device is the crash, not a tidier version of it.
        if (bound is { } old && ReferenceEquals(old.Evaluator, evaluator)) {
            old.Run.Dispose();
        }

        Bound fresh = new(document, document.AssetPath, chosen.Name, evaluator);

        fresh.Run = new(
            evaluator,
            canvases,
            document.AssetPath,
            (uploads, plan, owed) => {
                fresh.Pass = new(canvases);

                return TextureExternalImages.Supply(document.Project, document.AssetPath, uploads, plan, owed, fresh.Pass);
            }
        );

        bound = fresh;
    }

    /// <summary>The set the plan is built from, with a first stroke's canvas named.</summary>
    TextureSetAsset Named(LayerStackDocument document, TextureSetAsset set) =>
        Unnamed is { } first
        && ReferenceEquals(first.Document, document)
        && Renamed(set.Layers, first.Layer, first.Paint) is { } layers
            ? set with { Layers = layers }
            : set;

    static List<LayerAsset>? Renamed(List<LayerAsset> layers, string id, string paint) {
        for (var index = 0; index < layers.Count; index++) {
            var layer = layers[index];

            if (string.Equals(layer.Id, id, StringComparison.Ordinal)) {
                return layer.Paint.Trim().Length > 0 ? null : [.. layers[..index], layer with { Paint = paint }, .. layers[(index + 1)..]];
            }

            if (Renamed(layer.Children, id, paint) is { } children) {
                return [.. layers[..index], layer with { Children = children }, .. layers[(index + 1)..]];
            }
        }

        return null;
    }

    /// <summary>Evaluates what changed, and copies what that rewrote into the followed picture.</summary>
    /// <returns>The bake, or null when a watched canvas no longer fits the plan and it was let go.</returns>
    TextureBake? Step() {
        TextureBake bake;

        try {
            bake = bound!.Run.Evaluate();
        } catch (ArgumentException) {
            Unbind();

            return null;
        }

        Follow(bake);

        return bake;
    }

    /// <summary>Lets the held evaluator go, with what it holds on the device.</summary>
    void Unbind() {
        bound?.Run.Dispose();
        bound = null;
    }

    /// <summary>Copies an evaluation's changes to the followed channel into its picture.</summary>
    /// <remarks>
    ///     ⚠ <b>On every evaluation and not only a repair's</b>, because the rectangles are relative
    ///     to the evaluation before. One that skipped this would leave them describing a change the
    ///     picture never had.
    /// </remarks>
    void Follow(TextureBake bake) {
        if (followed.Length == 0) {
            return;
        }

        var output = Output(bound!.Run, followed);

        if (output < 0) {
            Stop();

            return;
        }

        var size = bake.Plan.SizeOf(output);

        if (Picture is null || Picture.Width != size.X || Picture.Height != size.Y) {
            Picture = new(size.X, size.Y);
            followedKey = default;
        }

        var key = bake.KeyOf(output);

        if (key.IsValid && key == followedKey) {
            return;
        }

        var repairs = followedKey.IsValid ? bake.RepairOf(output) : null;

        followedKey = key;

        if (repairs is not { } rects) {
            Picture.CopyFrom(bake.Read(output).Pixels);
            moved.Add(Picture.Bounds);

            return;
        }

        var pieces = bake.Read(output, rects);

        for (var index = 0; index < pieces.Count; index++) {
            PaintRect rect = new(rects[index].X, rects[index].Y, rects[index].Width, rects[index].Height);

            Picture.WriteRect(rect, pieces[index].Pixels);
            moved.Add(rect);
        }
    }

    /// <summary>What to say when the compilation refused.</summary>
    /// <param name="problemsSaid">What building the graph said.</param>
    /// <param name="diagnostics">What compiling it said.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>Both lists, because they are two readers' problems</b> —
    ///     <see cref="LayerStackCompilation" />'s own remarks. A layer problem names a row an artist
    ///     can select; a node diagnostic names a node in a graph nobody has exploded and is a
    ///     compiler's or a builder's fault. A pane that showed only the first would be silent on
    ///     every failure of the second.
    ///     <para>
    ///         ⚠ <b>Still errors only, and that is now a division of labour rather than a hole.</b>
    ///         This sentence answers "why is there no map", so a warning does not belong in it — a
    ///         warning is precisely a thing that did not stop the map. What
    ///         <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a> found is that there was
    ///         nowhere else for one to go; <c>LayerStackPicture.Diagnostics</c> is that somewhere, and
    ///         <c>LayerStackView</c> lists it whether or not there is a picture.
    ///     </para>
    /// </remarks>
    static string Refused(ImmutableArray<LayerStackProblem> problemsSaid, ImmutableArray<NodeDiagnostic> diagnostics) {
        var problems = problemsSaid.Select(problem => problem.Message)
            .Concat(diagnostics.Where(one => one.Severity == NodeSeverity.Error)
                .Select(one => one.Id + ": " + one.Message))
            .ToArray();

        return problems.Length == 0
            ? "No preview: this stack did not compile, and nothing said why — which is a compiler bug rather than yours."
            : "No preview: " + string.Join(" · ", problems);
    }

    /// <summary>Gives back what is held on a device that is going, while it is still valid.</summary>
    /// <remarks>The picture stays: it is in memory, and the next evaluation starts from nothing.</remarks>
    public void ReleaseDevice() {
        Unbind();

        followedKey = default;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The picture and the held stack, and not the evaluator.</b> The editor stops holding a
    ///     texture for a plugin that has gone — but the evaluator is the module's, lent to both panes,
    ///     and a pane that freed it would take the other pane's pipelines with it.
    ///     <c>TexturingModule.Release</c> is what disposes it, through the registration scope, and it
    ///     does so after this. #820.
    /// </remarks>
    public void Dispose() {
        shown?.Dispose();
        shown = null;

        Unbind();
        Stop();
    }

    /// <summary>One stack, its evaluator, and who it was opened for.</summary>
    sealed class Bound(LayerStackDocument document, string path, string set, TexturePlanEvaluator evaluator) {
        public TexturePlanEvaluator Evaluator { get; } = evaluator;

        public LayerStackEvaluator Run { get; set; } = null!;

        /// <summary>The fill the plan's pictures came from, or null when it read none.</summary>
        public TextureExternalPass? Pass { get; set; }

        public bool Holds(LayerStackDocument other, TextureSetAsset chosen, TexturePlanEvaluator lent) =>
            ReferenceEquals(document, other)
            && string.Equals(path, other.AssetPath, StringComparison.Ordinal)
            && string.Equals(set, chosen.Name, StringComparison.Ordinal)
            && ReferenceEquals(Evaluator, lent);
    }
}
