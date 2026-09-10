// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Vixen.DocGen;
using Vixen.DocGen.Guide;
using Vixen.Engine.Generators;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     ⚠ The engine's own rules, run over <em>this repository's own</em> compiled guide fences — the
///     check the docs gate did not have.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1238">#1238</a>.</b> A
///         <c>```csharp compile</c> fence is added to a real engine compilation and its compiler
///         diagnostics are read; analyzers are only ever run through <c>WithAnalyzers</c>, and
///         nothing did. <b>So the guide corpus was checked against the compiler and not against this
///         repository's own rules</b>, and every example a shipped analyzer would refuse compiled
///         clean. The one that found it is the page about world serialisation, whose remapping
///         example declared a <c>[Component] [DataContract]</c> struct holding an <c>Entity</c> —
///         <c>VXS0416</c>, an error, on the page about the operation the example exists to explain.
///     </para>
///     <para>
///         ⚠ <b>This is not a second implementation of that gate; it is the same analyzers against a
///         cheaper host.</b> <c>Program.cs</c> runs them inside the workspace's engine compilation,
///         which needs the solution built in Release — so it runs on master and nowhere else, and
///         CLAUDE.md tells agents not to run it. The rules below need three type names to resolve and
///         nothing else, because <c>SerializedHandleAnalyzer</c> asks about a declaration rather than
///         about a call. That fits in a stub compilation and a second.
///     </para>
///     <para>
///         ⚠ <b>The fences do not bind here and are not meant to.</b> A guide example names
///         <c>World</c>, <c>WorldSerializer</c> and whatever else its page is about; none of that is
///         in the stub, and the compilation is full of <c>CS0246</c>. A symbol action still runs over
///         the types the tree declares — which is why an <c>Entity</c> field on a
///         <c>[Component] [DataContract]</c> struct is visible from here at all, and why a rule about
///         a call site would not be.
///     </para>
/// </remarks>
public class RealExampleRuleTests {
    /// <summary>The three names the handle rules bind, and nothing else.</summary>
    /// <remarks>
    ///     Matched by full name, which is all <c>SerializedHandleAnalyzer</c> and
    ///     <c>BehaviorStateAnalyzer</c> look at: they resolve <c>Vixen.Core.Entity</c> and the two
    ///     attributes by metadata name and give up when any is missing. ⚠ That giving-up is why
    ///     <see cref="The_rules_see_the_shape_the_guide_taught" /> exists — a stub renamed by
    ///     accident would turn every case below green.
    /// </remarks>
    const string Stubs =
        """
        namespace Vixen.Core {
            public sealed class ComponentAttribute : System.Attribute;

            public sealed class DataContractAttribute : System.Attribute {
                public DataContractAttribute() { }
                public DataContractAttribute(string alias) { }
            }

            public readonly struct Entity {
                public static readonly Entity Null;
            }
        }

        namespace Vixen.Engine.Behaviors {
            public abstract class Behavior;
        }
        """;

