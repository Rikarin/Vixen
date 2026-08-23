// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
///     The library shader modules the editor loads, compiled and compared with what is committed.
/// </summary>
/// <remarks>
///     <para>
///         <b>The gap this closes: the editor could not draw anything the shader library defines.</b>
///         <c>Editor/Vixen.Editor.Host/Shaders</c> holds four <c>.rvn</c> and their <c>.spv</c>, and
///         every one of those sources is standalone — no <c>import</c>, and the block-out BRDF
///         written out by hand rather than taken from <c>Raven/Library/Shading/Brdf.rvn</c> for
///         exactly that reason. A shader that <em>does</em> import, like <c>Terrain.rvn</c>, cannot be
///         compiled one file at a time: a package's declarations are visible to a sibling file only
///         within one compilation, and the packages import each other. So the editor's viewport could
///         hold a terrain it had no module to draw with.
///     </para>
///     <para>
///         ⚠ <b>A shader's import closure is one compilation, which is what <c>raven --source</c> is
///         for.</b> The alternative is a chain of <c>.rvnlib</c> in dependency order — a second build
///         graph, written down in a second place, that has to agree with the <c>import</c> lines.
///         Raven's own <c>LibraryReflectionTests</c> binds the library as one compilation for the
///         same reason; the closure is that, minus the packages whose <c>compose</c> slots a host has
///         to fill. See <see cref="SourcesFor" />.
///     </para>
///     <para>
///         ⚠ <b><c>--shader</c> is not an optimisation, it is the difference between one module and
///         ninety.</b> Generation runs over the whole module, so without it every shader in the
///         library would be written into the editor's resources.
///     </para>
///     <para>
///         ⚠ <b>Committed rather than built, for <c>Shaders/README.md</c>'s reason</b> — the editor's
///         modules are <em>supplied</em> to their renderers, so a caller hands over what it has, and
///         building the compiler first would make the editor depend on the larger project of the two.
///         What that costs is drift, which is what the check half of this target exists to make
///         impossible: a change to <c>Terrain.rvn</c> that nobody regenerated fails here.
///     </para>
/// </remarks>
partial class Build {
    [Parameter("Rewrite the committed shader modules instead of checking them")]
    readonly bool UpdateShaders;

    /// <summary>
    ///     Which library shaders the editor loads: the package, the shader, the permutation the
    ///     modules are compiled at, and what the committed files are called.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The list grows when the editor starts drawing one, not in anticipation.</b> Every
    ///         entry is bytes committed to the repository and a module embedded in the application, and
    ///         a shader nothing loads is both of those for nothing — the same argument
    ///         <c>LibraryReflectionTests.Published</c> makes about keys.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A permutation is a separate entry, because it is a separate module.</b> Raven folds
    ///         a <c>[Permutation]</c> before lowering, so <c>GrassScatter</c> at <c>Arguments=true</c>
    ///         and at its default are two binaries with one name — which is what <c>Output</c> exists
    ///         to pull apart: the committed file is <c>{Output}.{stage}.spv</c>, and an entry that
    ///         renames nothing leaves it as the shader's own name.
    ///     </para>
    /// </remarks>
    static readonly (string Package, string Shader, string[] Defines, string Output)[] EditorShaders = [
        ("Terrain", "Terrain", [], "Terrain"),
        ("Terrain", "Grass", [], "Grass"),
        ("Terrain", "GrassScatter", [], "GrassScatter"),
        ("Terrain", "GrassScatter", ["LayerBound=false"], "GrassScatterUnbound"),
        ("Terrain", "GrassScatter", ["Arguments=true"], "GrassScatterArguments")
    ];

    /// <summary>
    ///     The editor's own <c>.rvn</c> sources, whose modules were committed and never checked.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The gap this closes: nothing recompiled these, so a source edit and a stale binary
    ///         could sit in one commit.</b> The list above is the shaders the editor loads out of
    ///         <c>Raven/Library</c>; these four are written beside the editor and are what the check
    ///         half of this target was always described as covering. <c>Ui.rvn</c> is the one that
    ///         made it matter: <c>UiShape</c> grew to a hundred and twelve bytes, and a source that
    ///         said so beside a module that did not would have been read by the host as the new layout
    ///         and by the GPU as the old one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Standalone, which is why they can be one file each.</b> None of the four
    ///         <c>import</c>s anything — <c>Shaders/README.md</c> spells out why the block-out BRDF is
    ///         written by hand rather than taken from the library — so they need no
    ///         <see cref="SourcesFor" /> closure, and passing one would parse the same declarations
    ///         twice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every module a source emits, not a named one.</b> <c>Ui.rvn</c> declares five
    ///         shaders and produces five modules with five names of its own, so unlike the entries
    ///         above there is no output to rename and no <c>--shader</c> to pass. That also means this
    ///         half catches a shader *added* to one of these files and never committed.
    ///     </para>
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>A project as well as a name, because there are two of these directories now.</b> The
    ///     interface's shaders moved out of hand-written GLSL and into a Raven <c>Ui.rvn</c> under
    ///     <c>Platform/Vixen.Ui.Desktop</c>, which is what every application that is not the editor
    ///     draws with — and a source this gate did not know about is a source somebody can edit
    ///     without recompiling, which is exactly the state this whole target exists to make
    ///     impossible.
    ///     <para>
    ///         The two <c>Ui</c> entries are deliberately not one file. The editor's declares five
    ///         shaders and the host's eight — the three compositing stages are the difference — and
    ///         they are compiled against different packages. Sharing them means emitting a
    ///         <c>.rvnlib</c>, which is a build step neither of them has.
    ///     </para>
    /// </remarks>
    static readonly (string Project, string Source)[] EditorSources = [
        ("Editor/Vixen.Editor.Host", "Ui"),
        ("Editor/Vixen.Editor.Host", "Line"),
        ("Editor/Vixen.Editor.Host", "Mesh"),
        ("Editor/Vixen.Editor.Host", "MeshInstanced"),
        ("Platform/Vixen.Ui.Desktop", "Ui")
    ];

