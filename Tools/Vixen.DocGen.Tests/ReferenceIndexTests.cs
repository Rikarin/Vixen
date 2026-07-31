// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>docs/plan/25 § 2.4 — the `used-by` edge, and what it deliberately does not count.</summary>
public class ReferenceIndexTests {
    static async Task<ReferenceIndex> Build(params (string Area, string Name, string Source)[] projects) {
        var loaded = projects
            .Select(project => new LoadedProject(
                project.Name,
                Fixture.Compile(project.Source),
                project.Area,
                IsPackable: true,
                GeneratedDocuments: 0,
                Errors: []))
            .ToList();

        var known = loaded
            .SelectMany(project => project.Compilation.Assembly.GlobalNamespace.GetMembers())
            .OfType<INamedTypeSymbol>()
            .Select(type => type.GetDocumentationCommentId() ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        return await ReferenceIndex.BuildAsync(loaded, known, CancellationToken.None);
    }

    [Fact]
    public async Task ATypeThatMentionsAnotherIsRecordedAgainstIt() {
        var index = await Build(("Core", "Vixen.Engine",
            """
            public sealed class World;

            public sealed class MovementSystem {
                World? _world;
            }
            """));

        var (shown, total) = index.For("T:World");

        Assert.Equal(1, total);
        Assert.Equal("MovementSystem", Assert.Single(shown).Name);
    }

    /// <summary>
    ///     A reference to a member is a reference to its type: somebody reading `World` wants to know
    ///     that `MovementSystem` calls `World.Query`, not to find `Query` listed on its own.
    /// </summary>
    [Fact]
    public async Task CallingAMemberCountsAsUsingItsType() {
        var index = await Build(("Core", "Vixen.Engine",
            """
            public sealed class World {
                public void Query() { }
            }

            public sealed class MovementSystem {
                public void Run(World world) => world.Query();
            }
            """));

        Assert.Equal(1, index.For("T:World").Total);
    }

    [Fact]
    public async Task ATypeUsingItselfIsNotAUseOfIt() {
        var index = await Build(("Core", "Vixen.Engine",
            """
            public sealed class World {
                World? _next;
                public World Self() => this;
            }
            """));

        Assert.Equal(0, index.For("T:World").Total);
    }

    /// <summary>
    ///     ⚠ The reason the pass runs over projects the graph does not document: a use in a sample is
    ///     a worked example, and it is the reference a reader wants first.
    /// </summary>
    [Fact]
    public async Task SamplesComeFirstBecauseTheyAreWhatAReaderWants() {
        var index = await Build(
            ("Core", "Vixen.Engine",
                """
                public sealed class World;

                public sealed class InternalHelper {
                    World? _world;
                }
                """),
            ("Samples", "PbrShowcase",
                """
                public sealed class World;

                public sealed class ShowcaseProgram {
                    World? _world;
                }
                """));

        var (shown, total) = index.For("T:World");

        Assert.Equal(2, total);
        Assert.Equal("ShowcaseProgram", shown[0].Name);
        Assert.Equal("Samples", shown[0].Area);
    }

    [Fact]
    public async Task AReferenceToSomethingOutsideTheGraphIsNotAnEdge() {
        var index = await Build(("Core", "Vixen.Engine",
            """
            public sealed class MovementSystem {
                System.Text.StringBuilder? _builder;
            }
            """));

        Assert.Equal(0, index.For("T:System.Text.StringBuilder").Total);
    }

    [Fact]
    public async Task TheSameTypeReferencedTwiceIsOneEdge() {
        var index = await Build(("Core", "Vixen.Engine",
            """
            public sealed class World;

            public sealed class MovementSystem {
                World? _a;
                World? _b;
                public World? Get() => _a;
            }
            """));

        Assert.Equal(1, index.For("T:World").Total);
    }

    [Fact]
    public async Task NothingUsesAnUnusedType() {
        var index = await Build(("Core", "Vixen.Engine", "public sealed class Lonely;"));

        var (shown, total) = index.For("T:Lonely");

        Assert.Empty(shown);
        Assert.Equal(0, total);
    }
}