    /// <summary>The checkout this assembly was compiled in — never the outermost one.</summary>
    /// <remarks>
    ///     ⚠ <c>.claude/worktrees/</c> holds a whole checkout per agent, so a walk that kept going
    ///     would leave a worktree's run reading the main tree's guide: green about a corpus this
    ///     branch cannot change and blind to the one it can.
    /// </remarks>
    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null) {
                if (Directory.Exists(Path.Combine(directory.FullName, "docs", "guide"))) {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("No docs/guide above " + AppContext.BaseDirectory + ".");
        }
    }

    static ImmutableArray<DiagnosticAnalyzer> Rules() =>
        Examples.Rules([new SerializedHandleAnalyzer(), new BehaviorStateAnalyzer()]);

    /// <summary>Every fence's diagnostics, from the same wrapping the gate compiles.</summary>
    static async Task<IReadOnlyList<string>> Report(IReadOnlyList<Example> fences) {
        var rules = Rules();
        var stubs = CSharpSyntaxTree.ParseText(Stubs);
        var found = new List<string>();

        for (var index = 0; index < fences.Count; index++) {
            var (source, _) = Examples.Wrap(fences[index], index);
            var tree = CSharpSyntaxTree.ParseText(source);

            var compilation = CSharpCompilation.Create(
                "GuideRules",
                [stubs, tree],
                Net10.References.All,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            found.AddRange(
                (await Examples.AnalyzeAsync(compilation, tree, rules, TestContext.Current.CancellationToken))
                .Select(diagnostic =>
                    $"{fences[index].Page}:{fences[index].Line}: {diagnostic.Id}: {diagnostic.GetMessage()}"));
        }

        return found;
    }

    /// <summary>The fences of this repository's guide that the docs gate compiles.</summary>
    static IReadOnlyList<Example> Compiled() {
        var (pages, _) = GuideReader.Read(
            Root,
            new SourceLinks(Root, "https://github.com/Rikarin/Vixen", commit: null));

        var fences = pages
            .SelectMany(page => page.Examples)
            .Where(example => example is { Compile: true, Language: "csharp" })
            .ToList();

        // The instrument first. An empty corpus is trivially clean, and a walk that found no pages —
        // wrong root, renamed directory — reads exactly like a guide with nothing wrong in it.
        Assert.True(
            fences.Count >= 100,
            $"the guide walk found {fences.Count} compiled C# fences and there were two hundred when "
            + "this was written, so this file is asserting about almost nothing. Check Root."
        );

        return fences;
    }

    /// <summary>⚠ No compiled fence in the guide teaches a shape the engine's analyzers refuse.</summary>
    [Fact]
    public async Task No_compiled_fence_breaks_a_handle_rule() {
        var found = await Report(Compiled());

        Assert.True(
            found.Count == 0,
            "a guide example would not build in a reader's own project:" + Environment.NewLine
            + string.Join(Environment.NewLine, found) + Environment.NewLine
            + "A fence deliberately showing a refused shape says so in the fence, with a "
            + "`#pragma warning disable` naming the issue that removes it — which is documentation of "
            + "the gap rather than of the workaround, and is what Samples/13 does for VXS0413."
        );
    }

    /// <summary>
    ///     ⚠ The detector against the shape it exists for: the example as the guide used to carry it
    ///     is reported.
    /// </summary>
    /// <remarks>
    ///     Without this the case above passes on the day the stubs are renamed, the analyzer stops
    ///     being loaded, or <c>Examples.AnalyzeAsync</c> filters everything out — three ways of
    ///     checking nothing, all of which print a clean corpus.
    /// </remarks>
    [Fact]
    public async Task The_rules_see_the_shape_the_guide_taught() {
        var offender = new Example(
            "docs/guide/engine/world-serialisation.md",
            1,
            "csharp",
            """
            using Vixen.Core;

            [Component]
            [DataContract("GuideFollowTarget")]
            public struct FollowTarget {
                public Entity Value;
            }
            """,
            Compile: true,
            Fragment: false,
            Reason: null
        );

        var found = await Report([offender]);

        Assert.Contains(found, line => line.Contains("VXS0416", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ And the same fence with the <c>#pragma</c> the page now carries is silent, so the
    ///     suppression a reader copies is the one that works.
    /// </summary>
    [Fact]
    public async Task A_pragma_is_what_makes_a_refused_shape_teachable() {
        var suppressed = new Example(
            "docs/guide/engine/world-serialisation.md",
            1,
            "csharp",
            """
            using Vixen.Core;

            [Component]
            [DataContract("GuideFollowTarget")]
            public struct FollowTarget {
            #pragma warning disable VXS0416
                public Entity Value;
            #pragma warning restore VXS0416
            }
            """,
            Compile: true,
            Fragment: false,
            Reason: null
        );

        Assert.Empty(await Report([suppressed]));
    }

    /// <summary>
    ///     ⚠ What the gate says on the day it resolves no analyzers, which is the question that found
    ///     #1238 in the first place.
    /// </summary>
    /// <remarks>
    ///     <c>Program.cs</c> turns a non-empty answer into a reported problem, so a run whose
    ///     <c>@(Analyzer)</c> items did not resolve fails loudly instead of printing a clean corpus.
    /// </remarks>
    [Fact]
    public void An_empty_analyzer_set_is_reported_rather_than_passing() {
        Assert.Equal(Examples.Enforced, Examples.Unreported([]));
        Assert.Empty(Examples.Unreported(Rules()));
    }
}
