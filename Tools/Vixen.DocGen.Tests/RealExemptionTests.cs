// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     <c>docs/DocsExempt.txt</c>'s <c>Vixen.Shaders.Generated.*</c> lines, checked against the
///     <c>.reflect.json</c> files that are the only thing able to produce them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the half of <see cref="Coverage.Check" /> that redded master's CI for two
///         days</b> (<a href="https://github.com/Rikarin/Vixen/issues/915">#915</a>). Commit
///         <c>92ed644f</c> deleted the duplicate <c>Ui.rvn</c> under
///         <c>Editor/Vixen.Editor.Host/Shaders</c> and the five <c>.reflect.json</c> beside it —
///         <c>UiBox</c>, <c>UiImage</c>, <c>UiSolid</c>, <c>UiText</c>, <c>UiVertex</c> — and with them
///         the five public
///         <c>Vixen.Shaders.Generated.Ui*Keys</c> classes the generator emitted from them. The
///         surviving copies live in <c>Platform/Vixen.Ui.Desktop</c>, which sets
///         <c>VixenShaderBindingsInternal</c>, so the same five names became internal and left the
///         graph. The five exemption lines stayed, and <c>Docs</c> says a line naming nothing is a
///         failure.
///     </para>
///     <para>
///         ⚠ <b>#915 guessed the cause was machine-dependent generation — a type present on a
///         developer Mac and absent on <c>ubuntu-latest</c> — and that is refuted.</b> The generator
///         reads committed <c>.reflect.json</c> and an MSBuild property; both are in the tree and
///         neither consults the machine. The gate gives the same answer everywhere. What differed was
///         not the platform but the running: <c>Docs</c> needs a Release build of the solution and
///         eleven minutes, so no branch ran it and only CI ever asked.
///     </para>
///     <para>
///         Which is what this file is for, and it is <see cref="RealGuideTests" />'s argument again:
///         the fact is derivable from committed text in well under a second, so it should not take a
///         gate nobody runs to notice. ⚠ A green run here is <em>not</em> a claim that <c>Docs</c> is
///         green — it sees only the generated shader bindings, which is one family out of the whole
///         graph.
///     </para>
/// </remarks>
public class RealExemptionTests {
    const string Suffix = ".reflect.json";

    /// <summary>The prefix every class <c>Vixen.Shaders.Generators</c> emits carries.</summary>
    const string GeneratedPrefix = "T:Vixen.Shaders.Generated.";

    /// <summary>Directories a walk of the checkout must not descend into.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout per parallel agent, so a walk that kept
    ///     going would read another branch's project files and answer about a tree this run cannot
    ///     change. That is the false positive that stopped <c>SharedUiShaderTests</c> reaching this
    ///     tree at all.
    /// </remarks>
    static readonly string[] Skipped = [".git", ".claude", ".nuke", "bin", "obj", "artifacts", "node_modules"];

    /// <summary>The checkout this assembly was compiled in — the nearest root, never the outermost.</summary>
    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            var relative = Coverage.RelativePath.Replace('/', Path.DirectorySeparatorChar);

