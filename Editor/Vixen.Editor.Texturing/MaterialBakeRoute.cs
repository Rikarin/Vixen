// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Plugin;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Graphics;

namespace Vixen.Editor.Texturing;

/// <summary>What one run of the bake did, as the verb reports it.</summary>
/// <param name="Set">What the project now holds, or <see langword="null" /> when nothing was written.</param>
/// <param name="Status">What to tell the artist, whether or not anything was written.</param>
/// <remarks>
///     ⚠ <b>A refusal is a sentence and not an exception, which is
///     <c>TextureGraphPreview.Evaluate</c>'s rule and is load bearing for the same reason.</b> This
///     runs from a command handler, and a throw out of one takes the editor's frame with it — so a
///     graph that does not compile, a host with no device, a bitmap the project cannot resolve and a
///     map somebody has painted over all come back here rather than out.
/// </remarks>
sealed record MaterialBakeOutcome(MaterialBakeSet? Set, string Status) {
    /// <summary>What the compiler had to say, so a panel can list it whether or not anything baked.</summary>
    public ImmutableArray<NodeDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Whether the refusal was the painted-over one, which is the only one force answers.</summary>
    /// <remarks>
    ///     ⚠ <b>A flag rather than the caller matching on the message.</b> Three of the four refusals
    ///     here are things force cannot help with — a graph that does not compile, a host with no
    ///     device, a file the asset database did not pick up — and a verb that offered force for all
    ///     of them would send an artist to a control that changes nothing.
    /// </remarks>
    public bool Painted { get; init; }
}

/// <summary>The route from a <c>.vxtexgraph</c> on the canvas to a <c>.vxmat</c> in the project.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § M5's first exit word, and until this type there was no route at all</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1009">#1009</a>. Every piece existed and
///         had tests: <c>TextureGraphDocument.Compile</c> makes the plan,
///         <see cref="TexturePlanEvaluator" /> makes the texels, <see cref="MaterialBake.Encode" />
///         packs occlusion, roughness and metalness into one file, and
///         <see cref="ProjectMaterialBaker" /> does the scan-then-read-back GUID dance and writes the
///         provenance block. <c>new ProjectMaterialBaker</c> had exactly one caller outside tests —
///         <c>Tools/Vixen.Cli/TextureRunner.cs</c>, which reads a folder of PNGs and evaluates no
///         graph — so an artist who authored a graph in the editor could not turn it into a material
///         by any route a person can take. This is that route; it invents nothing.
///     </para>
///     <para>
///         ⚠ <b>It is deliberately the same six steps <c>TextureGraphPreview.Evaluate</c> takes,
///         because the pane and the bake disagreeing is the defect this seam produces.</b> Compile
///         through the document, refuse before asking for a device, fill the externals through
///         <see cref="TextureExternalImages" />, dispatch through the module's one evaluator. What
///         differs is only the end: the pane reads one output and uploads it, and this reads every
///         output and writes files.
///     </para>
///     <para>
///         ⚠ <b>The declaring <c>Evaluate</c> overload, and that choice was made before this type was
///         written</b> — <a href="https://github.com/Rikarin/Vixen/issues/1014">#1014</a>.
///         <c>Evaluate(TexturePlan, IReadOnlyDictionary&lt;int, TextureHandle&gt;)</c> fills in
///         <c>new TextureExternal(handle, TextureExternal.Sampled)</c> with no size, so a plan
///         supplied through it gets no extent caution for its bitmap input — the picture
///         <a href="https://github.com/Rikarin/Vixen/issues/632">#632</a> is about. Every caller of
///         that overload today is a test fixture, and this is the first production caller that could
///         have reached for it: it does not. <see cref="TextureUploads.Externals" /> declares the real
///         size from the value it already remembers, and the cautions it buys are reported below.
///     </para>
///     <para>
///         ⚠ <b>Every usage the graph writes, rather than the first one.</b> The pane shows
///         <c>Outputs[0]</c> and says which map it is; a bake that did the same would write a
///         material with one map in it and no sign that the other six outputs existed. That is the
///         difference between a preview and a product, and it is why the two are not one call.
///     </para>
/// </remarks>
sealed class MaterialBakeRoute {
    readonly IEditorGraphics graphics;
    readonly TextureEvaluatorLease evaluators;
    readonly PaintCanvasStore canvases;

