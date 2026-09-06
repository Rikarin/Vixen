// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ Verifying the instrument behind <c>CheckDocsCoverage</c>, which read half its subject for
///     a week and printed a plausible number for the other half.
/// </summary>
/// <remarks>
///     <para>
///         The reader skipped every baseline line containing <c>-&gt;</c> as "a member, not a type",
///         and <c>Vixen.Ecs.Archetype -&gt; sealed class</c> is how this repository spells a type: the
///         arrow separates the name from the kind exactly as it separates a property from its type.
///         What survived were the type lines that <em>also</em> name a base or an interface, which is
///         2 398 of 4 711 — so the target's own floor, "did this read anything at all", passed at
///         1 000 with 51 % of the tree.
///     </para>
///     <para>
///         That is the reason for the last test here rather than for the first four. A parser test
///         over fixtures says the reader handles the shapes somebody thought of; asking the real
///         baselines how many types they hold is what would have caught a reader that quietly
///         stopped recognising the commonest shape of all.
///     </para>
/// </remarks>
public sealed class PublicApiTypeNamesTests {
    /// <summary>The declaration shapes this repository's baselines actually contain.</summary>
    [Theory]
    [InlineData("Vixen.Ecs.Archetype -> sealed class", "Vixen.Ecs.Archetype")]
    [InlineData("Vixen.Ai.Nodes.Ecs.AiFocus -> struct", "Vixen.Ai.Nodes.Ecs.AiFocus")]
    [InlineData("Vixen.Ai.Diagnostics.AiOverlayStyle -> readonly struct", "Vixen.Ai.Diagnostics.AiOverlayStyle")]
    [InlineData("Vixen.Ai.Diagnostics.AiDebugCategories -> static class", "Vixen.Ai.Diagnostics.AiDebugCategories")]
    [InlineData("Vixen.Ai.Perception.DamageSettings -> sealed record", "Vixen.Ai.Perception.DamageSettings")]
    [InlineData("Vixen.Ai.Perception.IBlackboardBinding -> interface", "Vixen.Ai.Perception.IBlackboardBinding")]
    [InlineData("Vixen.Ai.Perception.PerceptionPredicate -> delegate", "Vixen.Ai.Perception.PerceptionPredicate")]
    [InlineData("Vixen.Ai.FactorSpan -> readonly ref struct", "Vixen.Ai.FactorSpan")]
    [InlineData("Vixen.Ai.Diagnostics.AiDebugCategory -> enum : byte", "Vixen.Ai.Diagnostics.AiDebugCategory")]
    [InlineData("Vixen.Ecs.Entities : System.IDisposable", "Vixen.Ecs.Entities")]
    public void AKindOnTheRightOfTheArrowIsATypeDeclaration(string line, string expected) =>
        Assert.Equal(expected, PublicApiTypeNames.DocumentationId(line));

    /// <summary>
    ///     ⚠ The other half of the same question, and the half that makes the first one worth
    ///     asking: an arrow pointing at a <em>type</em> is a member, so widening the rule to "any
    ///     line with an arrow" would document every getter in the tree.
    /// </summary>
    [Theory]
    [InlineData("Vixen.Ecs.Chunk.Count.get -> int")]
    [InlineData("Vixen.Ecs.Archetype.World.get -> Vixen.Ecs.World")]
    [InlineData("Vixen.Ecs.Archetype.ColumnOf(Vixen.Core.ComponentTypeId id) -> int")]
    [InlineData("const Vixen.Net.Engine.Content.NetworkRulesContent.Label = \"network-rules\" -> string")]
    [InlineData("static Vixen.Platform.MacOS.MacOSAccessibility.Read() -> Vixen.Platform.SystemAccessibility")]
    [InlineData("#nullable enable")]
    public void AMemberIsNotATypeDeclaration(string line) => Assert.Null(PublicApiTypeNames.DocumentationId(line));

    /// <summary>
    ///     ⚠ A nested type inside a generic one, which the regex this replaced could not mangle
    ///     because it was anchored at the end of the string.
    /// </summary>
    /// <remarks>
    ///     Invisible in the direction that matters: the unmangled name simply never matches an
    ///     exemption, so a type whose line has been in <c>DocsExempt.txt</c> all along reads as
    ///     undocumented — and six of them do, one per collection with an enumerator.
    /// </remarks>
    [Theory]
    [InlineData("Vixen.Core.Collections.SmallList<T, TBuffer> -> struct", "Vixen.Core.Collections.SmallList`2")]
    [InlineData(
        "Vixen.Core.Collections.ChunkedArray<T>.Enumerator -> struct",
        "Vixen.Core.Collections.ChunkedArray`1.Enumerator"
    )]
    [InlineData(
        "Vixen.Core.Syntax.SyntaxList<TNode>.Enumerator : System.IDisposable",
        "Vixen.Core.Syntax.SyntaxList`1.Enumerator"
    )]
    public void AGenericCarriesItsArgumentCountRatherThanItsNames(string line, string expected) =>
        Assert.Equal(expected, PublicApiTypeNames.DocumentationId(line));

