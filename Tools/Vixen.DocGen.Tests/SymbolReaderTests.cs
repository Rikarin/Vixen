// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.CodeAnalysis;
using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>What a node carries, and — as much — what it leaves out.</summary>
public class SymbolReaderTests {
    static IReadOnlyList<DocNode> Read(string source) {
        var compilation = Fixture.Compile(source);
        var links = new SourceLinks(Path.GetTempPath(), "https://github.com/rikarin/Vixen", "abc123");

        return [.. new SymbolReader(links).Read(compilation.Assembly, "Core", isPackable: true)
            .Where(node => node.Namespace.StartsWith("Fixtures", StringComparison.Ordinal))];
    }

    [Fact]
    public void APublicTypeBecomesANodeKeyedByItsDocumentationId() {
        var node = Assert.Single(Read(
            """
            namespace Fixtures {
                /// <summary>A world.</summary>
                public sealed class World;
            }
            """));

        Assert.Equal("T:Fixtures.World", node.Id);
        Assert.Equal("World", node.Name);
        Assert.Equal("Fixtures", node.Namespace);
        Assert.Equal("Core", node.Area);
        Assert.Equal("fixtures/world", node.Slug);
        Assert.Equal("A world.", node.Summary);
        Assert.True(node.IsPackable);
    }

    /// <summary>
    ///     `internal` is not surface, and neither is a public type nested inside one — whatever its
    ///     own modifier says.
    /// </summary>
    [Fact]
    public void InternalTypesAndTypesReachableOnlyThroughThemAreNotSurface() {
        var nodes = Read(
            """
            namespace Fixtures {
                internal sealed class Hidden {
                    public sealed class AlsoHidden;
                }

                public sealed class Visible;
            }
            """);

        Assert.Equal(["T:Fixtures.Visible"], nodes.Select(node => node.Id));
    }

    /// <summary>A nested type gets its own page: a reader searching for it should not have to know
    ///     which type it hangs off.</summary>
    [Fact]
    public void PublicNestedTypesGetTheirOwnNode() {
        var nodes = Read(
            """
            namespace Fixtures {
                public sealed class Query {
                    public sealed class Builder;
                }
            }
            """);

        Assert.Contains("T:Fixtures.Query.Builder", nodes.Select(node => node.Id));
        Assert.Contains("fixtures/query.builder", nodes.Select(node => node.Slug));
    }