    /// <summary>Builds the route over the graphics a host lent the plugin.</summary>
    /// <param name="graphics">The host's graphics.</param>
    /// <param name="evaluators">Where the one evaluator comes from — see <see cref="TextureEvaluatorLease" />.</param>
    /// <param name="canvases">
    ///     The session's open <c>.vxpaint</c> canvases, for <c>TextureGraphPreview</c>'s reason: a
    ///     graph produced by <c>LayerStackExplode</c> carries whatever <c>vxpaint:</c> references the
    ///     stack had, and a bake that passed nothing here would be the copy of that loop which forgot
    ///     a case. ⚠ It also makes the bake read the pixels a stroke has <em>not</em> yet saved, which
    ///     is what the pane shows and therefore what an artist is baking.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public MaterialBakeRoute(
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

    /// <summary>How many bakes this route has written a material for.</summary>
    /// <remarks>
    ///     ⚠ <b>Written, not attempted, and the direction is the point.</b> A route un-wired from the
    ///     verb leaves every file assertion in the suite unreachable and this at zero; a route that
    ///     ran and refused leaves it at zero as well and says so in the status. Counting attempts
    ///     would move under a regression that stopped writing anything, which is the wrong way round.
    /// </remarks>
    public int Written { get; private set; }

    /// <summary>Compiles a graph, evaluates every map it writes, and puts them in the project.</summary>
    /// <param name="document">The graph on the canvas.</param>
    /// <param name="name">What the material should be called. Sanitised by the baker.</param>
    /// <param name="folder">Which folder under <c>Assets/</c> to write into.</param>
    /// <param name="force">Overwrite outputs somebody has painted over.</param>
    /// <param name="parallax">
    ///     Compose a <c>ParallaxOcclusionFeature</c> onto the material this bake writes, so that a
    ///     <em>first</em> bake can turn the height march on. See <see cref="MaterialBakeParallax" />,
    ///     which is the same rule the command line's <c>--parallax</c> asks for and which decides
    ///     nothing beyond the ask.
    /// </param>
    /// <returns>What happened, and what to say about it. Never null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    /// <remarks>
    ///     ⚠ <b>Outside the host's own frame</b>, which is <c>TexturingModule.Refresh</c>'s rule and
    ///     not a new one: <see cref="TexturePlanEvaluator" /> drives <c>BeginFrame</c> and
    ///     <c>EndFrame</c> on the device itself, so a call from inside the editor's frame would reset
    ///     a command pool with work still executing in it. Every route here is a command handler.
    /// </remarks>
    public MaterialBakeOutcome Bake(
        TextureGraphDocument document,
        string name,
        string folder = MaterialMapNaming.DefaultFolder,
        bool force = false,
        bool parallax = false
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var compilation = document.Compile();

        MaterialBakeOutcome Said(string status) => new(null, status) { Diagnostics = compilation.Diagnostics };

        // ⚠ Before the device, for the preview pane's reason: a graph that does not compile does not
        // compile on any host, and answering an author's mistake with a message about the window not
        // being up yet is what asking the other way round produces.
        if (compilation.Plan is not { } plan) {
            return Said("Nothing baked: " + Refused(compilation));
        }

        if (compilation.Outputs.Length == 0) {
            return Said(
                "Nothing baked: this graph writes no map. An Output node is what names a usage and "
                + "makes a file the bake writes, and there is none here."
            );
        }

        if (graphics.Device is not { } device) {
            return Said("Nothing baked: " + TexturePreview.Describe(TexturePreview.Blocking(graphics)));
        }

        return Write(
            document.Project,
            document.AssetPath,
            device,
            plan,
            compilation.Outputs,
            compilation.Externals,
            compilation.Diagnostics,
            Record(document, device),
            name,
            folder,
            force,
            parallax
        );
    }

