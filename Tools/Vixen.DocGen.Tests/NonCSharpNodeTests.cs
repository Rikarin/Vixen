// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     docs/plan/25 § 2.8 — the parts of the engine that are not C# symbols, and that a symbol-only
///     tool leaves undocumented.
/// </summary>
public class NonCSharpNodeTests : IDisposable {
    readonly string _root = Path.Combine(Path.GetTempPath(), "vixen-noncs-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_root)) {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    void Write(string relativePath, string contents) {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    IReadOnlyList<DocNode> Read() =>
        NonCSharpNodes.Read(_root, new SourceLinks(_root, "https://github.com/rikarin/Vixen", "abc123"));

    // ── Shaders ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AShaderIsDescribedByItsReflectionRatherThanItsSource() {
        Write("Raven/Library/Pipeline/ForwardPlus.reflect.json",
            """
            {
              "Sets": [{ "Set": 0 }, { "Set": 1 }],
              "VertexInputs": [{ "Name": "position" }, { "Name": "normal" }],
              "PushConstants": [{ "Name": "Push", "Stages": "Vertex, Fragment" }],
              "Permutations": [{ "Name": "UseSkinning" }],
              "Parameters": [{ "Name": "transformBase" }, { "Name": "albedo" }]
            }
            """);

        Write("Raven/Library/Pipeline/ForwardPlus.rvn",
            """
            // SPDX-FileCopyrightText: Copyright (c) Rikarin
            // The forward+ opaque pass.

            shader ForwardPlus { }
            """);

        var node = Assert.Single(Read(), candidate => candidate.Kind == DocKind.Shader);

        Assert.Equal("R:Pipeline/ForwardPlus", node.Id);
        Assert.Equal("shaders/pipeline.forwardplus", node.Slug);
        Assert.Equal("The forward+ opaque pass.", node.Summary);
        Assert.Equal(["Vertex", "Fragment"], node.Facets?.Stages!);
        Assert.Equal(["UseSkinning"], node.Facets?.Permutations!);
        Assert.Equal(2, node.Facets?.DescriptorSets);
        Assert.Equal(2, node.Facets?.ShaderParameters);
        Assert.Equal(["position", "normal"], node.Facets?.VertexInputs!);
    }

    /// <summary>
    ///     ⚠ A `.rvn` with no reflection beside it is not documented: the alternative is a page that
    ///     guesses at bindings, and a wrong descriptor set is worse than an absent one.
    /// </summary>
    [Fact]
    public void AShaderWithoutReflectionIsNotDocumented() {
        Write("Raven/Library/Pipeline/Uncompiled.rvn", "shader Uncompiled { }");

        Assert.DoesNotContain(Read(), node => node.Kind == DocKind.Shader);
    }

    [Fact]
    public void AReflectionThatDoesNotParseSkipsThatShaderRatherThanTheRest() {
        Write("Raven/Library/Pipeline/Broken.reflect.json", "{ not json");
        Write("Raven/Library/Pipeline/Fine.reflect.json", """{ "Sets": [{ "Set": 0 }] }""");

        var node = Assert.Single(Read(), candidate => candidate.Kind == DocKind.Shader);

        Assert.Equal("Fine", node.Name);
    }

    // ── The registers ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADiagnosticCodeBecomesANodeWithWhatEmitsIt() {
        Write("docs/manual/diagnostic-codes.md",
            """
            # Diagnostic codes

            | Code | Meaning | Emitted by |
            |---|---|---|
            | `VX1001` | An importer said something about an asset. | `vixen import`, `vixen content build` |
            """);

        var node = Assert.Single(Read(), candidate => candidate.Kind == DocKind.Diagnostic);

        Assert.Equal("D:VX1001", node.Id);
        Assert.Equal("diagnostics/vx1001", node.Slug);
        Assert.Equal("An importer said something about an asset.", node.Summary);
        Assert.Equal(["vixen import", "vixen content build"], node.Facets?.EmittedBy!);
    }

    [Fact]
    public void ALogEventCarriesItsLevelAndTheSubsystemItsHeadingNames() {
        Write("docs/manual/log-events.md",
            """
            # Log event ids

            ## Ranges

            | Range | Subsystem | Status |
            |---|---|---|
            | 2 000 – 2 999 | `Vixen.Graphics`, backends | **in use** |

            ## Allocated ids

            ### `Vixen.Graphics` and its backends

            | Id | Level | Message | Since |
            |---|---|---|---|
            | 2001 | Warning | The Vulkan validation layers were asked for and are not installed | 0.1.0 |
            """);

        var node = Assert.Single(Read(), candidate => candidate.Kind == DocKind.LogEvent);

        Assert.Equal("L:2001", node.Id);
        Assert.Equal("Warning", node.Facets?.Level);
        Assert.Equal("0.1.0", node.Facets?.Since);
        Assert.Equal("Vixen.Graphics and its backends", node.Assembly);
    }

    /// <summary>
    ///     The ranges table at the top of the register has rows shaped like an id row. A range is not
    ///     an event, and the level column is what tells them apart.
    /// </summary>
    [Fact]
    public void ARangeIsNotAnEvent() {
        Write("docs/manual/log-events.md",
            """
            | Range | Subsystem | Status |
            |---|---|---|
            | 1 000 – 1 999 | `Vixen.Core.*` | reserved |
            | 2 000 – 2 999 | `Vixen.Graphics` | **in use** |
            """);

        Assert.DoesNotContain(Read(), node => node.Kind == DocKind.LogEvent);
    }

    [Fact]
    public void NoRegistersMeansNoNodesRatherThanAFailure() => Assert.Empty(Read());
}