    [Fact]
    public void MembersAreReadWithTheirKindAndSignature() {
        var node = Assert.Single(Read(
            """
            namespace Fixtures {
                public sealed class Bag {
                    /// <summary>How many.</summary>
                    public int Count { get; }
                    public const int Max = 8;
                    public event System.Action? Changed;
                    public void Add(int value) { }
                }
            }
            """));

        var members = node.Members.ToDictionary(member => member.Name, StringComparer.Ordinal);

        Assert.Equal("property", members["Count"].MemberKind);
        Assert.Equal("How many.", members["Count"].Summary);
        Assert.Equal("constant", members["Max"].MemberKind);
        Assert.Equal("event", members["Changed"].MemberKind);
        Assert.Equal("method", members["Add"].MemberKind);
        Assert.Contains("Add(int value)", Signatures.Text(members["Add"].Signature), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A property's accessors are part of its own signature. Listing them again as methods is
    ///     noise, and it is the same call the ApiCheck baselines make.
    /// </summary>
    [Fact]
    public void PropertyAccessorsAreNotAlsoMethods() {
        var node = Assert.Single(Read(
            """
            namespace Fixtures {
                public sealed class Bag {
                    public int Count { get; set; }
                }
            }
            """));

        Assert.Equal(["Count"], node.Members.Select(member => member.Name));
    }

    [Fact]
    public void AttributeArgumentsAreKeptAsTheyWereWritten() {
        var node = Assert.Single(Read(
            """
            namespace Fixtures {
                [Vixen.Editor.Assets.Importer(".fbx", ".obj")]
                public sealed class ModelImporter;
            }
            """));

        var importer = Assert.Single(node.Attributes, attribute => attribute.Name == "Importer");

        Assert.Equal(["[\".fbx\", \".obj\"]"], importer.Arguments);
        Assert.Equal(DocKind.Importer, node.Kind);
    }

    /// <summary>
    ///     ⚠ A null array argument leaves `TypedConstant.Values` uninitialised rather than empty, and
    ///     reading it throws. The engine has such an attribute and the whole run died on it.
    /// </summary>
    [Fact]
    public void ANullArrayArgumentIsFormattedRatherThanThrown() {
        var node = Assert.Single(Read(
            """
            namespace Fixtures {
                [Vixen.Editor.Assets.Importer(null)]
                public sealed class NullImporter;
            }
            """));

        var importer = Assert.Single(node.Attributes, attribute => attribute.Name == "Importer");

        Assert.Equal(["null"], importer.Arguments);
    }

    [Fact]
    public void ObsoleteIsRecordedBecauseTheReleaseDiffReadsIt() {
        var node = Assert.Single(Read(
            """
            namespace Fixtures {
                [System.Obsolete("Use World instead.")]
                public sealed class Universe;
            }
            """));

        Assert.Equal("Use World instead.", node.Obsolete);
    }

    [Fact]
    public void BaseTypesAndInterfacesAreRecordedAsIds() {
        var node = Read(
            """
            namespace Fixtures {
                public interface IThing { }
                public abstract class Base;
                public sealed class Thing : Base, IThing;
            }
            """).Single(candidate => candidate.Name == "Thing");

        Assert.Equal("T:Fixtures.Base", node.BaseType);
        Assert.Equal(["T:Fixtures.IThing"], node.Interfaces);
    }

    [Fact]
    public void ObjectIsNotRecordedAsABaseTypeBecauseEverythingHasIt() {
        var node = Assert.Single(Read(
            """
            namespace Fixtures {
                public sealed class Plain;
            }
            """));

        Assert.Null(node.BaseType);
    }

    [Fact]
    public void ASourceLinkCarriesThePathTheLinesAndTheCommit() {
        var compilation = Fixture.Compile(
            """
            namespace Fixtures {
                public sealed class Linked;
            }
            """);

        var root = Path.GetTempPath();
        var links = new SourceLinks(root, "https://github.com/rikarin/Vixen", "abc123");
        var type = compilation.Type("Fixtures.Linked");
        var source = links.For(type);

        Assert.NotNull(source);
        Assert.True(source.StartLine > 0);
        Assert.True(source.EndLine >= source.StartLine);
    }

    [Fact]
    public void WithoutACommitThereIsAPathButNoUrl() {
        var compilation = Fixture.Compile(
            """
            namespace Fixtures {
                public sealed class Linked;
            }
            """);

        var links = new SourceLinks(Path.GetTempPath(), "https://github.com/rikarin/Vixen", commit: null);

        Assert.Null(links.For(compilation.Type("Fixtures.Linked"))?.Url);
    }

    [Theory]
    [InlineData("/repo/obj/Debug/net10.0/Generated.g.cs", true)]
    [InlineData("/repo/Core/Vixen.Core/World.cs", false)]
    [InlineData("/elsewhere/World.cs", true)]
    public void GeneratedCodeIsRecognisedByWhereItIsNot(string path, bool expected) {
        // ⚠ The root gets the same treatment as the path. Converting one and not the other put every
        // case outside the tree on Windows, where "outside the tree" is itself an answer — `false`
        // came back `true` and looked like a bug in the predicate.
        var links = new SourceLinks(
            "/repo".Replace('/', Path.DirectorySeparatorChar),
            "https://github.com/rikarin/Vixen",
            "abc123"
        );

        Assert.Equal(expected, links.IsGenerated(path.Replace('/', Path.DirectorySeparatorChar)));
    }
}