    /// <summary>Compiles a layer stack, evaluates every map its channels name, and puts them in the project.</summary>
    /// <param name="document">The stack in the layers panel.</param>
    /// <param name="name">What the material should be called. One per texture set; see below.</param>
    /// <param name="folder">Which folder under <c>Assets/</c> to write into.</param>
    /// <param name="force">Overwrite outputs somebody has painted over.</param>
    /// <returns>One outcome per texture set, in the stack's own order. Never empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § M7's exit word, which was owed one document after
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1009">#1009</a> closed the graph's</b>
    ///         — <a href="https://github.com/Rikarin/Vixen/issues/1029">#1029</a>. Both material-bake
    ///         callers read a graph, so an artist who built a layer stack — the whole of M7 and M9 —
    ///         could not turn it into a material by any route a person can take, which is what makes
    ///         a smart material worth applying: somebody who applies one wants a material out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A stack's usages come from its channels and they are already
    ///         <c>TextureGraphOutput</c>s by the time this sees them, which is the half of #1029's
    ///         framing that turned out not to be owed.</b> That issue reads "nothing walks the
    ///         stack's outputs … the stack half is compile once per usage, or teach the compiler to
    ///         emit them together" — but <c>LayerStackGraph.Build</c> already emits one
    ///         <c>Output/Output</c> node per channel of the set and <c>LayerStackCompilation.Outputs</c>
    ///         already carries all of them. What was missing was a caller: <c>LayerStackPreview</c>
    ///         searches that list for <em>one</em> usage and shows it, and nothing read the rest.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One material per texture set, and not one for <c>Sets[0]</c></b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>. A stack's sets are the
    ///         material slots of one model, so a bake that wrote the first one would leave every
    ///         other slot of the mesh with no material at all and say nothing about it. A single-set
    ///         stack takes <paramref name="name" /> unchanged; a stack with two or more suffixes each
    ///         with its set's name, because the alternative — <c>Hull_Default</c> for the ordinary
    ///         one-slot case — is a name nobody would have chosen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Outside the host's own frame</b>, which is
    ///         <see cref="Bake(TextureGraphDocument, string, string, bool, bool)" />'s rule and
    ///         holds for the same reason: the evaluator drives
    ///         <c>BeginFrame</c> and <c>EndFrame</c> on the device itself.
    ///     </para>
    /// </remarks>
    public ImmutableArray<MaterialBakeOutcome> Bake(
        LayerStackDocument document,
        string name,
        string folder = MaterialMapNaming.DefaultFolder,
        bool force = false
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var stack = document.Document;

        if (stack.Sets.Count == 0) {
            return [new(null, "Nothing baked: this stack has no texture set, so there is no map to bake.")];
        }

        // ⚠ Republished before the compile and not after it, which is `LayerStackPreview.Evaluate`'s
        // order and is what stops a bake compiling against a node library that predates the compound
        // the artist saved a minute ago.
        document.Republish();

        var outcomes = ImmutableArray.CreateBuilder<MaterialBakeOutcome>(stack.Sets.Count);
        var names = Names(stack, name);

        for (var index = 0; index < stack.Sets.Count; index++) {
            var set = stack.Sets[index];
            var compilation = LayerStackCompiler.Compile(stack, set, document.Library);
            var material = names[index];

            MaterialBakeOutcome Said(string status) =>
                new(null, $"'{set.Name}': " + status) { Diagnostics = compilation.Diagnostics };

            // ⚠ Before the device, for the pane's reason: a stack that does not compile does not
            // compile on any host, and answering an author's mistake with a message about the window
            // not being up yet is what asking the other way round produces.
            if (compilation.Plan is not { } stackPlan) {
                outcomes.Add(Said("nothing baked: " + Refused(compilation)));

                continue;
            }

            if (compilation.Outputs.Length == 0) {
                outcomes.Add(
                    Said(
                        "nothing baked: this texture set declares no channel, and a channel is what "
                        + "names a usage and makes a file the bake writes."
                    )
                );

                continue;
            }

            if (graphics.Device is not { } stackDevice) {
                outcomes.Add(Said("nothing baked: " + TexturePreview.Describe(TexturePreview.Blocking(graphics))));

                continue;
            }

            outcomes.Add(
                Write(
                    document.Project,
                    document.AssetPath,
                    stackDevice,
                    stackPlan,
                    compilation.Outputs,
                    compilation.Externals,
                    compilation.Diagnostics,
                    Record(document, stackDevice, set.Name),
                    material,
                    folder,
                    force,

                    // ⚠ Never from the stack route, and it is a gap rather than a decision: the
                    // editor's parallax ask is a verb on the *graph* bake, matching the command
                    // line's `--parallax`, and a layer stack has no such verb to pass one from.
                    // See #1103.
                    parallax: false
                )
            );
        }

        return outcomes.ToImmutable();
    }