    /// <summary>
    ///     Every file of the packages a shader can reach, which is what one compilation has to be.
    /// </summary>
    /// <param name="package">The directory the shader is in.</param>
    /// <returns>The files, sorted.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The import closure, not the whole library — and the difference is not
    ///         performance.</b> Compiling everything means compiling <c>Pipeline/ForwardPlus.rvn</c>,
    ///         whose <c>compose</c> slots have no implementation bound unless a caller says which one
    ///         to use. Those bindings are a <em>host's</em> decision about a material, so a target
    ///         that compiled the library wholesale would have to carry a copy of them — a second place
    ///         <c>LibraryReflectionTests.PublishedComposition</c> lives, out of step with it the first
    ///         time somebody adds a slot.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Walked from the <c>import</c> lines rather than listed.</b> A written-down graph
    ///         is a second copy of the one in the sources, and the failure when it drifts is a name
    ///         that "does not exist" in a file that plainly declares it.
    ///     </para>
    ///     <para>
    ///         The sort is what makes two machines produce the same bytes, which is what lets the
    ///         check half compare them at all.
    ///     </para>
    /// </remarks>
    List<AbsolutePath> SourcesFor(string package) {
        var library = RootDirectory / "Raven" / "Library";
        var files = library.GlobFiles("*/*.rvn");

        var declared = new Dictionary<string, string>(System.StringComparer.Ordinal);
        var imports = new Dictionary<string, HashSet<string>>(System.StringComparer.Ordinal);

        foreach (var file in files) {
            var directory = file.Parent.Name;

            foreach (var line in System.IO.File.ReadLines(file)) {
                var text = line.Trim();

                if (text.StartsWith("package ", System.StringComparison.Ordinal)) {
                    declared[directory] = text[8..].Trim();
                } else if (text.StartsWith("import ", System.StringComparison.Ordinal)) {
                    if (!imports.TryGetValue(directory, out var named)) {
                        imports[directory] = named = new(System.StringComparer.Ordinal);
                    }

                    named.Add(text[7..].Trim());
                }
            }
        }

        // Package name back to the directory that declares it, so an `import` can be followed.
        var directories = declared
            .GroupBy(entry => entry.Value, System.StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, System.StringComparer.Ordinal);

        var wanted = new HashSet<string>(System.StringComparer.Ordinal);
        var pending = new Stack<string>();

        pending.Push(package);

        while (pending.Count > 0) {
            var current = pending.Pop();

            if (!wanted.Add(current)) {
                continue;
            }

            if (!imports.TryGetValue(current, out var named)) {
                continue;
            }

            foreach (var import in named) {
                if (directories.TryGetValue(import, out var directory)) {
                    pending.Push(directory);
                }
            }
        }

        return
        [
            .. files
                .Where(file => wanted.Contains(file.Parent.Name))
                .OrderBy(path => path.ToString(), System.StringComparer.Ordinal)
        ];
    }

