// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Shading;
using Vixen.Rendering.Materials;
using Vixen.ShaderCompiler;
using Vixen.Shaders;

namespace Vixen.Cli;

/// <summary>Compiles a project's shader variants into the bundle a shipping build loads.</summary>
/// <remarks>
///     <para>
///         The third cache tier, made a product of <c>vixen build</c> rather than a library call. A
///         shipping build's only effect source is this file; if a variant is not in it, the run
///         reports a miss and draws nothing, because the code that could have compiled one was never
///         linked in.
///     </para>
///     <para>
///         <strong>Driven by a manifest, and deliberately not by "compile everything".</strong>
///         Enumerating every shader in a project sounds more complete and is not: a pass with
///         <c>compose</c> slots does not compile at all without something in them (RVN2073), so
///         "every variant of ForwardPlus" is not a well-formed question — every variant of
///         ForwardPlus <em>with these features</em> is, and which features a project has is in its
///         materials rather than in its shaders. The manifest is where that lands, and
///         <see cref="EffectSystem.Requests" /> is what fills it in: play the game against a
///         compiler, write the list, build.
///     </para>
///     <para>
///         Silence when there is no manifest, with a line saying how to make one. A project that has
///         not got to this step yet still builds and still runs — against a compiler in development
///         — and telling it that its shaders failed would be wrong.
///     </para>
/// </remarks>
public static class ShaderBuildRunner {
    /// <summary>What the bundle is called, beside the catalog.</summary>
    /// <remarks>
    ///     A sibling of <c>catalog.bin</c> rather than an addressed chunk, for the reason the catalog
    ///     is one: it has to be loadable before anything addressable can be, and an address is a thing
    ///     the catalog provides.
    /// </remarks>
    public const string BundleFileName = "shaders.effects";

    /// <summary>Where the manifest lives, under <c>ProjectSettings/</c>.</summary>
    /// <remarks>
    ///     Committed, because it is a build input people edit, review in a diff and merge when two
    ///     branches each add a material — and not under <c>Assets/</c>, because it is not content and
    ///     should not acquire a <c>.meta</c> and an address.
    /// </remarks>
    public const string ManifestFileName = "Shaders.effects.json";

    /// <summary>The backend a target wants unless something says otherwise.</summary>
    public const string DefaultBackend = "spirv";

    /// <summary>Compiles the manifest and writes the bundle.</summary>
    /// <param name="project">The project.</param>
    /// <param name="backend">Which Raven target — <c>spirv</c> or <c>glsl</c>.</param>
    /// <param name="outputDirectory">Where the content build wrote its catalog.</param>
    /// <param name="output">Where to write progress and diagnostics.</param>
    /// <param name="forServer">
    ///     Whether this is a dedicated server's build, which compiles no variants at all.
    /// </param>
    /// <returns>Whether the build may continue.</returns>
    /// <remarks>
    ///     ⚠ <b>The server profile skips the whole bundle rather than a subset of the manifest, and
    ///     that is the safe shape of it.</b> A dedicated server runs <c>Vixen.Graphics.Null</c> and
    ///     creates no pipeline, so every variant in the manifest is dead weight — and the bundle is a
    ///     sibling file rather than an addressed chunk, so its absence cannot leave the catalog naming
    ///     something that is not there. It is already an ordinary state besides: a project with no
    ///     manifest writes no bundle, and <c>ContentMount</c> reports "No baked shaders" once and
    ///     boots.
    ///     <para>
    ///         The alternative — compiling the manifest with some permutations dropped — is the trap
    ///         this repository has already been caught by: a value in <c>Permutations</c> that is not
    ///         also in <c>PermutationKeys[shader]</c> never reaches the compiler, and the variant
    ///         silently takes the <c>.rvn</c> default rather than failing. Not compiling is
    ///         checkable; compiling less is not.
    ///     </para>
    /// </remarks>
    public static bool Run(
        Project project,
        string backend,
        string outputDirectory,
        DiagnosticWriter output,
        bool forServer = false
    ) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(backend);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(output);

