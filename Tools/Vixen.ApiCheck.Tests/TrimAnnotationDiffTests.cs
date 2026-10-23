// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Build;
using Xunit;

namespace Vixen.ApiCheck.Tests;

/// <summary>
///     ⚠ <c>AffectedTests --since</c> runs <c>CheckAot</c> for exactly the diffs only it can check
///     (#1352): a trim or AOT annotation, a run-time <c>Type.BaseType</c> walk, or the probe.
/// </summary>
/// <remarks>
///     #1352's measured case is the first fact: the attribute on <c>UiPropertyRegistry.Collect</c>
///     narrowed from <c>All</c> to <c>NonPublicConstructors</c>, which <c>dotnet build</c> reports
///     nothing about and ILC reports as <c>IL2059</c> and <c>IL2072</c>.
/// </remarks>
public sealed class TrimAnnotationDiffTests {
    const string Collect = """
        diff --git a/Core/Vixen.Ui/UiProperty.cs b/Core/Vixen.Ui/UiProperty.cs
        index 8a0a96c49..a0a1ca29e 100644
        --- a/Core/Vixen.Ui/UiProperty.cs
        +++ b/Core/Vixen.Ui/UiProperty.cs
        @@ -390 +390 @@ internal static IReadOnlyList<UiPropertyKey> DeclaredBy(Type type) {
        -        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        +        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type type,
        """;

    [Fact]
    public void An_annotation_narrowed_in_a_published_assembly_is_found() {
        Assert.Equal(["Core/Vixen.Ui/UiProperty.cs: DynamicallyAccessedMembers"], TrimAnnotationDiff.Findings(Collect));
    }

    /// <summary>A new <c>BaseType</c> walk is the flow the analyzer does not model, annotated or not.</summary>
    [Fact]
    public void A_new_base_type_walk_is_found_without_any_annotation() {
        const string diff = """
            +++ b/Editor/Vixen.Editor.Ui/Menus/EditorArt.cs
            @@ -74,0 +75 @@
            +        for (var current = type; current is not null; current = current.BaseType) {
            """;

        Assert.Equal(["Editor/Vixen.Editor.Ui/Menus/EditorArt.cs: .BaseType"], TrimAnnotationDiff.Findings(diff));
    }

    /// <summary>
    ///     ⚠ The negative: a <c>BaseType</c> in a generator is Roslyn's symbol, a context line is not
    ///     a change, and a test is never published — so none of them buys a publish.
    /// </summary>
    [Fact]
    public void A_generator_a_test_and_an_unchanged_line_are_not_its_subject() {
        const string diff = """
            +++ b/Core/Vixen.Ui.Generators/UiPropertyGenerator.cs
            @@ -10 +10 @@
            +        var ancestor = symbol.BaseType;
            +++ b/Core/Vixen.Ui.Tests/UiPropertyTests.cs
            @@ -10 +10 @@
            +    [UnconditionalSuppressMessage("Trimming", "IL2070")]
            +++ b/Core/Vixen.Ui/UiProperty.cs
            @@ -10,3 +10,3 @@
                     [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
            -    int unrelated;
            +    int renamed;
            +    ///         <c>RunClassConstructor</c> on a <c>.BaseType</c>, in prose.
            """;

        Assert.Empty(TrimAnnotationDiff.Findings(diff));
    }

    /// <summary>The probe is <c>CheckAot</c>'s own subject, so any line of it counts.</summary>
    [Fact]
    public void Any_line_of_the_probe_is_found() {
        const string diff = """
            +++ b/Tools/Vixen.AotProbe/Program.cs
            @@ -1 +1 @@
            +Console.WriteLine("x");
            """;

        Assert.Equal(["Tools/Vixen.AotProbe/Program.cs: the probe"], TrimAnnotationDiff.Findings(diff));
    }

    /// <summary>An untracked file is all added lines, and a new annotation is likeliest in one.</summary>
    [Fact]
    public void A_new_file_is_read_whole() {
        Assert.Equal(
            ["Core/Vixen.Ecs/New.cs: RequiresDynamicCode"],
            TrimAnnotationDiff.FindingsInNewFile("Core/Vixen.Ecs/New.cs", "class A {\n    [RequiresDynamicCode(\"x\")]\n    void M() { }\n}\n")
        );
        Assert.Empty(TrimAnnotationDiff.FindingsInNewFile("Core/Vixen.Ecs/New.cs", "class A { }\n"));
    }
}
