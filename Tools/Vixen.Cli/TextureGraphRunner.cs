// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Yaml;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;

namespace Vixen.Cli;

/// <summary>`vixen texture bake --graph` — a `.vxtexgraph` evaluated on a GPU, into a material.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § M5's CLI row read literally, and the route an artist could not take</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/1020">#1020</a>. The editor half landed
///         two batches earlier: <c>TexturingModule</c>'s <c>Bake Material</c> verb compiles the open
///         document, evaluates it and writes through <see cref="ProjectMaterialBaker" />. A build
///         script regenerating a material set from a graph under version control had no route at all,
///         because nothing in this CLI created an <c>IGraphicsDevice</c> and evaluating a graph is
///         compute.
///     </para>
///     <para>
///         ⚠ <b>The decision in it is the refusal, and it is <see cref="HeadlessGraphics" />.</b> A
///         headless run with no adapter falls back to the device that draws nothing on every
///         platform, exits 0, and prints healthy counters — so a <c>--graph</c> bake could quietly
///         write black PNGs and a valid <c>.vxmat</c> over an artist's material. This verb refuses
///         instead, and the assembly does not reference <c>Vixen.Graphics.Null</c> so that the
///         refusal is a fact about what is linked rather than a branch somebody can delete.
///     </para>
///     <para>
///         ⚠ <b>It is the same six steps the editor's route takes, deliberately.</b> Publish the
///         compounds into a registry, compile through <see cref="TextureGraphCompiler" />, refuse
///         before asking for a device, upload the externals, evaluate, and hand every output to the
///         one <see cref="ProjectMaterialBaker" />. The pane, the editor's verb and this disagreeing
///         about what a graph means is the defect this seam produces; there is one baker and it now
///         has three callers.
///     </para>
///     <para>
///         ⚠ <b>What it deliberately cannot do is resolve a <c>Source/Bitmap</c> that names a
///         project asset</b>, which <see cref="TextureGraphExternals.Upload" /> hands back rather
///         than skipping for exactly this reason: "a host without one can see, before it starts,
///         that this graph is not one it can bake". The editor's resolver is 300 lines in the
///         texturing plugin and reads paint canvases the CLI has none of; a second copy here would be
///         the copy that forgot a case. So such a graph is named and refused —
///         <a href="https://github.com/Rikarin/Vixen/issues/1087">#1087</a> — and everything whose
///         pictures the compilation carries, which is every generator, pattern and noise graph and
///         every shipped compound, bakes.
///     </para>
/// </remarks>
static class TextureGraphRunner {
    /// <summary>Compiles a graph, evaluates every map it writes, and puts them in the project.</summary>
    /// <param name="project">The project to write into.</param>
    /// <param name="graph">The <c>.vxtexgraph</c>, as a path.</param>
    /// <param name="name">What the material should be called.</param>
    /// <param name="folder">Which folder under <c>Assets/</c> to write into.</param>
    /// <param name="force">Overwrite outputs somebody has painted over.</param>
    /// <param name="output">Where to write what happened.</param>
    /// <param name="error">Where to complain.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="ArgumentNullException">A writer or the project is null.</exception>
    public static ExitCode Bake(
        Project project,
        string graph,
        string name,
        string folder,
        bool force,
        TextWriter output,
        TextWriter error
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!File.Exists(graph)) {
            error.WriteLine($"There is no file at '{graph}'.");

            return ExitCode.UsageError;
        }

        var editor = new EditorProject(project.Paths);

        if (!Read(graph, error, out var model)) {
            return ExitCode.UsageError;
        }

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        // ⚠ `Assets/Compounds` spelled here as well as in `TextureNodeLibrary.CompoundFolder`, which
        // is in the texturing plugin and cannot be referenced from Tools/ without dragging the whole
        // editor shell in. It is the one thing this route duplicates rather than calls — #1088.
        var compounds = TextureCompoundLibrary.Publish(
            registry,
            Path.Combine(project.Paths.Assets, "Compounds"),
            out var unreadable
        );

