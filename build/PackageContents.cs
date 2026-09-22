// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
///     The expected-files check for a package that ships a tool, expressed against the package's own
///     manifest of what it needs rather than against a list somebody keeps up to date.
/// </summary>
/// <remarks>
///     <para>
///         <c>docs/plan/12</c> asks <c>Pack</c> to <i>"validate package contents against an
///         expected-files manifest (a package that silently stops shipping its native asset is a real
///         failure mode)"</i>. ⚠ <b>The expected-files manifest already exists inside every such
///         package and is written by the build:</b> a framework-dependent tool's <c>.deps.json</c> is
///         the exact list of assemblies and per-RID native payloads the host will look for at run
///         time. A hand-written table beside it would be a second source of truth that goes stale the
///         first time a dependency is added — the drift
///         <c>FuzzGateTests.TheNightlyMatrixIsTheRegistry</c> exists to stop one document over — and
///         it would have to be written by reading the very file this reads.
///     </para>
///     <para>
///         ⚠ <b>It is the generalisation of <c>CheckStyleGenIsShippable</c> and would have caught the
///         failure that check was written from.</b> That package shipped <c>Vixen.StyleGen.dll</c>
///         alone, with neither <c>Vixen.Ui.Styling.Utilities.dll</c> nor the <c>.deps.json</c> — and
///         every one of the missing assemblies is named in the <c>.deps.json</c> of the build that
///         produced it. The five files that check names are the five that were missing that day; this
///         asks the same question of whatever the tool needs today.
///     </para>
///     <para>
///         ⚠ <b>The native half is the half nothing else can see.</b> <c>Vixen.Sdk</c> ships one
///         portable CLI whose <c>tools/runtimes/&lt;rid&gt;/native/</c> serves seven RIDs from one
///         package, and <c>CheckCliIsShippable</c> starts it — on the machine that packed it, whose
///         own RID's natives are therefore present. A RID that silently stopped being restored leaves
///         that launch green and every other platform's consumer with a
///         <c>DllNotFoundException</c>. The <c>runtimeTargets</c> section names every one of them.
///     </para>
///     <para>
///         <b>What it deliberately does not require.</b> <c>resources</c> assets — satellite
///         <c>.resources.dll</c> files for localised dependency messages — live in per-culture
///         subdirectories, and both tool packages pack the output's <c>*.dll</c> at the root by
///         design. Requiring them would fail every package for files the packing rules were written
///         not to take. Nothing here is localised through them.
///     </para>
///     <para>
///         ⚠ <b>What it prints on the day it does not run.</b> A package with no <c>tools/</c> is not
///         examined, and a run that examined nothing reports <see cref="PackageContentsReport.Verified" />
///         zero — which the caller reports as skipped rather than as a pass. A <c>tools/</c> with no
///         <c>.deps.json</c> in it is the failure itself, not an excuse to check nothing: that is
///         precisely the shape the shipped <c>Vixen.Ui.Styling.Utilities</c> had.
///     </para>
/// </remarks>
static class PackageContents {
    /// <summary>Checks one package's <c>tools/</c> against the closure its own manifest declares.</summary>
    /// <param name="package">The package's file name, for the messages.</param>
    /// <param name="entries">Every entry path in the archive, with forward slashes.</param>
    /// <param name="open">Opens one entry for reading, by the path as <paramref name="entries" /> gives it.</param>
    public static PackageContentsReport Check(
        string package,
        IEnumerable<string> entries,
        Func<string, Stream> open,
        IReadOnlySet<string>? exempt = null
    ) {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(open);

        var paths = entries.ToList();
        var present = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        var tools = paths
            .Where(path => path.StartsWith("tools/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tools.Count == 0) {
            return new([], 0, false);
        }

        // ⚠ Two layouts, because there are two ways to ship a tool and only one of them is flat.
        // `Vixen.Sdk` packs its CLI by path into `tools/`, so its manifest sits at `tools/x.deps.json`
        // — that is what a host started as `dotnet tools/x.dll` reads. A `PackAsTool` project is
        // packed by NuGet into `tools/<tfm>/<rid>/`, which is the format `dotnet tool install`
        // requires and not a choice the project makes; its manifest is therefore three deep. This
        // used to demand depth one and reported all four of this repository's `PackAsTool` packages
        // — Vixen.Cli, Vixen.ContentServer, Vixen.Raven.Cli, Vixen.ShaderCompilerService — as
        // shipping no manifest at all, on the first CI run that ever reached `pack`.
        //
        // Anything deeper than its own layout is a dependency's manifest carried as content and says
        // nothing about what this package has to ship, which is what the depth test is for.
        var manifests = tools
            .Where(path => path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            .Where(IsToolManifest)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (manifests.Count == 0) {
            return new(
                [
                    $"{package} ships {tools.Count} file(s) under tools/ and no .deps.json among them. "
                    + "A framework-dependent tool cannot start without one — this is the shape "
                    + "Vixen.Ui.Styling.Utilities shipped in when its tools/ held the entry point alone."
                ],
                0,
                true
            );
        }

        var verified = 0;
        var unused = new HashSet<string>(
            exempt ?? (IEnumerable<string>)Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var manifest in manifests) {
            foreach (var required in Required(manifest, open, problems, package)) {
                verified++;

                if (present.Contains(required)) {
                    continue;
                }

                if (unused.Remove(required)) {
                    continue;
                }

                if (exempt is not null && exempt.Contains(required)) {
                    continue;
                }

                problems.Add($"{package} declares {required} in {manifest} and does not carry it");
            }
        }

        // ⚠ An exemption that has stopped being needed is a problem of its own, the way
        // `docs/WhitespaceExempt.txt` and `docs/DocCommentExempt.txt` work: the list can only
        // shrink. A package that started carrying a file somebody wrote a reason for not carrying
        // is a reason that has outlived itself, and an allow-list nobody prunes is how the next
        // genuinely missing file goes unreported.
        foreach (var stale in unused.OrderBy(path => path, StringComparer.Ordinal)) {
            if (present.Contains(stale)) {
                problems.Add(
                    $"{package} carries {stale}, which build/PackedToolExempt.txt still excuses it "
                    + "from carrying. Delete that line: the list may only shrink."
                );
            }
        }

        return new(problems, verified, true);
    }

    /// <summary>Every file one manifest says the tool needs beside it, as a path inside the package.</summary>
    static IEnumerable<string> Required(
        string manifest,
        Func<string, Stream> open,
        List<string> problems,
        string package
    ) {
        var directory = manifest[..(manifest.LastIndexOf('/') + 1)];

        JsonDocument document;

        using (var stream = open(manifest)) {
            try {
                document = JsonDocument.Parse(stream);
            } catch (JsonException exception) {
                problems.Add($"{package}'s {manifest} is not readable JSON: {exception.Message}");

                yield break;
            }
        }

        using (document) {
            if (!document.RootElement.TryGetProperty("targets", out var targets)) {
                problems.Add($"{package}'s {manifest} has no targets section, so it names nothing to check");

                yield break;
            }

            var named = 0;

            foreach (var target in targets.EnumerateObject()) {
                foreach (var library in target.Value.EnumerateObject()) {
                    // The assembly closure. Written as the path inside the dependency package
                    // (`lib/net10.0/X.dll`) and laid out FLAT beside the entry point, because a
                    // framework-dependent build is flat — so the file name is the question.
                    foreach (var asset in Assets(library.Value, "runtime")) {
                        named++;

                        var required = directory + Path.GetFileName(asset);

                        if (IsRequiredToStart(required)) {
                            yield return required;
                        }
                    }

                    // The per-RID payloads, native and managed alike, which keep their own path.
                    foreach (var asset in Assets(library.Value, "runtimeTargets")) {
                        named++;

                        var required = directory + asset;

                        if (IsRequiredToStart(required)) {
                            yield return required;
                        }
                    }
                }
            }

            if (named == 0) {
                problems.Add(
                    $"{package}'s {manifest} names no runtime assets at all. An empty closure is what "
                    + "a comparison over nothing looks like, not a tool with no dependencies."
                );
            }
        }
    }

    /// <summary>Whether a <c>.deps.json</c> at this path is the tool's own rather than a dependency's.</summary>
    /// <remarks>
    ///     <c>tools/x.deps.json</c> is a tool packed by path; <c>tools/&lt;tfm&gt;/&lt;rid&gt;/x.deps.json</c>
    ///     is what <c>PackAsTool</c> produces and what <c>dotnet tool install</c> reads. Nothing else
    ///     under <c>tools/</c> describes this package.
    /// </remarks>
    static bool IsToolManifest(string path) => path.Count(character => character == '/') is 1 or 3;

    /// <summary>
    ///     Whether a file a manifest names has to be in the package for the tool to start.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A <c>.pdb</c> never is, and six of the twenty-one problems the first CI run to reach
    ///     `pack` reported were native debug symbols.</b> <c>runtimeTargets</c> lists whatever the
    ///     dependency package put under <c>runtimes/</c>, symbols included — <c>HarfBuzzSharp</c>
    ///     ships <c>libHarfBuzzSharp.pdb</c> beside each Windows native — and <c>Vixen.Sdk</c> drops
    ///     <c>.pdb</c> deliberately, 67 MB of symbols for an assembly nobody debugs from a package.
    ///     A host loads a native library without its symbols and is none the wiser, so requiring one
    ///     asks a question whose only true answer is "no" and whose failure teaches nothing. This is
    ///     categorical rather than a line in the exemption list, because it is true of every package
    ///     rather than a decision one of them made.
    /// </remarks>
    static bool IsRequiredToStart(string path) =>
        !path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);

    /// <summary>One section's asset paths, with NuGet's empty-folder placeholder dropped.</summary>
    static IEnumerable<string> Assets(JsonElement library, string section) {
        if (!library.TryGetProperty(section, out var assets) || assets.ValueKind != JsonValueKind.Object) {
            yield break;
        }

        foreach (var asset in assets.EnumerateObject()) {
            // `_._` is how NuGet spells "this package deliberately contributes nothing here".
            if (Path.GetFileName(asset.Name) is not "_._") {
                yield return asset.Name.Replace('\\', '/');
            }
        }
    }
}

/// <summary>What one package's <c>tools/</c> check found.</summary>
/// <param name="Problems">Everything wrong, each naming the package and the file.</param>
/// <param name="Verified">How many required files were actually looked for. Zero is a skip, not a pass.</param>
/// <param name="ShipsTools">Whether the package ships a <c>tools/</c> at all.</param>
readonly record struct PackageContentsReport(IReadOnlyList<string> Problems, int Verified, bool ShipsTools);