    Target CheckShaders => definition => definition
        .Description("Fails if a committed editor shader module differs from what Raven produces now")
        .DependsOn(Restore)
        .Executes(() => {
                var compiler = RootDirectory / "Raven" / "Vixen.Raven.Cli" / "Vixen.Raven.Cli.csproj";

                DotNetBuild(settings => settings
                    .SetProjectFile(compiler)
                    .SetConfiguration(Configuration.Release)
                    .EnableNoRestore()
                );

                var staging = TemporaryDirectory / "shaders";

                staging.CreateOrCleanDirectory();

                // What was produced, against the name it is committed under — which differs from the
                // unit's own name exactly when the entry is a renamed permutation.
                var produced = new List<(AbsolutePath File, string Committed)>();

                for (var index = 0; index < EditorShaders.Length; index++) {
                    var (package, shader, defines, output) = EditorShaders[index];
                    var sources = SourcesFor(package);

                    Assert.True(sources.Count > 0, $"Found no sources for {package} — the glob is wrong.");

                    // ⚠ A directory per entry, because two permutations of one shader produce two
                    // binaries with one name — into a shared directory the second silently wins.
                    var into = staging / $"{index:00}-{output}";

                    into.CreateOrCleanDirectory();

                    var arguments = new List<string> {
                        "compile",
                        (RootDirectory / "Raven" / "Library" / package / $"{shader}.rvn").ToString(),
                        into.ToString(),
                        "--target",
                        "spirv",
                        "--shader",
                        shader,
                        "--no-color"
                    };

                    foreach (var define in defines) {
                        arguments.Add("--define");
                        arguments.Add(define);
                    }

                    // ⚠ Every library file except the one that is already the input. Passing it twice
                    // parses it twice into one compilation, which is every declaration in it declared
                    // twice.
                    foreach (var file in sources.Where(path => path.NameWithoutExtension != shader)) {
                        arguments.Add("--source");
                        arguments.Add(file.ToString());
                    }

                    DotNetRun(settings => settings
                        .SetProjectFile(compiler)
                        .SetConfiguration(Configuration.Release)
                        .EnableNoRestore()
                        .EnableNoBuild()
                        .SetApplicationArguments(arguments)
                    );

                    foreach (var file in into.GlobFiles("*.spv").OrderBy(path => path.Name, System.StringComparer.Ordinal)) {
                        // The backend names a unit `<shader>.<stage>`; the committed file swaps the
                        // shader for the entry's output name and keeps the stage.
                        //
                        // ⚠ Prefixed with the directory, like the entries below, because the
                        // comparison is against the repository root now rather than against one
                        // shader folder — there are two of those since the interface's own modules
                        // moved to `Platform/Vixen.Ui.Desktop`.
                        produced.Add((file, $"Editor/Vixen.Editor.Host/Shaders/{output}{file.Name[shader.Length..]}"));
                    }
                }

                // ⚠ The editor's own sources, compiled whole. `--emit-reflection` because the
                // committed `.reflect.json` beside each module is what the host reads its bindings
                // and its struct offsets out of — a module regenerated without it leaves the
                // reflection saying the old layout, which is the exact failure `UiShapeLayoutTests`
                // is on the other side of.
                for (var index = 0; index < EditorSources.Length; index++) {
                    var (project, source) = EditorSources[index];
                    var directory = RootDirectory / project / "Shaders";
                    // ⚠ The dots come out as well as the slashes, and that is not tidiness: the
                    // compiler decides "directory or file" by whether the path has an extension, so
                    // `src-00-Editor-Vixen.Editor.Host-Ui` is read as a single file called `Host-Ui`
                    // and refused — with a good message, which is how this was found.
                    var into = staging / $"src-{index:00}-{project.Replace('/', '-').Replace('.', '-')}-{source}";

                    into.CreateOrCleanDirectory();

                    DotNetRun(settings => settings
                        .SetProjectFile(compiler)
                        .SetConfiguration(Configuration.Release)
                        .EnableNoRestore()
                        .EnableNoBuild()
                        .SetApplicationArguments(
                            "compile",
                            (directory / $"{source}.rvn").ToString(),
                            into.ToString(),
                            "--target",
                            "spirv",
                            "--emit-reflection",
                            "--no-color"
                        )
                    );

                    foreach (var file in into.GlobFiles("*.spv", "*.reflect.json")
                                 .OrderBy(path => path.Name, System.StringComparer.Ordinal)) {
                        // ⚠ The name carries its directory now, because two projects both produce a
                        // `UiBox.frag.spv` and a flat name would compare the host's module against
                        // the editor's — which differ, and are meant to.
                        produced.Add((file, $"{project}/Shaders/{file.Name}"));
                    }
                }

                var differing = new List<string>();

                foreach (var (file, name) in produced) {
                    var committed = RootDirectory / name;

                    if (UpdateShaders) {
                        file.Copy(committed, ExistsPolicy.FileOverwrite);
                        continue;
                    }

                    if (!committed.FileExists()) {
                        differing.Add($"{name} is not committed");
                        continue;
                    }

                    if (!System.IO.File.ReadAllBytes(committed).AsSpan()
                            .SequenceEqual(System.IO.File.ReadAllBytes(file))) {
                        differing.Add($"{name} differs from what the compiler produces");
                    }
                }

                if (UpdateShaders) {
                    Log.Warning(
                        "The editor's shader modules have been rewritten. `spirv-val --target-env "
                        + "vulkan1.2` over them is worth running: Raven's SPIR-V is checked against the "
                        + "validator in its own tests, but these are the modules the editor loads."
                    );

                    return;
                }

                Assert.True(
                    differing.Count == 0,
                    "The committed editor shader modules are stale:\n  "
                    + string.Join("\n  ", differing)
                    + "\nRun `./build.sh CheckShaders --update-shaders` and read the diff."
                );

                Log.Information("{Count} editor shader modules match their sources.", produced.Count);
            }
        );
}