    /// <summary>Packs a stack's painted layer weights into a splat map beside each set's material.</summary>
    /// <param name="document">The layer stack on the canvas.</param>
    /// <param name="name">What the stack's material is called, which the map is written beside.</param>
    /// <param name="folder">Which folder under <c>Assets/</c> the baked materials are in.</param>
    /// <param name="force">Overwrite a splat map somebody has painted over.</param>
    /// <returns>One outcome per texture set, in the sets' order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1124">#1124</a>, and the step that
    ///         turns <a href="https://github.com/Rikarin/Vixen/issues/1073">#1073</a> from "a script
    ///         where the tool should be" into a verb.</b> Before it an artist who wanted a layered
    ///         material painted the weights in the stack, exported them by hand, packed them in an
    ///         external editor, imported the result and named it in the <c>.vxmat</c>'s
    ///         <c>textures:</c> block.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One evaluation per layer, and it has to be.</b>
    ///         <c>TextureDiagnostics.TwoOutputsOneUsage</c> refuses a graph with two <c>mask</c>
    ///         outputs, so "N masks in node order are N channels" was never available — and it is the
    ///         right refusal, because two outputs under one usage is two maps a bake would write to
    ///         one file. So each layer's coverage is its own compile of its own one-channel set, and
    ///         <see cref="MaterialBake.Splat" /> is what makes them channels.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is <em>not</em> a bake of the stack, and the difference is which files exist
    ///         afterwards.</b> <see cref="Bake(LayerStackDocument,string,string,bool)" /> writes a
    ///         material and its maps; this writes one texture and binds it onto a material that has
    ///         to be there already, because a splat map's channels are that material's layer indices.
    ///         The order for an artist is therefore bake, add the layered feature with its layers,
    ///         then this — which is the same two-step shape a height map is under
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1103">#1103</a>).
    ///     </para>
    /// </remarks>
    public ImmutableArray<MaterialBakeOutcome> BakeSplat(
        LayerStackDocument document,
        string name,
        string folder = MaterialMapNaming.DefaultFolder,
        bool force = false
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var stack = document.Document;

        if (stack.Sets.Count == 0) {
            return [new(null, "Nothing baked: this stack has no texture set, so there is no layer to weigh.")];
        }

        // The pane's order — see `Bake`: a stack compiled against a node library that predates the
        // compound the artist saved a minute ago is a coverage from a graph nobody is looking at.
        document.Republish();

        var outcomes = ImmutableArray.CreateBuilder<MaterialBakeOutcome>(stack.Sets.Count);
        var names = Names(stack, name);

        for (var index = 0; index < stack.Sets.Count; index++) {
            var set = stack.Sets[index];

            MaterialBakeOutcome Said(string status) => new(null, $"'{set.Name}': " + status);

            var layers = LayerStackSplat.Layers(set);

            if (layers.Length == 0) {
                outcomes.Add(
                    Said(
                        "nothing baked: this texture set has no enabled layer, so every channel of its splat map "
                        + "would be zero — a material whose weights sum to nothing."
                    )
                );

                continue;
            }

            if (layers.Length > LayerStackSplat.MaxLayers) {
                outcomes.Add(new(null, "Nothing baked: " + LayerStackSplat.Crowded(set, layers.Length)));

                continue;
            }

            // ⚠ Before the device, which is `Bake`'s rule for its own compile: a project with no
            // layered material to bind onto has none on any host, and answering that with a message
            // about the window not being up is what asking the other way round produces. The
            // sentence is `ProjectMaterialBaker`'s rather than a second copy of it, because the
            // write refuses on the same question at the end whatever this says here.
            if (new ProjectMaterialBaker(document.Project, folder).Unbindable(names[index]) is { } missing) {
                outcomes.Add(new(null, "Nothing baked: " + missing));

                continue;
            }

            if (graphics.Device is not { } device) {
                outcomes.Add(Said("nothing baked: " + TexturePreview.Describe(TexturePreview.Blocking(graphics))));

                continue;
            }

            outcomes.Add(Weigh(document, set, layers, device, names[index], folder, force));
        }

        return outcomes.ToImmutable();
    }

