// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.DocGen.Tests;

/// <summary>
///     URLs are derived from documentation ids and never stored — docs/plan/25 § 2.2 — so this is
///     where the two collisions that derivation can produce are pinned down.
/// </summary>
public class SlugTests {
    [Theory]
    [InlineData("T:Vixen.Ecs.World", "vixen.ecs/world")]
    [InlineData("T:Vixen.Core.Mathematics.Matrix3x3", "vixen.core.mathematics/matrix3x3")]
    [InlineData("T:Global", "global")]
    public void ATypeIdBecomesANamespacePathAndAName(string id, string expected) =>
        Assert.Equal(expected, Slugs.ForType(id));

    /// <summary>
    ///     Arity is part of the identity. Without this `List`1` and `List`2` would serve one page and
    ///     one of the two would not exist.
    /// </summary>
    [Fact]
    public void GenericArityIsPartOfThePath() {
        Assert.Equal("vixen.core.collections/pool-1", Slugs.ForType("T:Vixen.Core.Collections.Pool`1"));
        Assert.Equal("vixen.core.collections/pool-2", Slugs.ForType("T:Vixen.Core.Collections.Pool`2"));
        Assert.NotEqual(
            Slugs.ForType("T:Vixen.Core.Collections.Pool`1"),
            Slugs.ForType("T:Vixen.Core.Collections.Pool`2"));
    }

    [Fact]
    public void ANestedTypeKeepsItsContainer() =>
        Assert.Equal("vixen.ecs/query.builder", Slugs.ForType("T:Vixen.Ecs.Query.Builder", "Vixen.Ecs"));

    /// <summary>
    ///     Lowercasing is not cosmetic: the site is served off a case-sensitive filesystem and a
    ///     Windows checkout is not, so a path that differs only in case is a page that works on one
    ///     machine. Which makes collisions possible — the emitter asserts on them rather than
    ///     silently dropping one, and this is the pair that proves it can happen.
    /// </summary>
    [Fact]
    public void CaseIsRemovedWhichIsWhyTheEmitterChecksForCollisions() =>
        Assert.Equal(Slugs.ForType("T:A.IPin"), Slugs.ForType("T:A.IPIN"));

    [Theory]
    [InlineData("Vixen.Ecs.Systems", "vixen.ecs.systems")]
    [InlineData("", "global")]
    public void ANamespaceBecomesOneSegment(string name, string expected) =>
        Assert.Equal(expected, Slugs.ForNamespace(name));
}
