// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Assets.Gameplay;

namespace Vixen.Cli;

/// <summary>Writes the project's addresses out as C# constants.</summary>
/// <remarks>
///     <para>
///         <b>Part of <c>import</c> rather than of <c>content build</c>, and the ordering is the
///         reason.</b> <c>Vixen.Sdk</c> runs the import <c>BeforeTargets="CoreCompile"</c> precisely
///         so that generated C# exists before the compiler reads its inputs, and runs the content
///         build <c>AfterTargets="Build"</c>. A constant emitted by the second is a constant that is
///         one build out of date, every build.
///     </para>
///     <para>
///         ⚠ <b>It plans rather than packs.</b> <see cref="ContentPipeline.Analyse" /> answers the
///         same address list a build would, without writing a bundle — which is what makes this
///         affordable on every compile.
///     </para>
///     <para>
///         ⚠ <b>The file is rewritten only when it changed.</b> Touching it every build would make
///         MSBuild rebuild the project every build, which is how an incremental build stops being
///         one.
///     </para>
/// </remarks>
public static class AddressRunner {
    /// <summary>Plans the project's content and writes the constants beside it.</summary>
    /// <param name="project">The project, already scanned.</param>
    /// <param name="path">Where the file goes.</param>
    /// <param name="namespace">What namespace to put them in.</param>
    /// <param name="ids">Whether to emit a <c>DefId</c> beside each address.</param>
    /// <param name="output">Where to write progress and diagnostics.</param>
    /// <returns>Whether it wrote a usable file.</returns>
    public static bool Run(Project project, string path, string @namespace, bool ids, DiagnosticWriter output) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);

        var failed = false;
        var plan = ContentPipeline.Analyse(project.Workspace, diagnostic => {
                ContentBuildRunner.Write(output, diagnostic);
                failed |= diagnostic.Severity == Editor.Assets.ImportSeverity.Error;
            }
        );

        if (failed) {
            return false;
        }

        var emitted = AddressConstants.Emit(plan.Assets.Select(asset => asset.Address), @namespace, ids: ids);

        foreach (var problem in emitted.Problems) {
            // A warning rather than an error. The constant is missing, so anything that names it
            // fails to compile a moment later with a message about the name somebody actually typed —
            // which is a better place to find out than here.
            output.Line($"  warning {DiagnosticCode.Plan}: {problem}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (directory is { Length: > 0 }) {
            Directory.CreateDirectory(directory);
        }

        // Only when it changed. See the remark on the type: an unconditional write is a rebuild of
        // the whole project on every build.
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), emitted.Source, StringComparison.Ordinal)) {
            File.WriteAllText(path, emitted.Source);

            output.Line(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Wrote {emitted.Count} address {(emitted.Count == 1 ? "constant" : "constants")} to {path}."
                )
            );
        }

        return true;
    }
}
