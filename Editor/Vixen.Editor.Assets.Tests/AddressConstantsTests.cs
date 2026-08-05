// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vixen.Editor.Assets.Gameplay;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

public class AddressConstantsTests {
    const string Namespace = "MyGame";

    /// <summary>Everything loaded beside the test, which is what the generated file compiles against.</summary>
    static readonly ImmutableArray<MetadataReference> References = [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "Vixen.*.dll"))
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
    ];

    static string Emit(params string[] addresses) =>
        AddressConstants.Emit(addresses, Namespace).Source;

    /// <summary>Compiles what the generator wrote and answers whatever the compiler complained of.</summary>
    static string[] Compile(string source) =>
        CSharpCompilation.Create(
                "Generated",
                [CSharpSyntaxTree.ParseText(source)],
                References,
                new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
            )
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

    // ── The shape ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAddressBecomesANestedClassWithItsOwnText() {
        var source = Emit("items/weapons/flamebrand");

        Assert.Contains("public static partial class Items {", source, StringComparison.Ordinal);
        Assert.Contains("public static partial class Weapons {", source, StringComparison.Ordinal);
        Assert.Contains("public static partial class Flamebrand {", source, StringComparison.Ordinal);
        Assert.Contains("public const string Address = \"items/weapons/flamebrand\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddressThatIsAlsoAPrefixOfAnotherWorks() {
        // ⚠ The reason every address gets a class rather than a field on its parent. A field named
        // Greenmarch and a class named Greenmarch cannot both exist, and this content is ordinary.
        var source = Emit("maps/greenmarch", "maps/greenmarch/spawns");

        Assert.Contains("public const string Address = \"maps/greenmarch\";", source, StringComparison.Ordinal);
        Assert.Contains("public const string Address = \"maps/greenmarch/spawns\";", source, StringComparison.Ordinal);
        Assert.Empty(AddressConstants.Emit(["maps/greenmarch", "maps/greenmarch/spawns"], Namespace).Problems);
    }

    [Fact]
    public void ABranchThatIsNotItselfAnAddressCarriesNoText() {
        var source = Emit("items/weapons/flamebrand");

        // Items and Weapons are grouping only. One Address in the whole file.
        Assert.Equal(1, source.Split("public const string Address").Length - 1);
    }

    [Fact]
    public void TheIdIsOptionalAndOffByDefault() {
        // ⚠ Generated code referencing Vixen.Gameplay would not compile for a game that declined the
        // gameplay libraries — from a build step it did not know it had.
        Assert.DoesNotContain("DefId", Emit("items/sword"), StringComparison.Ordinal);

        var withIds = AddressConstants.Emit(["items/sword"], Namespace, ids: true).Source;

        Assert.Contains("using Vixen.Gameplay;", withIds, StringComparison.Ordinal);
        Assert.Contains("public static readonly DefId Id = DefId.From(Address);", withIds, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdIsComputedFromTheStringAndNeverWrittenAsALiteral() {
        // ⚠ The rule. A literal hash is a second implementation of DefId.From, in generated code
        // nobody reads, that goes wrong silently and takes every id in the game with it.
        var source = AddressConstants.Emit(["items/sword"], Namespace, ids: true).Source;

        Assert.Contains("DefId.From(Address)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new DefId(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", source, StringComparison.Ordinal);
    }

    // ── Naming ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("flamebrand", "Flamebrand")]
    [InlineData("frost-edge", "FrostEdge")]
    [InlineData("the_missing_scout", "TheMissingScout")]
    [InlineData("great.sword", "GreatSword")]
    [InlineData("Already Pascal", "AlreadyPascal")]
    [InlineData("sword2", "Sword2")]
    public void ASegmentBecomesAPascalCaseIdentifier(string segment, string expected) =>
        Assert.Equal(expected, AddressConstants.Identifier(segment));

    [Fact]
    public void ALeadingDigitIsPrefixedRatherThanDropped() {
        // Dropping it would turn 'maps/2-crypt' into 'Crypt' and collide it with 'maps/crypt' for a
        // reason nobody reading either address could see.
        Assert.Equal("_2Crypt", AddressConstants.Identifier("2-crypt"));
    }

    [Fact]
    public void AKeywordSegmentNeedsNoEscapingBecausePascalCasingAlreadyPreventsIt() {
        // ⚠ Written as an escaping test, which is how it was discovered that escaping is unreachable:
        // every C# keyword is lowercase and the first letter of every segment is upper-cased, so
        // 'class' becomes 'Class'. A keyword table here would be a dead branch that reads as a
        // handled case.
        var source = Emit("spells/class/warrior");

        Assert.Contains("public static partial class Class {", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ASegmentWithNothingNameableInItIsReportedAndTheRestSurvives() {
        var result = AddressConstants.Emit(["items/---/sword", "items/shield"], Namespace);

        Assert.Equal(1, result.Count);
        Assert.Single(result.Problems);
        Assert.Contains("items/---/sword", result.Problems[0], StringComparison.Ordinal);
        Assert.Contains("Shield", result.Source, StringComparison.Ordinal);
    }

    // ── Clashes ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoAddressesThatBecomeOneNameAreBothRefused() {
        // ⚠ Neither is written. Emitting the first makes the second invisible, and whoever authored
        // it finds out when their rule silently matches nothing — the exact failure a compile-time
        // constant exists to prevent.
        var result = AddressConstants.Emit(["items/frost-edge", "items/frost_edge"], Namespace);

        Assert.Equal(0, result.Count);
        Assert.Single(result.Problems);
        Assert.Contains("Rename one", result.Problems[0], StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Source);
    }

    [Fact]
    public void AClashDoesNotTakeUnrelatedAddressesWithIt() {
        var result = AddressConstants.Emit(
            ["items/frost-edge", "items/frost_edge", "items/sword"],
            Namespace
        );

        Assert.Equal(1, result.Count);
        Assert.Contains("items/sword", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("FrostEdge", result.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void AClashDoesNotRemoveAChildOfTheClashingName() {
        // The class stays because something below it survived; only its own Address goes.
        var result = AddressConstants.Emit(
            ["maps/green-march", "maps/green_march", "maps/green-march/spawns"],
            Namespace
        );

        Assert.Contains("maps/green-march/spawns", result.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"maps/green-march\";", result.Source, StringComparison.Ordinal);
    }

    // ── Determinism ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheSameAddressesInAnyOrderProduceTheSameBytes() {
        // Doc 12 gates the content build on producing identical bytes across three operating systems,
        // and a generated file that reordered itself per run would fail that for no findable reason.
        var one = Emit("z/last", "a/first", "m/middle");
        var two = Emit("m/middle", "z/last", "a/first");

        Assert.Equal(one, two);
    }

    [Fact]
    public void AnAddressGivenTwiceIsWrittenOnce() {
        var result = AddressConstants.Emit(["items/sword", "items/sword"], Namespace);

        Assert.Equal(1, result.Count);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ProblemsComeBackInOrder() {
        var result = AddressConstants.Emit(["z/---/one", "a/---/two"], Namespace);

        Assert.Equal(2, result.Problems.Length);
        Assert.Contains("a/---/two", result.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NoAddressesWritesNoFileRatherThanAnEmptyClass() {
        var result = AddressConstants.Emit([], Namespace);

        Assert.Equal(string.Empty, result.Source);
        Assert.Equal(0, result.Count);
    }

    // ── The file itself ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFileSaysItIsGeneratedAndWhereItCameFrom() {
        var source = Emit("items/sword");

        Assert.StartsWith("// <auto-generated/>", source, StringComparison.Ordinal);
        Assert.Contains("Edits are lost on the next one.", source, StringComparison.Ordinal);
        Assert.Contains("#nullable enable", source, StringComparison.Ordinal);
        Assert.Contains("namespace MyGame;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryClassIsPartialSoAGameCanAddToIt() {
        // A game that wants Addresses.Items.Sword.Icon writes it in its own file rather than in one
        // the next build overwrites.
        var source = Emit("items/sword");

        Assert.DoesNotContain("public static class ", source, StringComparison.Ordinal);
        Assert.Contains("public static partial class Addresses {", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBraceIsClosed() {
        var source = AddressConstants.Emit(
            ["items/weapons/flamebrand", "items/weapons/frost-edge", "maps/greenmarch", "maps/greenmarch/spawns"],
            Namespace,
            ids: true
        ).Source;

        Assert.Equal(source.Count(character => character == '{'), source.Count(character => character == '}'));
    }

    // ── The one that matters ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WhatItWroteCompiles() {
        // Structural assertions over a string catch a missing brace and nothing else. A duplicate
        // member, an identifier that is not one, a nested class shadowing its parent — every one of
        // those reaches a *game's* build rather than this one, which is the worst place to find a
        // generator bug.
        var source = AddressConstants.Emit(
            [
                "items/weapons/flamebrand",
                "items/weapons/frost-edge",
                "items/2-handed/maul",
                "maps/greenmarch",
                "maps/greenmarch/spawns",
                "maps/greenmarch/spawns/boars",
                "quests/greenmarch/the-missing-scout",
                "spells/class/warrior",
                "effects/burning.stack"
            ],
            Namespace,
            ids: true
        ).Source;

        Assert.Empty(Compile(source));
    }

    [Fact]
    public void WhatItWroteCompilesWithoutTheGameplayLibrariesToo() {
        // The default. A game that declined doc 28 still gets its addresses, and the file it gets has
        // no `using Vixen.Gameplay;` to fail on.
        Assert.Empty(Compile(Emit("maps/greenmarch", "prefabs/props/barrel", "art/ui/icons/sword")));
    }
}
