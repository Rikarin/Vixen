// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Assets.Content;

namespace Vixen.Cli;

/// <summary>Turns an imported project into a directory a game or a CDN can be pointed at.</summary>
/// <remarks>
///     ⚠ <b>The build itself is <see cref="ContentPipeline" />'s, in <c>Vixen.Editor.Assets</c>, and
///     what is left here is the console.</b> The editor builds content too, and two orchestrations
///     over the same components drift — the way this one would drift is the editor and
///     <c>vixen content build</c> producing different output for one project, which reads as a machine
///     problem for as long as it takes somebody to compare two catalogs by hand.
/// </remarks>
public static class ContentBuildRunner {
    /// <summary>What the build wrote, beside the catalog, so a static host needs nothing else.</summary>
    public const string HashFileSuffix = ContentPipeline.HashFileSuffix;

    /// <summary>The catalog's file name, which is what a runtime and a server both expect.</summary>
    public const string CatalogFileName = ContentPipeline.CatalogFileName;

    /// <summary>Plans, packs and writes a content build.</summary>
    /// <param name="project">The project, already imported.</param>
    /// <param name="target">Which build target — <c>Windows</c>, <c>Android/Vulkan</c>.</param>
    /// <param name="outputDirectory">Where to write it.</param>
    /// <param name="output">Where to write progress and diagnostics.</param>
    /// <returns>Whether it produced a build.</returns>
    public static bool Run(Project project, string target, string outputDirectory, DiagnosticWriter output) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);

        var built = ContentPipeline.Build(project.Workspace, target, outputDirectory, diagnostic => Write(output, diagnostic));

        if (!built.Succeeded) {
            return false;
        }

        output.Line(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Built {built.Addresses} {Plural(built.Addresses, "address", "addresses")} into "
                + $"{built.Bundles} {Plural(built.Bundles, "bundle", "bundles")} ({built.Bytes:N0} bytes) "
                + $"for {target}, at {built.OutputDirectory}."
            )
        );

        return true;
    }

    /// <summary>Puts one of the pipeline's diagnostics on the console.</summary>
    /// <param name="output">Where to write it.</param>
    /// <param name="diagnostic">What was said.</param>
    /// <remarks>
    ///     The stage decides the code, and whether the diagnostic names an asset decides which of the
    ///     writer's two forms it takes — MSBuild attributes a line with no file to the project.
    /// </remarks>
    public static void Write(DiagnosticWriter output, ContentDiagnostic diagnostic) {
        ArgumentNullException.ThrowIfNull(output);

        var code = diagnostic.Stage switch {
            ContentStage.Scan => DiagnosticCode.Scan,
            ContentStage.Import => DiagnosticCode.Import,
            ContentStage.Plan => DiagnosticCode.Plan,
            _ => DiagnosticCode.Pack
        };

        if (diagnostic.Path.Length == 0) {
            output.Project(diagnostic.Severity, code, diagnostic.Message);
            return;
        }

        output.Asset(diagnostic.Severity, diagnostic.Path, code, diagnostic.Message);
    }

    static string Plural(int count, string one, string many) => count == 1 ? one : many;
}
