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
        bool force = false
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

        var wanted = new Dictionary<MaterialMapUsage, TextureGraphOutput>();
        var unknown = new List<string>();

        foreach (var output in compilation.Outputs) {
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
            document.Project,
            document.AssetPath,
            uploads,
            plan,
            compilation.Externals,
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
            set = new ProjectMaterialBaker(document.Project, folder).Write(
                name,
                MaterialBake.Encode(pictures),
                Record(document, device),
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
                Diagnostics = compilation.Diagnostics,
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

        var warnings = set.Warnings.Count > 0
            ? " ⚠ " + string.Join(" · ", set.Warnings)
            : "";

        return new MaterialBakeOutcome(set, Reported(set) + dropped + cautions + warnings) {
            Diagnostics = compilation.Diagnostics
        };
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
}