        foreach (var problem in unreadable) {
            // Reported and not fatal, which is the library's own decision: one bad compound must not
            // cost every other node. Here it matters more than in a panel, because a node type
            // missing from the registry is a TG-diagnostic about the graph rather than about the file
            // that failed to read — so the cause is printed before the symptom.
            error.WriteLine($"'{problem.Path}' was not published: {problem.Problem}");
        }

        var compiler = new TextureGraphCompiler(registry) { SubGraphSource = compounds };
        var compilation = compiler.Compile(model);

        foreach (var diagnostic in compilation.Diagnostics) {
            error.WriteLine($"{diagnostic.Id}: {diagnostic.Message}");
        }

        // ⚠ Before the device, which is the editor route's rule and holds harder here: a graph that
        // does not compile does not compile on any machine, and answering an author's mistake with a
        // message about a missing driver is what asking the other way round produces.
        if (compilation.Value is not { } plan) {
            error.WriteLine("Nothing baked: this graph does not compile.");

            return ExitCode.Failed;
        }

        if (compiler.Outputs.Length == 0) {
            error.WriteLine(
                "Nothing baked: this graph writes no map. An Output node is what names a usage and "
                + "makes a file the bake writes, and there is none here."
            );

            return ExitCode.Failed;
        }

        var wanted = new Dictionary<MaterialMapUsage, TextureGraphOutput>();
        var unknown = new List<string>();

        foreach (var written in compiler.Outputs) {
            if (MaterialMapNaming.TryParseSuffix(written.Usage, out var usage)) {
                wanted[usage] = written;
            } else {
                unknown.Add(written.Usage);
            }
        }

        foreach (var missing in unknown) {
            // ⚠ Said even when other outputs did parse. One known usage beside one unknown one is the
            // likelier case, and reporting success over it is a material with a map silently absent —
            // `MaterialBakeRoute` learned this the same way.
            error.WriteLine($"⚠ This build writes no map called '{missing}', so nothing was written for it.");
        }

        if (wanted.Count == 0) {
            error.WriteLine("Nothing baked: none of this graph's outputs is a map this writes.");

            return ExitCode.Failed;
        }

        if (!HeadlessGraphics.TryOpen(out var device, out var why)) {
            error.WriteLine("Nothing baked: " + why);

            return ExitCode.Failed;
        }

