// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Vixen.Build;

/// <summary>
///     Whether a diff touches what only <c>CheckAot</c> can check: a trim or AOT annotation, a
///     runtime <c>Type.BaseType</c> walk, the trim properties, or the probe itself.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The Roslyn trim analyzer is on for the whole tree and does not model every flow ILC
///         does</b> (#1352). Measured on <c>UiPropertyRegistry.Collect</c>: with
///         <c>NonPublicConstructors</c> on the parameter a <c>dotnet build</c> reports 0 warnings
///         and an ILC publish reports <c>IL2059</c> and <c>IL2072</c> on
///         <c>System.Type.BaseType.get</c>; with the attribute deleted outright the build still
///         reports 0. So an edit to such an annotation passes <c>Test</c> and <c>CheckFormat</c> and
///         is witnessed by nothing local.
///     </para>
///     <para>
///         <b>Decision (made on Jiu's behalf, at Jiu's request, 2026-10-23):</b> <c>CheckAot</c>
///         does not join the default <c>./build.sh</c> chain. It is a NativeAOT publish — about a
///         minute and a half warm, more cold — and it needs a native toolchain the rest of the
///         default chain does not (<c>vswhere</c> and the MSVC linker on Windows, a C toolchain
///         elsewhere), so a default run would fail on a machine that can build and test everything
///         else, and a default people learn to skip is not a gate. CI's three <c>aot</c> legs
///         (<c>ci.yml</c>) stay the per-push witness. Locally, <c>AffectedTests --since</c> runs
///         <c>CheckAot</c> when — and only when — this finds the diff touches its subject, so the
///         one inner-loop command a developer runs on such an edit is the one that publishes it.
///     </para>
///     <para>
///         ⚠ <b>Lines, not files.</b> Every runtime assembly is rooted by the probe, so "a file in
///         a rooted assembly changed" would be every diff. What ILC sees that the analyzer does not
///         is an annotation or a <c>BaseType</c> flow, and those are spelled on the changed lines.
///         A <c>.BaseType</c> in a generator, an analyzer or a test is Roslyn's
///         <c>INamedTypeSymbol.BaseType</c> or a test's own reflection, which no publish compiles,
///         so those paths are skipped.
///     </para>
/// </remarks>
public static class TrimAnnotationDiff {
    /// <summary>The probe <c>CheckAot</c> publishes and runs; every changed line of it counts.</summary>
    public const string ProbeDirectory = "Tools/Vixen.AotProbe/";

    /// <summary>The spellings on a changed line that only an ahead-of-time publish can check.</summary>
    public static IReadOnlyList<string> Tokens { get; } = [
        "DynamicallyAccessedMembers",
        "RequiresUnreferencedCode",
        "RequiresDynamicCode",
        "RequiresAssemblyFiles",
        "UnconditionalSuppressMessage",
        "DynamicDependency",
        "RunClassConstructor",
        ".BaseType",
        "IsAotCompatible",
        "EnableTrimAnalyzer",
        "TrimmerRootAssembly",
        "PublishAot"
    ];

    /// <summary>
    ///     What in a unified diff only <c>CheckAot</c> can check, as <c>path: token</c> lines; empty
    ///     when nothing.
    /// </summary>
    /// <param name="diff">The output of <c>git diff</c> — any context width.</param>
    /// <returns>One entry per file and token, in diff order.</returns>
    public static IReadOnlyList<string> Findings(string diff) {
        ArgumentNullException.ThrowIfNull(diff);

        var findings = new List<string>();
        string? file = null;

        foreach (var raw in diff.Split('\n')) {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("+++ ", StringComparison.Ordinal)) {
                var name = line[4..];
                file = name.StartsWith("b/", StringComparison.Ordinal) ? name[2..] : name;

                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal) || file is null) {
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) {
                file = null;

                continue;
            }

            if (line.Length == 0 || (line[0] != '+' && line[0] != '-')) {
                continue;
            }

            foreach (var finding in OnLine(file, line[1..])) {
                if (!findings.Contains(finding)) {
                    findings.Add(finding);
                }
            }
        }

        return findings;
    }

    /// <summary>The same question about a file that is new and untracked, whose every line is added.</summary>
    /// <param name="path">Its repository-relative path, <c>/</c>-separated.</param>
    /// <param name="text">Its contents.</param>
    /// <returns>One entry per token.</returns>
    public static IReadOnlyList<string> FindingsInNewFile(string path, string text) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(text);

        return [.. text.Split('\n').SelectMany(line => OnLine(path, line)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether a path is one whose lines can be ILC's subject at all.</summary>
    /// <param name="path">Repository-relative, <c>/</c>-separated.</param>
    /// <returns><see langword="true" /> unless it is a generator, an analyzer, a test or the build.</returns>
    public static bool Compiled(string path) {
        ArgumentNullException.ThrowIfNull(path);

        return !path.StartsWith("build/", StringComparison.Ordinal)
            && !path.Split('/').Any(segment => segment.EndsWith(".Generator", StringComparison.Ordinal)
                || segment.EndsWith(".Generators", StringComparison.Ordinal)
                || segment.EndsWith(".Analyzers", StringComparison.Ordinal)
                || segment.EndsWith(".Tests", StringComparison.Ordinal))
            && (path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".vxml", StringComparison.Ordinal)
                || path.EndsWith(".csproj", StringComparison.Ordinal)
                || path.EndsWith(".props", StringComparison.Ordinal)
                || path.EndsWith(".targets", StringComparison.Ordinal));
    }

    static IEnumerable<string> OnLine(string path, string line) {
        // The probe is CheckAot's own subject, so any line of it is.
        if (path.StartsWith(ProbeDirectory, StringComparison.Ordinal)) {
            yield return $"{path}: the probe";

            yield break;
        }

        // A comment names these as often as code does — UiProperty.cs' remarks spell every one —
        // and prose is not ILC's subject.
        if (!Compiled(path) || line.TrimStart().StartsWith("//", StringComparison.Ordinal)) {
            yield break;
        }

        foreach (var token in Tokens.Where(token => line.Contains(token, StringComparison.Ordinal))) {
            yield return $"{path}: {token}";
        }
    }
}