    /// <summary>Resolves one set's layer coverages and writes the splat map they pack into.</summary>
    /// <param name="document">The stack, for its project, its library and its externals.</param>
    /// <param name="set">The texture set.</param>
    /// <param name="layers">Its layers, bottom first.</param>
    /// <param name="device">The device to evaluate on.</param>
    /// <param name="name">What the material is called.</param>
    /// <param name="folder">Which folder under <c>Assets/</c>.</param>
    /// <param name="force">Overwrite a splat map somebody has painted over.</param>
    /// <returns>What happened, and what to say about it.</returns>
    /// <remarks>
    ///     ⚠ <b>A refusal on any one layer stops the whole map rather than weighing it as zero.</b> A
    ///     splat map missing one layer's channel is a surface drawn out of the other three, normalised
    ///     over a total that is short — a lit, plausible picture of a stack the artist did not make.
    ///     There is no partial answer here that is better than saying which layer refused.
    /// </remarks>
    MaterialBakeOutcome Weigh(
        LayerStackDocument document,
        TextureSetAsset set,
        ImmutableArray<LayerAsset> layers,
        IGraphicsDevice device,
        string name,
        string folder,
        bool force
    ) {
        MaterialBakeOutcome Said(string status) => new(null, $"'{set.Name}': " + status);

        var coverage = new List<Bitmap>(layers.Length);
        var cautions = new List<string>();

        foreach (var layer in layers) {
            var named = layer.Name.Length > 0 ? layer.Name : layer.Id;
            var library = document.Library;
            var build = LayerStackGraph.Weights(document.Document, LayerStackSplat.Coverage(set, layer), library.Registry);

            var compilation = LayerStackCompiler.Compile(
                document.Document,
                build,
                library.Registry,
                subGraphs: library.SubGraphs
            );

            if (compilation.Plan is not { } plan || compilation.Outputs.Length == 0) {
                return Said($"nothing baked, because layer '{named}' did not: " + Refused(compilation));
            }

            using TextureUploads uploads = new(device);

            var unresolved = TextureExternalImages.Fill(
                document.Project,
                document.AssetPath,
                uploads,
                plan,
                compilation.Externals,
                canvases
            );

            if (unresolved.Count > 0) {
                return Said($"nothing baked, because layer '{named}' needs " + string.Join(" · ", unresolved));
            }

            using var run = evaluators(device).Evaluate(plan, uploads.Externals);

            coverage.Add(run.Read(compilation.Outputs[0].Image));
            cautions.AddRange(run.Warnings);
        }

        MaterialBakeSet written;

        try {
            written = new ProjectMaterialBaker(document.Project, folder).WriteSplat(
                name,
                MaterialBake.Splat(coverage),
                coverage.Count,
                Record(document, device, set.Name),
                force
            );
        } catch (ArgumentException failure) {
            // Two coverages at different sizes, and a material whose layer list is not this stack's.
            return Said("nothing baked: " + failure.Message);
        } catch (IOException failure) {
            // ⚠ Only the overpaint is offered force, on `Write`'s terms: the other refusal here is
            // "there is no material of that name", which forcing cannot conjure one for.
            return new MaterialBakeOutcome(null, $"'{set.Name}': nothing baked: " + failure.Message) {
                Painted = failure.Message.EndsWith(ProjectMaterialBaker.Overpaint, StringComparison.Ordinal)
            };
        } catch (InvalidOperationException failure) {
            return Said("nothing baked: " + failure.Message);
        }

        Written++;

        return new(
            written,
            $"Wrote '{Path.GetFileName(written.Files[0])}' weighing "
            + $"{coverage.Count.ToString(CultureInfo.InvariantCulture)} "
            + $"{(coverage.Count == 1 ? "layer" : "layers")} of '{set.Name}', and bound it onto {name}."
            + (cautions.Count > 0 ? " ⚠ " + string.Join(" · ", cautions) : "")
            + (written.Warnings.Count > 0 ? " ⚠ " + string.Join(" · ", written.Warnings) : "")
        );
    }