        using (device) {
            return Run(editor, project, device!, plan, wanted, compiler, graph, model, name, folder, force, output, error);
        }
    }

    /// <summary>Uploads the externals, evaluates, and writes the set.</summary>
    static ExitCode Run(
        EditorProject editor,
        Project project,
        Vixen.Graphics.IGraphicsDevice device,
        TexturePlan plan,
        Dictionary<MaterialMapUsage, TextureGraphOutput> wanted,
        TextureGraphCompiler compiler,
        string graph,
        NodeGraphModel model,
        string name,
        string folder,
        bool force,
        TextWriter output,
        TextWriter error
    ) {
        using TextureUploads uploads = new(device);

        var owed = TextureGraphExternals.Upload(uploads, plan, compiler.Externals);

        if (owed.Length > 0) {
            error.WriteLine(
                "Nothing baked: this graph reads "
                + string.Join(", ", owed.Select(one => "'" + one.Asset + "'"))
                + ", and resolving a project asset into a picture is the editor's job — see #1087. "
                + "Everything else compiled."
            );

            return ExitCode.Failed;
        }

        using var bake = new TexturePlanEvaluator(device).Evaluate(plan, uploads.Externals);

        var pictures = new Dictionary<MaterialMapUsage, Bitmap>();

        foreach (var (usage, written) in wanted) {
            pictures[usage] = bake.Read(written.Image);
        }

        foreach (var caution in bake.Warnings) {
            error.WriteLine("⚠ " + caution);
        }

        MaterialBakeSet set;

        try {
            set = new ProjectMaterialBaker(editor, folder).Write(
                name,
                MaterialBake.Encode(pictures),
                Record(editor, project, graph, model, device),
                force
            );
        } catch (ArgumentException failure) {
            error.WriteLine(failure.Message);

            return ExitCode.UsageError;
        } catch (IOException failure) {
            error.WriteLine(failure.Message);
            error.WriteLine("Pass --force to bake over it anyway.");

            return ExitCode.Failed;
        } catch (InvalidOperationException failure) {
            error.WriteLine(failure.Message);

            return ExitCode.Failed;
        }

        foreach (var warning in set.Warnings) {
            error.WriteLine(warning);
        }

        output.WriteLine(
            $"{set.Name}: {set.Maps.Count} "
            + (set.Maps.Count == 1 ? "map" : "maps")
            + $" and a material, baked on {HeadlessGraphics.Adapter(device)}."
        );

        foreach (var file in set.Files) {
            output.WriteLine("  " + Relative(project, file));
        }

        return ExitCode.Success;
    }

    /// <summary>The provenance block this bake writes into the material's sidecar.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="MaterialBakeRecord.SourceAsset" /> and not only the path, and the scan is
    ///     what it costs.</b> A material's set is keyed on the source asset where there is one and on
    ///     the path where there is not — <c>MaterialProvenance.KeyOf</c> — so a CLI bake that left the
    ///     id empty would key on a path while the editor's bake of the same graph keys on the id, and
    ///     the two would read as different sources under one name: new GUIDs on every alternating
    ///     run. The folder bake has no asset to name and takes the fallback honestly; this one has.
    /// </remarks>
    static MaterialBakeRecord Record(
        EditorProject editor,
        Project project,
        string graph,
        NodeGraphModel model,
        Vixen.Graphics.IGraphicsDevice device
    ) {
        var relative = Relative(project, graph);

        editor.Assets.Scan();

        var asset = editor.Assets.TryGetByPath(relative, out var entry) ? entry.Guid : default;

        return new() {
            Source = relative,
            SourceAsset = asset,
            Adapter = HeadlessGraphics.Adapter(device),

            // What the graph exposes, as it stood. A graph exposing none writes an empty mapping
            // rather than omitting the key, which is `MaterialBakeRoute.Record`'s choice.
            Parameters = model.Parameters.ToDictionary(
                parameter => parameter.Name,
                parameter => parameter.Default,
                StringComparer.Ordinal
            )
        };
    }

    /// <summary>Reads a <c>.vxtexgraph</c> off the disk, or says why it could not be.</summary>
    /// <remarks>
    ///     ⚠ <b>A repair is printed and not fatal.</b> <c>NodeGraphDocument.Load</c> drops an edge to
    ///     a port that no longer exists and says so; refusing would make every graph saved against an
    ///     older node library unbakeable, and staying silent would bake something else.
    /// </remarks>
    static bool Read(string path, TextWriter error, out NodeGraphModel model) {
        model = null!;

        NodeGraphAsset stored;

        try {
            stored = YamlSerializer.Parse<NodeGraphAsset>(File.ReadAllText(path));
        } catch (Exception failure)
            when (failure is YamlParseException or YamlBindingException or NotSupportedException or IOException) {
            error.WriteLine($"'{Path.GetFileName(path)}' could not be read: {failure.Message}");

            return false;
        }

        model = NodeGraphDocument.Load(stored, out var repairs);

        foreach (var repair in repairs) {
            error.WriteLine($"⚠ {repair.Message}");
        }

        return true;
    }

    /// <summary>A path measured from the project where it is inside one, and left alone where it is not.</summary>
    static string Relative(Project project, string path) {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(project.Paths.Root);

        return full.StartsWith(root, StringComparison.Ordinal)
            ? full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/')
            : full;
    }
}
