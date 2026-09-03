// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

namespace Vixen.Platform.Web.Tests;

/// <summary>Every <c>[JSImport]</c> names a function that exists, and every export has a caller.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The <c>[JSImport]</c> boundary is the one place in this repository where the
///         compiler checks nothing at all.</b> The attribute takes a string; the generated
///         marshalling looks that name up in an ES module at run time. A name that is misspelled,
///         renamed on one side, or deleted on the other compiles cleanly, passes
///         <c>CheckApi</c>, <c>CheckFormat</c> and <c>CompileWeb</c>, publishes, and then throws on
///         the first call — which for most of these functions is frame one.
///     </para>
///     <para>
///         <c>nuke BrowserSmoke</c> does catch it, by calling every import against a real head, and
///         one of its verification sabotages was precisely "a <c>[JSImport]</c> pointed at a
///         function the module does not export". But that gate needs a published head and a
///         browser, runs on one CI leg, and takes minutes. This is the same question asked of the
///         text, in milliseconds, on every platform, under <c>nuke Test</c>. It does not replace the
///         smoke test — it cannot tell you a function does the right thing — it replaces
///         <i>finding out in a browser</i> that a name is wrong.
///     </para>
///     <para>
///         <b>Both directions, and the second one is the one that found something.</b> An import
///         with no export is fatal and loud. An export with no import is silent: it is this
///         repository's commonest defect, a finished thing nothing calls. It found
///         <c>lastErrorMessage</c> in <c>vixen-webgpu.js</c> — the function that captures every
///         <c>uncapturederror</c> and <c>device lost</c> message the WebGPU backend produces, with
///         no <c>[JSImport]</c> anywhere to read it. Every diagnostic that would explain a silently
///         broken browser backend was being recorded and thrown away.
///     </para>
/// </remarks>
public sealed class InteropSurfaceTests {
    /// <summary>The three bindings: the C# file holding the imports, and the module they name.</summary>
    public static TheoryData<string, string, string> Bindings => new() {
        { "Vixen.Platform.Web", "WebInterop.cs", "vixen-platform.js" },
        { "Vixen.Audio.Backend.WebAudio", "WebAudioInterop.cs", "vixen-audio.js" },
        { "Vixen.Graphics.WebGPU.Browser", "WebGpuInterop.cs", "vixen-webgpu.js" }
    };

    [Theory]
    [MemberData(nameof(Bindings))]
    public void EveryImportNamesAFunctionTheModuleExports(string project, string interop, string module) {
        var imports = Imports(project, interop);
        var exports = Exports(project, module);

        // ⚠ The instrument first. A regex that matched nothing would make this test pass on any
        // pair of files, including two empty ones — the comparator-that-called-three-empty-
        // manifests-identical failure this repository keeps a note about.
        Assert.True(imports.Count > 10, $"Only {imports.Count} [JSImport]s found in {interop}; the parse is wrong.");
        Assert.True(exports.Count > 10, $"Only {exports.Count} exports found in {module}; the parse is wrong.");

        var missing = imports.Where(name => !exports.Contains(name)).Order().ToList();

        Assert.True(
            missing.Count == 0,
            $"{interop} declares [JSImport]s that {module} does not export, so each one throws on "
            + $"its first call in a browser and no gate but nuke BrowserSmoke would say so: "
            + string.Join(", ", missing)
        );
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public void EveryExportedFunctionHasACaller(string project, string interop, string module) {
        var imports = Imports(project, interop);
        var exports = Exports(project, module);

        var orphans = exports.Where(name => !imports.Contains(name)).Order().ToList();

        Assert.True(
            orphans.Count == 0,
            $"{module} exports functions no [JSImport] in {interop} names, so they are unreachable "
            + "from .NET — the commonest defect in this repository. Either wire them up, or delete "
            + "them: " + string.Join(", ", orphans)
        );
    }

    /// <summary>Every name in a <c>[JSImport("name", ModuleName)]</c> in that file.</summary>
    static HashSet<string> Imports(string project, string file) =>
        [
            .. Regex.Matches(
                    File.ReadAllText(Path.Combine(Root(project), file)),
                    "\\[JSImport\\(\"(?<name>[A-Za-z0-9_]+)\"",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5)
                )
                .Select(match => match.Groups["name"].Value)
        ];

    /// <summary>Every top-level <c>export function</c> in that module.</summary>
    /// <remarks>
    ///     Anchored to the start of a line, because a nested helper is not part of the module's
    ///     surface and an `export` inside a comment is not an export.
    /// </remarks>
    static HashSet<string> Exports(string project, string file) =>
        [
            .. Regex.Matches(
                    File.ReadAllText(Path.Combine(Root(project), "wwwroot", file)),
                    @"^export\s+(?:async\s+)?function\s+(?<name>[A-Za-z0-9_]+)",
                    RegexOptions.Multiline,
                    TimeSpan.FromSeconds(5)
                )
                .Select(match => match.Groups["name"].Value)
        ];

    /// <summary>A browser binding's directory. All three live under <c>Platform/</c>.</summary>
    static string Root(string project) => Path.Combine(RepositoryRoot(), "Platform", project);

    static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory, "Vixen.slnx"))) {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("No Vixen.slnx above the test assembly, so no repository root.");
    }
}