    /// <summary>Evaluates a compiled plan's every output and writes the set into the project.</summary>
    /// <param name="project">The project the files go into.</param>
    /// <param name="assetPath">The document the plan came from, for the externals it names.</param>
    /// <param name="device">The device to run it on.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="outputs">Which image is which map.</param>
    /// <param name="externals">The imported images the plan needs supplied.</param>
    /// <param name="diagnostics">What the compile had to say, carried onto every answer.</param>
    /// <param name="record">The provenance block.</param>
    /// <param name="name">What the material should be called.</param>
    /// <param name="folder">Which folder under <c>Assets/</c>.</param>
    /// <param name="force">Overwrite outputs somebody has painted over.</param>
    /// <returns>What happened, and what to say about it.</returns>
    /// <remarks>
    ///     ⚠ <b>Shared by the graph route and the stack route rather than copied, and the copy is the
    ///     defect it exists to prevent.</b> Everything after "there is a plan and a device" is the
    ///     same eight steps — resolve the externals, evaluate, read every output, encode, write,
    ///     and turn each of the four refusals into a sentence — and the one that gets forgotten in a
    ///     second copy is the <see cref="MaterialBakeOutcome.Painted" /> flag, which is the whole of
    ///     what makes a force control reachable
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/1019">#1019</a>).
    /// </remarks>
    MaterialBakeOutcome Write(
        EditorProject project,
        string assetPath,
        IGraphicsDevice device,
        TexturePlan plan,
        ImmutableArray<TextureGraphOutput> outputs,
        ImmutableArray<TextureGraphExternal> externals,
        ImmutableArray<NodeDiagnostic> diagnostics,
        MaterialBakeRecord record,
        string name,
        string folder,
        bool force,
        bool parallax
    ) {
        MaterialBakeOutcome Said(string status) => new(null, status) { Diagnostics = diagnostics };

        var wanted = new Dictionary<MaterialMapUsage, TextureGraphOutput>();
        var unknown = new List<string>();

        foreach (var output in outputs) {
            // ⚠ Two lists in two assemblies, and this is where they meet. `TextureUsages.Known` is
            // the nine an Output node accepts and `MaterialMapNaming.Every` is the nine a bake can
            // write; the compiler canonicalises against the first, so today every output parses.
            // `Every_usage_an_output_node_accepts_is_one_the_baker_writes` is the assertion that they
            // have not drifted — this branch is what an artist would be told on the day they do,
            // instead of a map silently missing from the material.
            if (!MaterialMapNaming.TryParseSuffix(output.Usage, out var usage)) {
                unknown.Add(output.Usage);

                continue;
            }

            wanted[usage] = output;
        }

        if (wanted.Count == 0) {
            return Said(
                "Nothing baked: this build writes no map called "
                + string.Join(", ", unknown.Select(one => "'" + one + "'"))
                + "."
            );
        }

        // ⚠ And the mixed case, which the branch above cannot reach. One known usage alongside one
        // unknown one leaves `wanted.Count` at 1, and an artist was told the bake succeeded while a
        // map they named was silently missing from the material — the exact failure this whole
        // stretch was written to prevent, in the case that is likelier than a graph whose every
        // output has drifted.
        var dropped = unknown.Count > 0
            ? " ⚠ This build writes no map called "
            + string.Join(", ", unknown.Select(one => "'" + one + "'"))
            + ", so nothing was written for "
            + (unknown.Count == 1 ? "it" : "them")
            + "."
            : "";

        var evaluator = evaluators(device);

        using TextureUploads uploads = new(device);

        // The same loop both panes run — see `TextureExternalImages`. A `Source/Bitmap` in a graph
        // names a project asset, and a second copy of this resolve here would be the copy that
        // forgot a case.
        var unresolved = TextureExternalImages.Fill(
            project,
            assetPath,
            uploads,
            plan,
            externals,
            canvases
        );

        if (unresolved.Count > 0) {
            return Said("Nothing baked: " + string.Join(" · ", unresolved) + " Everything else compiled.");
        }

        // ⚠ `uploads.Externals` and not the bare-handle overload — #1014, decided above.
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var pictures = new Dictionary<MaterialMapUsage, Bitmap>();

        foreach (var (usage, output) in wanted) {
            pictures[usage] = bake.Read(output.Image);
        }

        MaterialBakeSet set;

        try {
            set = new ProjectMaterialBaker(project, folder).Write(
                name,
                MaterialBake.Encode(pictures),
                record,
                force
            );
        } catch (ArgumentException failure) {
            // Two outputs at different levels land here — `MaterialBake.Encode` refuses a set whose
            // maps are not one size rather than resampling one to meet the other.
            return Said("Nothing baked: " + failure.Message);
        } catch (IOException failure) {
            // ⚠ Only the overpaint, and this used to flag all three. § D4's digest exists so that a
            // file whose bytes are no longer what the bake wrote is flagged rather than overwritten
            // — but `Write` also raises this for the `Crowd` ceiling and for a locked or read-only
            // file, and force answers neither. Flagging those was sending an artist to a control
            // that changes nothing, which is the sentence `Painted` exists to avoid.
            return new MaterialBakeOutcome(null, "Nothing baked: " + failure.Message) {
                Diagnostics = diagnostics,
                Painted = failure.Message.EndsWith(ProjectMaterialBaker.Overpaint, StringComparison.Ordinal)
            };
        } catch (InvalidOperationException failure) {
            // ⚠ And this one is deliberately not offered force, because forcing would not help: a
            // file the asset database did not pick up cannot be named by id however hard the bake
            // insists, and a material naming a texture that resolves to nothing shades from the
            // bindless fallback with nothing said.
            return Said("Nothing baked: " + failure.Message);
        }

        Written++;

        var cautions = bake.Warnings.Length > 0
            ? " ⚠ " + string.Join(" · ", bake.Warnings)
            : "";

        // ⚠ After the write and never before it, which is `MaterialBakeParallax`'s rule rather than
        // an ordering chosen here: the path the material lands at is the baker's, and a name a
        // different source already owns becomes `Name_2` — so an ask applied beforehand would have to
        // guess a path and could land on somebody else's material.
        var kept = set.Warnings;
        var asked = "";

        if (parallax) {
            kept = MaterialBakeParallax.Requested(set, out var refused);

            // ⚠ Said either way. A bake that turned the march on looks identical to one that did not,
            // and the artist pressed a verb whose whole content is that difference.
            asked = refused is null
                ? $" The height march is on: this material carries {MaterialBakeParallax.Tag}."
                : " ⚠ " + refused;
        }

        var warnings = kept.Count > 0
            ? " ⚠ " + string.Join(" · ", kept)
            : "";

        return new MaterialBakeOutcome(set, Reported(set) + dropped + cautions + warnings + asked) {
            Diagnostics = diagnostics
        };
    }