    /// <summary>
    ///     The floor, asked of the real baselines: how many types are in there, really.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the test the defect needed. Every fixture above passed against the old reader
    ///     too — it had no case for <c>-&gt; sealed class</c> at all, so nobody wrote one — and the
    ///     only way to see that 2 313 types were missing was to count them in the tree. The bound is
    ///     a floor rather than an equality because the number grows with every commit; it sits above
    ///     the failure that happened (2 398) and below the tree (4 711 on 2026-09-06).
    /// </remarks>
    [Fact]
    public void TheRealBaselinesHoldEveryTypeAndNotHalfOfThem() {
        var ids = PublicApiTypeNames
            .BaselinedIds(Baselines().SelectMany(File.ReadAllLines))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            ids.Count > 3500,
            $"read {ids.Count} type(s) out of the repository's PublicAPI baselines, which is too few "
            + "to be this tree. A reader that stops recognising a declaration shape does not fail — "
            + "it returns fewer types, and CheckDocsCoverage then reports success over the ones it "
            + "did not read."
        );

        // Three shapes that were each invisible to the reader this replaced: a plain sealed class, a
        // nested type inside a generic, and a static class nobody had documented.
        Assert.Contains("Vixen.Ecs.Archetype", ids);
        Assert.Contains("Vixen.Core.Collections.ChunkedArray`1.Enumerator", ids);
        Assert.Contains("Vixen.Platform.MacOS.MacOSAccessibility", ids);
    }

    /// <summary>
    ///     <c>CheckDocsCoverage</c>'s own question, asked of the tree this was compiled in and
    ///     costing a fraction of a second: every baselined type has a guide page or a line in
    ///     <c>docs/DocsExempt.txt</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ The same code as the target rather than a second reading of the same rule —
    ///     <c>build/PublicApiTypeNames.cs</c> is compiled into this assembly, not copied — because a
    ///     transcription that drifts in the permissive direction is green while the gate is red,
    ///     which is the worst of the two failures available here. What differs is only the file
    ///     list: the target asks git, and this walks the tree it sits in, skipping build output.
    /// </remarks>
    [Fact]
    public void EveryBaselinedTypeHasAPageOrAnExemption() {
        var documented = PublicApiTypeNames
            .ExemptedIds(File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "DocsExempt.txt")))
            .Concat(GuidePages().SelectMany(page => PublicApiTypeNames.PageIds(File.ReadAllLines(page))))
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = PublicApiTypeNames
            .BaselinedIds(Baselines().SelectMany(File.ReadAllLines))
            .Where(id => !documented.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            $"{uncovered.Count} public type(s) have neither a guide page naming them in `api:` nor a "
            + "line in docs/DocsExempt.txt. CheckDocs says the same thing after an eleven-minute "
            + "Release build; this says it here.\n  T:"
            + string.Join("\n  T:", uncovered)
        );
    }

    static IEnumerable<string> Baselines() =>
        TreeFiles().Where(path => Path.GetFileName(path) is "PublicAPI.Shipped.txt" or "PublicAPI.Unshipped.txt");

    static IEnumerable<string> GuidePages() =>
        TreeFiles().Where(path => path.EndsWith(".md", StringComparison.Ordinal))
            .Where(path => path.Replace('\\', '/').Contains("/docs/", StringComparison.Ordinal));

    /// <summary>
    ///     ⚠ Skipping <c>.claude</c>, which holds a whole checkout of this repository per agent. A
    ///     walk that descends into it reads another agent's copy of these same files and compares
    ///     one version of the tree with another.
    /// </summary>
    /// <remarks>
    ///     ⚠ And the exclusions are matched against the path <em>below the repository root</em>,
    ///     because this checkout is itself inside a <c>.claude/worktrees</c> directory: matching
    ///     absolute paths excluded the whole tree and the walk read nothing, which is the shape of
    ///     failure this file is about.
    /// </remarks>
    static IEnumerable<string> TreeFiles() {
        var root = RepositoryRoot();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        return Directory.EnumerateFiles(root, "*", options)
            .Where(path => !Segments(Path.GetRelativePath(root, path))
                .Any(segment => segment is ".claude" or ".git" or "bin" or "obj" or "artifacts" or "node_modules")
            );
    }

    static IEnumerable<string> Segments(string path) => path.Replace('\\', '/').Split('/');

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