        if (forServer) {
            // ⚠ And a bundle a previous client build left in this directory is removed, for the
            // reason ContentPipeline deletes stale *.bundle files: an output directory is what
            // somebody copies into an image, and one carrying a shader bundle from the build before
            // it is a server image shipping a client's shaders while its log says it has none.
            var stale = Path.Combine(outputDirectory, BundleFileName);

            if (File.Exists(stale)) {
                File.Delete(stale);
            }

            output.Project(
                ImportSeverity.Information,
                DiagnosticCode.Shaders,
                $"This is a server build, so no {BundleFileName} was compiled: a dedicated server runs the null "
                + "graphics backend and creates no pipeline."
            );

            return true;
        }

        var manifestPath = Path.Combine(project.Paths.ProjectSettings, ManifestFileName);

        if (!File.Exists(manifestPath)) {
            output.Project(
                ImportSeverity.Information,
                DiagnosticCode.Shaders,
                $"No {ManifestFileName} in ProjectSettings/, so no shader bundle was built. Write one from "
                + "EffectSystem.Requests after a development run to make this build compile them ahead of time."
            );

            return true;
        }

        EffectManifest manifest;

        try {
            manifest = EffectManifest.Parse(File.ReadAllText(manifestPath));
        } catch (Exception failure) when (failure is IOException or InvalidDataException or System.Text.Json.JsonException) {
            output.Project(ImportSeverity.Error, DiagnosticCode.Shaders, $"{ManifestFileName} is not readable: {failure.Message}");
            return false;
        }

        if (manifest.Effects.Length == 0) {
            output.Project(ImportSeverity.Information, DiagnosticCode.Shaders, $"{ManifestFileName} names no variants.");
            return true;
        }

        List<(string Name, string Text)> sources = [];