    /// <summary>One material name per texture set, all of them distinct.</summary>
    /// <param name="stack">The stack.</param>
    /// <param name="name">The stack's own name, which a single-set stack keeps unchanged.</param>
    /// <returns>The names, in the sets' order.</returns>
    /// <remarks>
    ///     ⚠ <b>The obvious spelling — <c>name + "_" + set.Name</c> per set — loses a set's whole
    ///     bake silently.</b> <c>TextureSetAsset.Name</c> defaults to the empty string, so a stack
    ///     with two unnamed sets sent both through the writer under one name: the second overwrote
    ///     the first's maps, and because the first write had just recorded its digest the overpaint
    ///     guard saw bytes that matched and raised nothing. Both sets were then reported as baked
    ///     and one of them was not on the disk.
    ///     ⚠ <b>A collision falls back to the set's index rather than refusing</b>, because the
    ///     artist's fix — naming the sets — is one they can only make after seeing what was baked,
    ///     and a refusal on a `.vxlayers` that was fine yesterday is worse than a name with a 1 in
    ///     it. The notification names the set either way.
    /// </remarks>
    static ImmutableArray<string> Names(LayerStackAsset stack, string name) {
        if (stack.Sets.Count == 1) {
            return [name];
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = ImmutableArray.CreateBuilder<string>(stack.Sets.Count);

        for (var index = 0; index < stack.Sets.Count; index++) {
            var wanted = stack.Sets[index].Name.Length == 0
                ? name + "_" + index.ToString(CultureInfo.InvariantCulture)
                : name + "_" + stack.Sets[index].Name;

            if (!taken.Add(wanted)) {
                wanted += "_" + index.ToString(CultureInfo.InvariantCulture);
                taken.Add(wanted);
            }

            names.Add(wanted);
        }

        return names.DrainToImmutable();
    }

    /// <summary>What the artist is told when it worked.</summary>
    /// <param name="set">What the project now holds.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>The maps are counted off <see cref="MaterialBakeSet.Maps" /> rather than off the
    ///     graph's outputs</b>, because those are not the same number: occlusion, roughness and
    ///     metalness are three usages and one file, which is the whole of what
    ///     <see cref="MaterialMapNaming.Packed" /> does. A sentence saying "three maps" over a
    ///     material holding one would be wrong in exactly the direction that makes an artist look for
    ///     files that were never meant to exist.
    /// </remarks>
    static string Reported(MaterialBakeSet set) =>
        $"'{set.Name}': {set.Maps.Count.ToString(CultureInfo.InvariantCulture)} "
        + (set.Maps.Count == 1 ? "map" : "maps")
        + " and a material — "
        + string.Join(", ", set.Maps.Keys.Select(MaterialMapNaming.Suffix))
        + ".";

    /// <summary>The provenance block this bake writes into the material's sidecar.</summary>
    /// <param name="document">The graph that produced the maps.</param>
    /// <param name="device">The device that ran it.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="MaterialBakeRecord.SourceAsset" /> is the set's identity and the file
    ///         name is not.</b> Two graphs both called <c>Material</c> produce the same seven file
    ///         names, and a writer keying on those overwrites the first one's maps with the second's
    ///         and hands back the first one's GUIDs — #681, on the mesh-map baker, for exactly this
    ///         reason. The command line cannot fill this in, because a folder bake has no asset;
    ///         a graph bake has one, and this is where it starts being used.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The adapter is recorded and never asserted</b>, which is § D4's decision rather
    ///         than an omission: a re-bake on a different card is not byte-identical, and refusing it
    ///         would make the first artist with another GPU a bug report.
    ///     </para>
    /// </remarks>
    static MaterialBakeRecord Record(TextureGraphDocument document, IGraphicsDevice device) => new() {
        Source = Relative(document.Project, document.AssetPath),
        SourceAsset = document.Asset,
        Adapter = device.Adapter.Name,

        // The exposed parameters as they stood, which is what makes the block enough to say what
        // produced these bytes. A graph exposing none writes an empty mapping rather than omitting
        // the key.
        Parameters = document.Graph.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => parameter.Default,
            StringComparer.Ordinal
        )
    };

