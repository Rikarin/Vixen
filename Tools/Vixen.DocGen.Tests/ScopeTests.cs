// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>What the graph documents, and how it picks when two assemblies claim one URL.</summary>
public class ScopeTests {
    static DocNode Node(string assembly, bool packable, string slug = "vixen.core.syntax/diagnostic") => new() {
        Id = "T:Vixen.Core.Syntax.Diagnostic",
        Kind = DocKind.Class,
        Name = "Diagnostic",
        QualifiedName = "Vixen.Core.Syntax.Diagnostic",
        Namespace = "Vixen.Core.Syntax",
        Assembly = assembly,
        Area = "Core",
        Slug = slug,
        Signature = "public sealed class Diagnostic",
        IsPackable = packable
    };

    [Theory]
    [InlineData("Core", "Vixen.Ecs", true)]
    [InlineData("Platform", "Vixen.Graphics.Vulkan", true)]
    [InlineData("Editor", "Vixen.Editor.NodeGraph", true)]
    [InlineData("Tools", "Vixen.Cli", true)]
    [InlineData("Raven", "Vixen.Raven", true)]
    [InlineData("Core", "Vixen.Ecs.Tests", false)]
    [InlineData("Samples", "HelloTriangle", false)]
    [InlineData("Benchmarks", "Vixen.Benchmarks.Ecs", false)]
    public void SamplesBenchmarksAndTestsAreExamplesRatherThanSurface(
        string area,
        string project,
        bool expected
    ) => Assert.Equal(expected, Scope.IsDocumented(area, project));

    /// <summary>
    ///     ⚠ A documentation id is unique inside an assembly, not across a solution: this repository
    ///     links `Vixen.Core.Syntax` into `Vixen.Ui.Markup.Generators` as source, so the same
    ///     qualified name is two symbols claiming one page.
    /// </summary>
    [Fact]
    public void ThePackableCopyKeepsTheUrl() {
        var (nodes, merged) = Scope.Deduplicate([
            Node("Vixen.Ui.Markup.Generators", packable: false),
            Node("Vixen.Core.Syntax", packable: true)
        ]);

        var node = Assert.Single(nodes);

        Assert.Equal("Vixen.Core.Syntax", node.Assembly);
        Assert.Equal(["Vixen.Ui.Markup.Generators"], node.AlsoIn);
        Assert.Equal(1, merged);
    }

    /// <summary>
    ///     With nothing packable to prefer, the assembly name decides — so the URL a type gets does
    ///     not depend on the order the projects happened to load in.
    /// </summary>
    [Fact]
    public void TiesBreakOnTheAssemblyNameSoTheOutputIsDeterministic() {
        var (first, _) = Scope.Deduplicate([Node("B.Generators", false), Node("A.Generators", false)]);
        var (second, _) = Scope.Deduplicate([Node("A.Generators", false), Node("B.Generators", false)]);

        Assert.Equal("A.Generators", Assert.Single(first).Assembly);
        Assert.Equal("A.Generators", Assert.Single(second).Assembly);
    }

    [Fact]
    public void DistinctSlugsAreLeftAlone() {
        var (nodes, merged) = Scope.Deduplicate([
            Node("Vixen.Core.Syntax", true),
            Node("Vixen.Ecs", true, "vixen.ecs/world")
        ]);

        Assert.Equal(2, nodes.Count);
        Assert.Equal(0, merged);
        Assert.All(nodes, node => Assert.Empty(node.AlsoIn));
    }
}