        // ⚠ The engine's library first, and its absence is why this runner could not bake a shader
        // that imports one. A package's declarations are visible to a sibling file only within one
        // compilation, so a project shader — or a graph's generated surface, which always imports
        // `Vixen.Shaders.Material` — needs the library's files in the same compilation rather than a
        // reference to a built artefact. `EditorEffects` has done this since it was written; this
        // enumerated `Assets/**/*.rvn` alone, so the editor could compile a shader the build could
        // not.
        foreach (var file in Library()) {
            try {
                sources.Add((file, File.ReadAllText(file)));
            } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
                output.Project(ImportSeverity.Error, DiagnosticCode.Shaders, $"Could not read {file}: {failure.Message}");

                return false;
            }
        }

        foreach (var file in Sources(project)) {
            try {
                sources.Add((file, File.ReadAllText(file)));
            } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
                output.Project(ImportSeverity.Error, DiagnosticCode.Shaders, $"Could not read {file}: {failure.Message}");

                return false;
            }
        }

        // ⚠ And the project's shader graphs, which are shader sources that are not files. A graph
        // emitting a material surface was invisible to this runner for as long as it enumerated
        // `*.rvn` alone, so a shipping build's bundle held every hand-written variant and none of
        // the authored ones — and the miss surfaced at run time as a draw that does not happen.
        foreach (var graph in ShaderGraphSources.All(project.Paths.Assets)) {
            foreach (var diagnostic in graph.Diagnostics) {
                // An error, unlike the manifest's missing-variant warning below: a graph that does
                // not compile is a material that cannot draw, and a build is where that is cheap to
                // find out.
                output.Project(
                    ImportSeverity.Error,
                    DiagnosticCode.Shaders,
                    $"{Path.GetFileName(graph.Path)}: {diagnostic}"
                );
            }

            if (graph.Compiled) {
                sources.Add((graph.Path, graph.Text));
            }
        }

        if (sources.Count == 0) {
            output.Project(
                ImportSeverity.Error,
                DiagnosticCode.Shaders,
                $"{ManifestFileName} names {manifest.Effects.Length} variant(s), and there are no .rvn files or shader graphs under Assets/ to compile them from."
            );

            return false;
        }

        EffectBundleBuilder builder;

        try {
            // ⚠ With the library's own defaults, and without them nothing compiles at all once the
            // library is in the compilation: every slot the sources declare has to be bound, so
            // asking for a project's own `Tint` — which has no slots — still has to satisfy
            // `ForwardPlus.shading` and the eight of `CompositeSurface`. A key's own bindings win
            // over these, which is what keeps a material's chosen shading model its own.
            // `EditorEffects` makes exactly this pairing and for exactly this reason.
            builder = new(
                RavenEffectCompiler.FromSources(
                    sources,
                    backend,
                    References(project),
                    MaterialCompiler.PassComposition()
                )
            );
            builder.Add(manifest);
        } catch (ShaderCompilationException failure) {
            // Every diagnostic, not the first: a shader that does not compile usually says several
            // true things about itself and fixing one at a time is a build per mistake.
            foreach (var diagnostic in failure.Diagnostics) {
                output.Project(ImportSeverity.Error, DiagnosticCode.Shaders, diagnostic);
            }

            return false;
        } catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException) {
            output.Project(ImportSeverity.Error, DiagnosticCode.Shaders, failure.Message);
            return false;
        }

        // A named variant nobody can compile is a warning rather than an error, because the usual
        // cause is a manifest older than the material it was captured from — and failing the build
        // for a line somebody can delete would be the wrong trade. The run that needs it will report
        // it as a miss, by the same name.
        foreach (var missing in builder.Missing) {
            output.Project(
                ImportSeverity.Warning,
                DiagnosticCode.Shaders,
                $"{ManifestFileName} names '{missing}', and no shader in this project answers to it."
            );
        }

        var path = Path.Combine(outputDirectory, BundleFileName);

        try {
            Directory.CreateDirectory(outputDirectory);
            builder.Write(path);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            output.Project(ImportSeverity.Error, DiagnosticCode.Shaders, $"Could not write {BundleFileName}: {failure.Message}");
            return false;
        }

        output.Line(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Compiled {builder.Count} shader variant{(builder.Count == 1 ? "" : "s")} for {backend} "
                + $"({new FileInfo(path).Length:N0} bytes), at {path}."
            )
        );

        return true;
    }

    /// <summary>Every <c>.rvn</c> under <c>Assets/</c>, in a stable order.</summary>
    /// <remarks>
    ///     Ordered because the source hash every artefact carries is taken over the texts in the order
    ///     they were read, and a hash that depended on a directory enumeration would make a cache
    ///     entry stale on a machine whose filesystem sorted differently.
    /// </remarks>
    static string[] Sources(Project project) =>
        Directory.Exists(project.Paths.Assets)
            ? [.. Directory.EnumerateFiles(project.Paths.Assets, "*.rvn", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Where the engine's shader library sits beside the CLI.</summary>
    /// <inheritdoc cref="Library" path="/remarks" />
    public const string LibraryFolder = "Shaders/Library";

    /// <summary>Every <c>.rvn</c> of the engine's own library, in a stable order.</summary>
    /// <remarks>
    ///     ⚠ <b>The package directories, not the folder itself</b> — which is how
    ///     <c>EditorEffects.Sources</c> and <c>LibraryReflectionTests</c> both enumerate it, and for
    ///     the same reason: <c>Example1.rvn</c> sits at the library's root and imports packages the
    ///     library does not have, so including it fails every variant in the compilation rather than
    ///     only itself.
    /// </remarks>
    static IEnumerable<string> Library() {
        var library = Path.Combine(AppContext.BaseDirectory, LibraryFolder.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(library)) {
            yield break;
        }

        foreach (var package in Directory.EnumerateDirectories(library).Order(StringComparer.Ordinal)) {
            foreach (var file in Directory.EnumerateFiles(package, "*.rvn", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal)) {
                yield return file;
            }
        }
    }

    /// <summary>Compiled libraries to bind against, which a project may vendor beside its shaders.</summary>
    static string[] References(Project project) =>
        Directory.Exists(project.Paths.Assets)
            ? [.. Directory.EnumerateFiles(project.Paths.Assets, "*.rvnlib", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
            : [];
}
