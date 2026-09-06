// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vixen.Shaders.Generators;
using Xunit;

namespace Tests;

/// <summary>
///     ⚠ Verifying the instrument: <c>docs/DocsExempt.txt</c>'s lines for the generated shader
///     bindings, held to what the generator actually emits.
/// </summary>
/// <remarks>
///     <para>
///         <c>CheckDocs</c> fails on three things, and the one nobody can see coming is
///         <em>"a line names a type the graph does not have"</em>. Finding that out costs a Release
///         compile of the whole solution — eleven minutes, which CLAUDE.md tells every agent not to
///         spend per branch — so a line goes stale on one commit and is discovered on master by
///         whoever merges next. It has been discovered that way repeatedly
///         (<a href="https://github.com/Rikarin/Vixen/issues/480">#480</a>), and the last time it
///         reached CI and stayed red for fifteen consecutive runs
///         (<a href="https://github.com/Rikarin/Vixen/issues/915">#915</a>).
///     </para>
///     <para>
///         ⚠ <b>For one block of that file the question needs no compilation at all, and that is
///         what this is.</b> The <c>Vixen.Shaders.Generated.*</c> types are a pure function of two
///         committed inputs: the <c>.reflect.json</c> beside a shader, and whether the project that
///         owns it sets <c>VixenShaderBindingsInternal</c>. So the exact set of type names the
///         graph will contain can be produced here, from the same emitter the build runs, in under
///         a second — and every exemption line naming one of them checked against it.
///     </para>
///     <para>
///         ⚠ <b>The failure this was written from was not a machine difference, though it was
///         reported as one.</b> <c>92ed644f</c> set <c>VixenShaderBindingsInternal</c> on
///         <c>Platform/Vixen.Ui.Desktop</c>, which turned five emitted classes from <c>public</c>
///         to <c>internal</c> in one line of a csproj — and <c>Vixen.DocGen</c>'s
///         <c>SymbolReader</c> keeps only public and protected symbols, so <c>UiBoxKeys</c> and its
///         four siblings left the graph
///         while their exemption lines stayed. The lines were stale on every machine from that
///         commit onward; Linux was merely where somebody read the log.
///     </para>
///     <para>
///         What this does <em>not</em> replace: <c>CheckDocs</c> resolves every <c>api:</c> id
///         against the whole graph, compiles every <c>compile</c> fence and fails on an unlinked
///         page, none of which is possible without the solution. Green here is a claim about one
///         block of one file, which is the block that has gone stale.
///     </para>
/// </remarks>
public class ExemptedGeneratedTypeTests {
    const string Prefix = "T:" + BindingsEmitter.Namespace + ".";

    /// <summary>The msbuild property behind the generator's option, without the analyzer's prefix.</summary>
    static readonly string InternalProperty =
        ShaderBindingsGenerator.InternalProperty["build_property.".Length..];

    /// <summary>A top-level <c>public</c> declaration in the emitted source, which is a graph node.</summary>
    static readonly Regex Declaration = new(
        @"^public (?:static |sealed |readonly |partial )*(?:class|struct|interface|enum|record) ([A-Za-z0-9_]+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant
    );