    /// <summary>The provenance block a stack's bake writes into each material's sidecar.</summary>
    /// <param name="document">The stack that produced the maps.</param>
    /// <param name="device">The device that ran it.</param>
    /// <param name="set">Which texture set of the stack this material is.</param>
    /// <returns>The record.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The graph overload's three decisions, unchanged and for its reasons</b> — the asset
    ///         is the identity rather than the file name, the adapter is recorded and never asserted,
    ///         and the path is measured from the project so that a provenance block is not a fact
    ///         about somebody's machine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A stack exposes no parameters, so the mapping is empty rather than absent</b> —
    ///         which is what the graph overload does for a graph that exposes none, and the two have
    ///         to agree or a reader of the block would take a missing key for a bake by an older
    ///         build.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every texture set of one stack records the same source, and what tells two of
    ///         them apart is <see cref="MaterialBakeRecord.Set" /></b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1066">#1066</a>. Without it
    ///         <c>Hull_Body.vxmat.meta</c> and <c>Hull_Trim.vxmat.meta</c> carried
    ///         character-identical blocks and the only thing telling them apart was the file name,
    ///         which is exactly the identity <c>SourceAsset</c> exists because a file name is not.
    ///         ⚠ <b>It is not part of <c>MaterialProvenance.KeyOf</c> and must not become one</b>:
    ///         that key stops a <em>different</em> source adopting a name, and two sets of one stack
    ///         are the same source, so the shared key is correct rather than a collision.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The graph overload leaves it empty rather than writing the material's name into
    ///         it.</b> A graph has no texture sets, and a set named after the file would make
    ///         "which slot did this come from" answerable with a fact that is not one.
    ///     </para>
    /// </remarks>
    static MaterialBakeRecord Record(LayerStackDocument document, IGraphicsDevice device, string set) => new() {
        Source = Relative(document.Project, document.AssetPath),
        SourceAsset = document.Asset,
        Adapter = device.Adapter.Name,
        Set = set
    };

    /// <summary>A path measured from the project where it is inside one, and left alone where it is not.</summary>
    /// <param name="project">The project.</param>
    /// <param name="path">The absolute path.</param>
    /// <returns>The project-relative path, or the absolute one.</returns>
    /// <remarks>
    ///     ⚠ An absolute path off somebody's machine in a provenance block is a fact about that
    ///     machine, and it is the sort that reaches a review as a diff nobody can act on.
    ///     <c>TextureRunner.Relative</c> is the same six lines for the same reason, and the two are
    ///     not shared because nothing links the CLI to this plugin.
    /// </remarks>
    static string Relative(EditorProject project, string path) {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(project.Paths.Root);

        return full.StartsWith(root, StringComparison.Ordinal)
            ? full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/')
            : full;
    }

    /// <summary>What to say when the compilation refused.</summary>
    /// <param name="compilation">It.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>Errors only, because this sentence answers "why is there no material"</b> — and a
    ///     warning is precisely a thing that did not stop one. The warnings still travel, on
    ///     <see cref="MaterialBakeOutcome.Diagnostics" />.
    /// </remarks>
    static string Refused(TextureGraphCompilation compilation) {
        var problems = compilation.Diagnostics
            .Where(one => one.Severity == NodeSeverity.Error)
            .Select(one => one.Id + ": " + one.Message)
            .ToArray();

        return problems.Length == 0
            ? "this graph did not compile, and nothing said why — which is a compiler bug rather than yours."
            : string.Join(" · ", problems);
    }

    /// <summary>What to say when a texture set's stack refused.</summary>
    /// <param name="compilation">It.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>Both lists, which is <c>LayerStackPreview.Refused</c>'s decision and is the one thing
    ///     the graph overload above cannot be reused for.</b> A <c>LayerStackProblem</c> names a layer
    ///     an artist can select in the layers panel and a <c>NodeDiagnostic</c> names a node in a
    ///     graph nobody has exploded — so a bake that reported only the second would be silent on
    ///     every mistake an artist can actually make in the panel they are standing in.
    /// </remarks>
    static string Refused(LayerStackCompilation compilation) {
        var problems = compilation.Problems.Select(problem => problem.Message)
            .Concat(
                compilation.Diagnostics
                    .Where(one => one.Severity == NodeSeverity.Error)
                    .Select(one => one.Id + ": " + one.Message)
            )
            .ToArray();

        return problems.Length == 0
            ? "this stack did not compile, and nothing said why — which is a compiler bug rather than yours."
            : string.Join(" · ", problems);
    }
}