            while (directory is not null) {
                if (File.Exists(Path.Combine(directory.FullName, relative))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No {Coverage.RelativePath} above {AppContext.BaseDirectory}. This test reads the "
                + "repository it was compiled in, so an output directory outside the checkout breaks it."
            );
        }
    }

    /// <summary>
    ///     Shader names whose generated bindings are <em>public</em>, and so reach the graph.
    /// </summary>
    /// <remarks>
    ///     The two halves of the generator's contract, both read out of the project file:
    ///     <c>AdditionalFiles</c> ending in <c>.reflect.json</c> decide which shaders it emits at all,
    ///     and <c>VixenShaderBindingsInternal</c> decides whether what it emits is visible outside the
    ///     assembly. <see cref="SymbolReader" /> keeps public and protected types only, so an internal
    ///     emission is not in the graph and cannot be exempted.
    /// </remarks>
    static (IReadOnlySet<string> Public, IReadOnlySet<string> Internal) Reflected() {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in Projects(Root)) {
            XDocument document;

            try {
                document = XDocument.Load(project);
            } catch (XmlException) {
                // A project file that is not XML is not this test's finding, and the floor in
                // The_scan_reaches_the_shader_projects is what stops a tree of them passing silently.
                continue;
            }

            var isInternal = document.Descendants("VixenShaderBindingsInternal")
                .Any(element => string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

            var into = isInternal ? hidden : visible;

            foreach (var name in ReflectedBy(project, document)) {
                into.Add(name);
            }
        }

        return (visible, hidden);
    }

    /// <summary>Every shader the project hands the generator, with its <c>Include</c> globs expanded.</summary>
    static IEnumerable<string> ReflectedBy(string project, XDocument document) {
        var directory = Path.GetDirectoryName(Path.GetFullPath(project))!;

        foreach (var item in document.Descendants("AdditionalFiles")) {
            foreach (var include in (item.Attribute("Include")?.Value ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                if (!include.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                // MSBuild writes Windows separators in this repository's project files and resolves
                // them on every platform; a walk that did not would find nothing here on macOS or
                // Linux and say the exemptions were all stale.
                var resolved = Path.GetFullPath(Path.Combine(
                    directory,
                    include.Replace('\\', Path.DirectorySeparatorChar)));

                var folder = Path.GetDirectoryName(resolved);

                if (folder is null || !Directory.Exists(folder)) {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(folder, Path.GetFileName(resolved))) {
                    yield return Path.GetFileName(file)[..^Suffix.Length];
                }
            }
        }
    }

    /// <summary>Every <c>.csproj</c> in the checkout, skipping the directories that are not it.</summary>
    static IEnumerable<string> Projects(string directory) {
        foreach (var child in Directory.EnumerateDirectories(directory)) {
            if (Skipped.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            foreach (var project in Projects(child)) {
                yield return project;
            }
        }

        foreach (var project in Directory.EnumerateFiles(directory, "*.csproj")) {
            yield return project;
        }
    }

    /// <summary>The exemption ids this file is about, with the namespace stripped.</summary>
    static IReadOnlyList<Exemption> GeneratedExemptions() {
        var (entries, _) = Coverage.Read(Root);

        return [.. entries.Where(entry => entry.Id.StartsWith(GeneratedPrefix, StringComparison.Ordinal))];
    }

    /// <summary>
    ///     The walk found the projects and the file, so a green run below is not a run over nothing.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Ask what this prints on the day the scan stops finding a shader.</b> Without this the
    ///     answer is "success" twice over: an empty set of public shaders makes every exemption look
    ///     stale — loud, and at least visible — but an empty set of <em>exemptions</em> makes the
    ///     check below hold vacuously and stay green for ever. Both floors are far below the tree
    ///     (measured 70 shaders and 106 lines) because this is an instrument check and not a census.
    /// </remarks>
    [Fact]
    public void The_scan_reaches_the_shader_projects() {
        var (visible, hidden) = Reflected();
        var exemptions = GeneratedExemptions();

        Assert.True(
            visible.Count > 50,
            $"{Root} yielded {visible.Count} shader(s) with public bindings, which is too few to be "
            + "this repository. The walk has stopped resolving AdditionalFiles, and every line in "
            + $"{Coverage.RelativePath} would look stale."
        );

        Assert.True(
            hidden.Count > 0,
            "No project sets VixenShaderBindingsInternal, so this test cannot tell a shader whose "
            + "bindings are hidden from one that is absent — which is exactly the distinction #915 "
            + "turned on. Platform/Vixen.Ui.Desktop is the project that should be here."
        );

        Assert.True(
            exemptions.Count > 50,
            $"{Coverage.RelativePath} yielded {exemptions.Count} `{GeneratedPrefix}…` line(s), which "
            + "is too few to be this file. The reader has stopped matching and the check below holds "
            + "over nothing."
        );
    }

    /// <summary>
    ///     Every <c>Vixen.Shaders.Generated.*</c> exemption names a shader that still emits public
    ///     bindings.
    /// </summary>
    /// <remarks>
    ///     Prefix rather than equality, because one shader emits several classes and the suffix is
    ///     the generator's business — <c>Keys</c>, <c>Constants</c>, <c>PerFrameConstants</c>, and one
    ///     <c>…Element</c> per array member. Being permissive is the right side to err on: this must
    ///     never contradict <c>Docs</c> by failing on a line <c>Docs</c> accepts.
    /// </remarks>
    [Fact]
    public void Every_generated_exemption_names_a_shader_that_is_still_public() {
        var (visible, hidden) = Reflected();

        var stale = GeneratedExemptions()
            .Where(entry => {
                var type = entry.Id[GeneratedPrefix.Length..];

                return !visible.Any(shader => type.StartsWith(shader, StringComparison.Ordinal));
            })
            .Select(entry => {
                var type = entry.Id[GeneratedPrefix.Length..];

                var reason = hidden.Any(shader => type.StartsWith(shader, StringComparison.Ordinal))
                    ? "its shader is reflected only where VixenShaderBindingsInternal makes the "
                    + "bindings internal, so the class is not in the graph"
                    : "no .reflect.json in the tree is named after it any more";

                return $"{Coverage.RelativePath}:{entry.Line}: `{entry.Id}` — {reason}";
            })
            .ToArray();

        Assert.True(
            stale.Length == 0,
            $"Exemption line(s) naming a generated class the graph does not have. `Docs` fails on "
            + $"each of these, eleven minutes into a Release build:{Environment.NewLine}  "
            + string.Join($"{Environment.NewLine}  ", stale)
            + $"{Environment.NewLine}Delete the line in the commit that removed or hid the shader — "
            + "the exemption list can only shrink."
        );
    }
}