    /// <summary>
    ///     The checkout this assembly was compiled in.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The nearest root and never the outermost one.</b> <c>.claude/worktrees/</c> holds a
    ///     whole checkout per agent, so a walk that kept going would leave a worktree's test
    ///     asserting about the main tree's exemption list — a file the run cannot change — and
    ///     missing the one it can.
    /// </remarks>
    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null) {
                if (File.Exists(Path.Combine(directory.FullName, "docs", "DocsExempt.txt"))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No docs/DocsExempt.txt above {AppContext.BaseDirectory}. This test reads the "
                + "repository it was compiled in, so an output directory outside the checkout breaks it."
            );
        }
    }

    /// <summary>Source, rather than a copy of it: another agent's checkout, or a build output.</summary>
    /// <remarks>
    ///     ⚠ <b>The path is relative to the root and this is not a tidiness point.</b> An agent's
    ///     own checkout <em>is</em> <c>…/.claude/worktrees/&lt;name&gt;/</c>, so a filter applied to
    ///     the absolute path rejects every file in the very tree it was meant to keep — silently,
    ///     because "nothing matched" and "nothing to check" look identical from outside. That is
    ///     what <see cref="The_walk_reaches_the_shaders_and_the_exemptions" /> is for, and it caught
    ///     exactly this while the filter was being written.
    /// </remarks>
    internal static bool IsSource(string relativePath) =>
        !relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment is ".claude" or "bin" or "obj" or "artifacts" or "node_modules");

    static List<string> ReflectionFiles() =>
        Directory.EnumerateFiles(Root, "*.reflect.json", SearchOption.AllDirectories)
            .Where(path => IsSource(Path.GetRelativePath(Root, path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     Whether the project owning a reflection file asks the generator for internal bindings.
    /// </summary>
    /// <remarks>
    ///     The nearest <c>.csproj</c> above the file, because that is the compilation the generator
    ///     runs in — <c>AdditionalFiles</c> globs live in the same project as the shaders they name.
    /// </remarks>
    static bool EmitsInternalBindings(string reflectionFile) {
        var directory = new DirectoryInfo(Path.GetDirectoryName(reflectionFile)!);

        while (directory is not null && directory.FullName.Length >= Root.Length) {
            var project = directory.EnumerateFiles("*.csproj").FirstOrDefault();

            if (project is not null) {
                return XDocument.Load(project.FullName)
                    .Descendants()
                    .Any(element =>
                        element.Name.LocalName == InternalProperty
                        && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
            }

            directory = directory.Parent;
        }

        return false;
    }

    /// <summary>Every type the generator will put in the documentation graph, by name.</summary>
    static HashSet<string> PublicGeneratedTypes() {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in ReflectionFiles()) {
            var shader = Path.GetFileName(file)[..^".reflect.json".Length];
            var source = BindingsEmitter.Emit(
                shader,
                ReflectionReader.Read(File.ReadAllText(file)),
                Path.GetFileName(file),
                EmitsInternalBindings(file)
            );

            foreach (Match match in Declaration.Matches(source)) {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    /// <summary>The type names <c>docs/DocsExempt.txt</c> excuses in the generated namespace.</summary>
    static List<string> ExemptedGeneratedTypes() =>
        File.ReadAllLines(Path.Combine(Root, "docs", "DocsExempt.txt"))
            .Where(line => line.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(line => line[Prefix.Length..].Split(' ', '\t')[0])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     The filter is relative, so the checkout's own path cannot exclude the checkout.
    /// </summary>
    [Theory]
    [InlineData("Platform/Vixen.Ui.Desktop/Shaders/UiBox.reflect.json", true)]
    [InlineData("Raven/Library/PostFx/Tonemap.reflect.json", true)]
    [InlineData(".claude/worktrees/other/Raven/Library/PostFx/Tonemap.reflect.json", false)]
    [InlineData("Core/Vixen.Shaders.Tests/bin/Debug/net10.0/Fixtures/Lighting.reflect.json", false)]
    [InlineData("Core/Vixen.Shaders/obj/Release/Lighting.reflect.json", false)]
    public void Only_this_checkouts_own_shaders_are_read(string path, bool expected) =>
        Assert.Equal(expected, IsSource(path.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The walk found both halves, so a green run below is not a run over nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what this file prints on the day it stops reading the repository.</b> Without
    ///     this, the answer is "success": an empty exemption list is a subset of anything, and an
    ///     empty emitted set would be too if the list were empty with it. The floors are
    ///     deliberately far below the real counts — this is an instrument check and not a census,
    ///     and a census would be an exact-equality claim over a set every shader grows.
    /// </remarks>
    [Fact]
    public void The_walk_reaches_the_shaders_and_the_exemptions() {
        Assert.True(
            ReflectionFiles().Count > 40,
            $"{Root} yielded {ReflectionFiles().Count} .reflect.json files, which is too few to be "
            + "this repository's shaders. The walk has stopped reaching them and the assertion "
            + "below would pass over nothing."
        );

        Assert.True(
            ExemptedGeneratedTypes().Count > 50,
            $"docs/DocsExempt.txt yielded {ExemptedGeneratedTypes().Count} lines in "
            + $"{BindingsEmitter.Namespace}, which is too few to be the generated block. The reader "
            + "has stopped matching and the assertion below would pass over nothing."
        );
    }

    /// <summary>
    ///     ⚠ The direction that costs eleven minutes to ask any other way: an exemption line naming
    ///     a type the graph will not have.
    /// </summary>
    [Fact]
    public void An_exempted_generated_type_is_still_emitted_and_still_public() {
        var emitted = PublicGeneratedTypes();
        var stale = ExemptedGeneratedTypes().Where(name => !emitted.Contains(name)).ToList();

        Assert.True(
            stale.Count == 0,
            "These lines in docs/DocsExempt.txt name types the documentation graph will not have, "
            + "so CheckDocs fails on them — and it is the whole solution's Release build away, which "
            + "is why they reach master. Either the shader's .reflect.json was renamed or deleted, "
            + "or the project owning it now sets VixenShaderBindingsInternal, which turns the "
            + "emitted classes internal and takes them out of the graph. Delete the lines in the "
            + "commit that does it — the file may only shrink.\n  "
            + string.Join("\n  ", stale.Select(name => Prefix + name))
        );
    }
}
