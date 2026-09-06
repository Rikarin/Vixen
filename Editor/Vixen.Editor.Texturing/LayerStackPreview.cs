// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Plugin;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Graphics;

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
///         type still evaluates a fixed checkerboard, and until
///         <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a> its status line cited
///         <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a> — "the compiler is
///         internal, so this plugin can offer every node and compile none of them". #738 is closed
///         and <c>TextureGraphCompiler</c> is public: <see cref="LayerStackCompiler" /> in this very
///         assembly compiles through it, and so does <c>TextureGraphDocument.Compile</c>. The line
///         now names <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>, which is the
///         gap that is actually open — a missing caller rather than a missing visibility.
///     </para>
///     <para>
///         ⚠ <b>An imported image is read here, on the panel's thread, and a picture that will not
///         read is a sentence rather than an exception —
///         <a href="https://github.com/Rikarin/Vixen/issues/818">#818</a>.</b>
///         <c>TextureGraphExternals.Upload</c> fills the externals whose bytes the compilation
///         carries — a ramp, a curve table — and hands back the ones naming an asset, because a
///         compiler that ran on every edit must not touch an <c>AssetDatabase</c>. This is the host
///         half: the database resolves the reference, <c>ImageDecoders</c> reads the file, and the
///         texels go up through the same <c>TextureUploads</c>. Skipping one was never an option —
///         <c>TexturePlanEvaluator.Evaluate</c> refuses a plan with an external nothing supplied,
///         and it refuses it by throwing about an image index out of a panel build.
///     </para>
///     <para>
///         ⚠ <b>A mesh map is not a file and is refused as one.</b> A <c>Source/Mesh Map</c> crosses
///         as <c>meshmap:curvature</c> rather than as a path, because what it names is a measurement
///         of a mesh this pane has not been told about; resolving it as a project path would be a
///         missing-file message about a file nobody named.
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

    /// <summary>What a mesh map's reference starts with, rather than a path.</summary>
    /// <remarks>
    ///     ⚠ <b>Duplicated from <c>TextureMeshMaps.Scheme</c>, which is <c>internal</c> to
    ///     <c>Vixen.Editor.TextureGraph</c> and visible to its own tests alone.</b> The alternative
    ///     is resolving <c>meshmap:curvature</c> as a project path and telling an artist that a file
    ///     of that name is missing. <c>LayerStackPanelDeviceTests</c> asserts a mesh-map layer still
    ///     gets the sentence, which is the only thing that can catch the two drifting apart.
    /// </remarks>
    const string TextureMeshMapScheme = "meshmap:";

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

        // ⚠ Compiled before the device is asked for, and the order is the finding.
        // `LayerStackCompiler.Compile` is pure — it allocates no texture and dispatches nothing — so
        // everything it has to say about an author's stack costs exactly as much on a host that
        // cannot draw. Asking for the device first meant an editor between construction and its
        // window coming up showed a stack in silence, and it is the state the editor starts in.
        //
        // ⚠ `assets` is the whole of #924, and leaving it out was not a smaller version of the same
        // behaviour: the compiler's default is the four compounds this build ships, so a graph fill
        // or a mask effect naming a compound out of `Assets/Compounds` refused here while compiling
        // in the graph panel next door. The parameter existed for a batch and nothing production
        // passed it, which is #858's fix reaching nobody.
        var compilation = LayerStackCompiler.Compile(
            stack,
            stack.Sets[0],
            assets: document.Project.Paths.Assets
        );

        // Everything either half said travels with every answer below, because a compilation that
        // produced a plan still has things to say and the sentence is not where they fit.
        LayerStackPicture Said(IEditorImage? drawn, int drawnWidth, int drawnHeight, string status) =>
            new(drawn, usage, drawnWidth, drawnHeight, status) {
                Problems = compilation.Problems,
                Diagnostics = compilation.Diagnostics,
                Layers = compilation.Layers
            };

        // ⚠ Before the device, because a stack that does not compile does not compile on any host.
        // Told the other way round, the one message an author could act on was replaced by a message
        // about the window not being up yet.
        if (compilation.Plan is not { } plan) {
            return Said(null, width, height, Refused(compilation));
        }

        if (graphics.Device is not { } device) {
            return Said(null, width, height, TexturePreview.Describe(TexturePreview.Blocking(graphics)));
        }

        var image = -1;

        foreach (var output in compilation.Outputs) {
            if (string.Equals(output.Usage, usage, StringComparison.OrdinalIgnoreCase)) {
                image = output.Image;

                break;
            }
        }

        if (image < 0) {
            return Said(
                null,
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
        List<string> unresolved = [];

        foreach (var entry in owed) {
            if (Resolve(document, uploads, plan, entry) is { } why) {
                unresolved.Add(why);
            }
        }

        // ⚠ Every one of them, and only then the refusal. A pane that returned at the first would
        // send an artist round the loop once per missing picture, which for a stack that has been
        // moved between projects is once per layer.
        if (unresolved.Count > 0) {
            return Said(
                null,
                width,
                height,
                "No preview: " + string.Join(" · ", unresolved) + " Every other layer compiled."
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

    /// <summary>Reads one external image out of the project and uploads it.</summary>
    /// <param name="document">The open stack: whose assets resolve a reference, and where it lives.</param>
    /// <param name="uploads">Where the texture is made, and what owns it.</param>
    /// <param name="plan">The plan the image belongs to, which says what format and size it is.</param>
    /// <param name="entry">The external the compilation could not fill.</param>
    /// <returns>Null when it was uploaded, or the sentence saying why it was not.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every failure is a returned sentence and none is an exception, including the ones
    ///         that are this build's fault.</b> A preview runs on every edit and a throw out of one
    ///         takes the editor's frame with it — so a file that has been deleted, a format nothing
    ///         decodes, and a decoder that read the file and produced nothing are all the same kind
    ///         of answer here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rgba8 only, and it is a real limit rather than an oversight.</b> The plan's
    ///         external image for a <c>Source/Bitmap</c> is <c>Rgba8</c> —
    ///         <c>BitmapNode</c> says why — so a KTX2 or DDS asset that decodes to a block-compressed
    ///         format has the wrong byte count for the image it would fill, and
    ///         <c>TextureUploads.Add</c> would refuse it with a message about a byte count rather
    ///         than about a file. Named here instead.
    ///     </para>
    /// </remarks>
    static string? Resolve(
        LayerStackDocument document,
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry
    ) {
        var reference = entry.Asset.Trim();

        // A mesh map names a measurement rather than a file — see the type's remarks.
        if (reference.StartsWith(TextureMeshMapScheme, StringComparison.Ordinal)) {
            return $"a layer reads '{reference}', which is a measurement of a mesh this pane has not been "
                + "told about rather than a file it can open.";
        }

        if (PaintReference.Claims(reference)) {
            return Painted(document, uploads, plan, entry, reference);
        }

        var project = document.Project;

        if (!project.Assets.TryGetByPath(reference, out var asset)) {
            return $"'{reference}' is not in this project's assets, so there is nothing to read.";
        }

        var file = project.Paths.Absolute(asset.Path);
        var extension = Path.GetExtension(file);

        if (ImageDecoders.For(ImageDecoders.BuiltIn, extension) is not { } decoder) {
            return $"nothing here decodes '{extension}', so '{reference}' cannot be read.";
        }

        TextureData decoded;

        try {
            using var stream = File.OpenRead(file);

            decoded = decoder.Decode(stream, extension);
        } catch (Exception failure) when (failure is IOException
            or InvalidDataException or NotSupportedException or ArgumentException
            or UnauthorizedAccessException) {
            return $"'{reference}' would not read: {failure.Message}";
        }

        if (decoded.Format != PixelFormat.Rgba8UNorm) {
            return $"'{reference}' decoded as {decoded.Format} and a graph's imported image is Rgba8, so this "
                + "pane cannot upload it. Import it as an uncompressed 8-bit picture.";
        }

        try {
            uploads.Add(plan, entry.Image, decoded.Width, decoded.Height, decoded.Level(0));
        } catch (ArgumentException failure) {
            return $"'{reference}' could not be uploaded: {failure.Message}";
        }

        return null;
    }

    /// <summary>Reads one channel of a paint layer's canvas and uploads it.</summary>
    /// <param name="document">The open stack, whose folder the canvas is beside.</param>
    /// <param name="uploads">Where the texture is made.</param>
    /// <param name="plan">The plan the image belongs to.</param>
    /// <param name="entry">The external to fill.</param>
    /// <param name="reference">Its <c>vxpaint:</c> reference.</param>
    /// <returns>Null when it was uploaded, or the sentence saying why it was not.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The host half of <a href="https://github.com/Rikarin/Vixen/issues/852">#852</a>,
    ///         and it is the imported-picture path with one substitution.</b> #818's resolver reads
    ///         a file the <c>AssetDatabase</c> knows about through <c>ImageDecoders</c>; a
    ///         <c>.vxpaint</c> is not in that database and no decoder reads it, so it is resolved
    ///         against the <em>stack's own folder</em> and read by <c>PaintCanvas</c>. Everything
    ///         after that — the byte order, the format, the upload — is identical, because
    ///         <c>PaintImage</c>'s texels are already RGBA8 with red first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Relative to the document rather than to the project.</b> <c>LayerAsset.Paint</c>
    ///         is documented as relative to the stack, and <c>LayerPaint.NameFor</c> derives a bare
    ///         file name — so a stack in a subfolder whose canvases were resolved from the project
    ///         root would read a file from the wrong folder or, worse, another stack's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A channel the canvas does not hold is transparent rather than an error.</b> A
    ///         paint layer writes every channel it does not restrict, and an artist who has painted
    ///         base colour alone has a canvas with one image in it. Refusing here would make the
    ///         first stroke on a seven-channel set produce six sentences; an absent channel
    ///         contributes nothing, which is what not having painted it means.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read from disk on every evaluation, which is a real cost and a deliberate
    ///         match.</b> The imported-picture path decodes its PNG on every evaluation too, and a
    ///         preview runs on every edit. A 4K canvas is 67 MB a channel, so this is the more
    ///         expensive of the two and the one worth caching first —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>. It is filed rather
    ///         than done here because a cache that this pane owns and the paint session does not
    ///         would serve a stale canvas the moment the two are wired to each other.
    ///     </para>
    /// </remarks>
    static string? Painted(
        LayerStackDocument document,
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry,
        string reference
    ) {
        if (!PaintReference.TryParse(reference, out var relative, out var usage)) {
            return $"a layer reads '{reference}', which claims to be painted pixels and does not name both a "
                + "file and a channel. That is a builder's fault rather than yours.";
        }

        var folder = Path.GetDirectoryName(document.AssetPath);

        if (string.IsNullOrEmpty(folder)) {
            return $"'{relative}' is named relative to this stack and this stack has no folder, so there is "
                + "nothing to resolve it against.";
        }

        var file = Path.GetFullPath(Path.Combine(folder, relative));

        if (!File.Exists(file)) {
            return $"'{relative}' is the painted canvas this layer names and there is no such file beside the "
                + "stack, so its pixels cannot be read.";
        }

        PaintCanvas canvas;

        try {
            using var stream = File.OpenRead(file);

            canvas = PaintCanvas.Read(stream);
        } catch (Exception failure) when (failure is IOException
            or InvalidDataException or UnauthorizedAccessException or EndOfStreamException) {
            return $"'{relative}' would not read: {failure.Message}";
        }

        if (!canvas.Has(usage)) {
            // Not a failure: an unpainted channel is an absent one. Filled with transparency so the
            // layer's blend composites nothing rather than black — `Blend.rvn` reads the foreground's
            // alpha as its amount, so zero alpha leaves the backdrop exactly as it was.
            PaintImage empty = new(canvas.Width, canvas.Height);

            return Uploaded(uploads, plan, entry, relative, empty);
        }

        return Uploaded(uploads, plan, entry, relative, canvas.Channel(usage));
    }

    /// <summary>Puts one paint image into the plan's external slot.</summary>
    static string? Uploaded(
        TextureUploads uploads,
        TexturePlan plan,
        TextureGraphExternal entry,
        string relative,
        PaintImage image
    ) {
        try {
            uploads.Add(plan, entry.Image, image.Width, image.Height, image.Texels);
        } catch (ArgumentException failure) {
            return $"'{relative}' could not be uploaded: {failure.Message}";
        }

        return null;
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
    ///     <para>
    ///         ⚠ <b>Still errors only, and that is now a division of labour rather than a hole.</b>
    ///         This sentence answers "why is there no map", so a warning does not belong in it — a
    ///         warning is precisely a thing that did not stop the map. What
    ///         <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a> found is that there was
    ///         nowhere else for one to go; <c>LayerStackPicture.Diagnostics</c> is that somewhere, and
    ///         <c>LayerStackView</c> lists it whether or not there is a picture.
    ///     </para>
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
