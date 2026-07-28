// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Serialization;
using Vixen.Editor.Assets;
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
    /// <returns>Whether the build may continue.</returns>
    public static bool Run(Project project, string backend, string outputDirectory, DiagnosticWriter output) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(backend);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        ArgumentNullException.ThrowIfNull(output);

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

        var sources = Sources(project);

        if (sources.Length == 0) {
            output.Project(
                ImportSeverity.Error,
                DiagnosticCode.Shaders,
                $"{ManifestFileName} names {manifest.Effects.Length} variant(s), and there are no .rvn files under Assets/ to compile them from."
            );

            return false;
        }

        EffectBundleBuilder builder;

        try {
            builder = new(new RavenEffectCompiler(sources, backend, References(project)));
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

    /// <summary>Compiled libraries to bind against, which a project may vendor beside its shaders.</summary>
    static string[] References(Project project) =>
        Directory.Exists(project.Paths.Assets)
            ? [.. Directory.EnumerateFiles(project.Paths.Assets, "*.rvnlib", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]
            : [];
}
