// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>What kind of island a recipe asks for, which is the axis the shapes differ along.</summary>
/// <remarks>
///     ⚠ <b>Not a list of shapes somebody drew — a list of the things about a shape that a packer can
///     get wrong.</b> A rectangle is the case the skyline path is exact for; a star is the case the
///     mask path exists for; a degenerate is the case with no area at all, which is where a divide by
///     the island's own size lives.
/// </remarks>
enum IslandShape : byte {
    /// <summary>An axis-aligned box. The rectangle rung's exact case.</summary>
    Rectangle,

    /// <summary>A convex fan. Round, so a quarter turn changes its bounding box very little.</summary>
    Convex,

    /// <summary>A star with every third corner cut inward. Non-convex, which is what masks are for.</summary>
    Star,

    /// <summary>A long thin sliver, whose bounding box is almost all empty.</summary>
    Sliver,

    /// <summary>Three coincident coordinates: an island with no area, no size and no bounding box.</summary>
    Degenerate
}

/// <summary>One island, described by the handful of numbers a shrinker can make smaller.</summary>
/// <param name="Shape">Which family.</param>
/// <param name="Corners">How many corners round it. Ignored by <see cref="IslandShape.Degenerate" />.</param>
/// <param name="Size">Its radius, in island coordinates.</param>
/// <param name="Aspect">How much wider than tall, as a multiplier on the x coordinate.</param>
/// <param name="Scale">Its coordinates per world unit, which is what texel density is computed from.</param>
/// <remarks>
///     ⚠ <b>A description rather than the island itself, and that is the whole reason this is a
///     record of five scalars.</b> CsCheck shrinks the values it generated, not the object they were
///     turned into: a failing four-thousand-triangle island set is not a finding anybody can act on,
///     and a failing <c>(Star, 8, 0.10, 1.0, 1.0)</c> is one line to paste into a test.
/// </remarks>
readonly record struct IslandRecipe(IslandShape Shape, int Corners, float Size, float Aspect, float Scale) {
    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Round-trippable, and that is the whole point of printing it at all.</b> A shrunk case
    ///     is only a finding if it can be pasted into a test, and <c>0.####</c> prints
    ///     <c>0.037500001</c> as <c>0.0375</c> — which packs differently, so the paste reproduces
    ///     nothing and the next person concludes the property is flaky. <c>R</c> is the shortest string
    ///     that parses back to the same float.
    /// </remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"new({nameof(IslandShape)}.{Shape}, {Corners}, {Size:R}f, {Aspect:R}f, {Scale:R}f)"
        );
}

/// <summary>The space <see cref="IslandCorpus" />'s twenty-four fixed islands are one point in.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/42 § D7's determinism criterion is a statement about every set of islands, and
///         a fixed corpus can only ever be a sample of one.</b> The real defect that motivated
///         <c>IslandMask.Signature</c> — two islands of equal texel area and different shapes swapping
///         under a shuffle, and every later placement moving with them — was found by one hand-written
///         shuffle of one hand-written corpus. A generator over the space finds the next one without
///         anybody having to guess which twenty-four islands to draw.
///     </para>
///     <para>
///         ⚠ <b><see cref="IslandCorpus" /> is not replaced and must not be.</b> § B6 is explicit that
///         a test measuring <i>efficiency</i> against a moving corpus cannot tell a regression from a
///         reseed, so the efficiency and golden tests keep their fixed sequence. What moves here is
///         only the set of properties that are true of every input — order independence and the texel
///         margin — where a fixed corpus buys nothing that a generator does not buy more of.
///     </para>
/// </remarks>
static class IslandSpace {
    /// <summary>Islands, in the distribution a real atlas has: a few large and a long tail of small.</summary>
    /// <remarks>
    ///     ⚠ The fourth power on the radius is <see cref="IslandCorpus" />'s, kept for the same reason
    ///     it is there: a flat draw gives fifty islands of much the same size, which is the one
    ///     distribution where a packer's ordering never matters and therefore the one where nothing is
    ///     being tested.
    /// </remarks>
    public static readonly Gen<IslandRecipe> Recipe = Gen.Select(
        Gen.FrequencyConst(
            (6, IslandShape.Star),
            (4, IslandShape.Rectangle),
            (4, IslandShape.Convex),
            (2, IslandShape.Sliver),
            (1, IslandShape.Degenerate)
        ),
        Gen.Int[3, 20],
        Gen.Single[0f, 1f],
        Gen.Single[0.15f, 4f],
        Gen.Single[0.25f, 4f],
        (shape, corners, spread, aspect, scale) => new IslandRecipe(
            shape,
            corners,
            0.02f + (0.34f * spread * spread * spread * spread),
            aspect,
            scale
        )
    );

    /// <summary>A whole set of them, of a size a packer has something to do with.</summary>
    /// <remarks>
    ///     ⚠ Two at the bottom rather than one: every property here is about two islands not
    ///     colliding, and a one-island atlas satisfies all of them for the wrong reason.
    /// </remarks>
    public static readonly Gen<IslandRecipe[]> Set = Recipe.Array[2, 48];

    /// <summary>Turns a recipe into the island it describes.</summary>
    /// <param name="recipe">The recipe.</param>
    /// <returns>The island, as a triangle fan about its own centre.</returns>
    /// <remarks>
    ///     ⚠ <b>The corners are a deterministic function of the recipe and nothing here draws from a
    ///     random stream.</b> A generator that reached for its own randomness would shrink a recipe
    ///     into a <i>different</i> island, and the minimal case CsCheck reported would not reproduce.
    /// </remarks>
    public static UvIsland Build(IslandRecipe recipe) {
        if (recipe.Shape == IslandShape.Degenerate) {
            return new([Vector2.Zero, Vector2.Zero, Vector2.Zero], [0, 1, 2], Vector2.Zero, Vector2.Zero, recipe.Scale);
        }

        var corners = recipe.Shape == IslandShape.Rectangle ? 4 : Math.Max(3, recipe.Corners);
        var points = new Vector2[corners];

        for (var corner = 0; corner < corners; corner++) {
            var angle = MathF.Tau * corner / corners;

            var reach = recipe.Shape switch {
                // Every third corner cut inward is what makes it non-convex, and a non-convex island
                // is the entire reason the mask path exists.
                IslandShape.Star => corner % 3 == 2 ? recipe.Size * 0.3f : recipe.Size,
                IslandShape.Sliver => recipe.Size,
                _ => recipe.Size
            };

            var thin = recipe.Shape == IslandShape.Sliver ? 0.02f : 1f;

            points[corner] = new(reach * recipe.Aspect * MathF.Cos(angle), reach * thin * MathF.Sin(angle));
        }

        var coordinates = new List<Vector2>(corners * 3);
        var indices = new List<int>(corners * 3);
        var minimum = Vector2.Zero;
        var maximum = Vector2.Zero;

        for (var corner = 0; corner < corners; corner++) {
            var a = points[corner];
            var b = points[(corner + 1) % corners];

            coordinates.Add(Vector2.Zero);
            coordinates.Add(a);
            coordinates.Add(b);

            indices.Add(corner * 3);
            indices.Add((corner * 3) + 1);
            indices.Add((corner * 3) + 2);

            minimum = new(MathF.Min(minimum.X, MathF.Min(a.X, b.X)), MathF.Min(minimum.Y, MathF.Min(a.Y, b.Y)));
            maximum = new(MathF.Max(maximum.X, MathF.Max(a.X, b.X)), MathF.Max(maximum.Y, MathF.Max(a.Y, b.Y)));
        }

        return new(coordinates, indices, minimum, maximum, recipe.Scale);
    }

    /// <summary>Turns a whole set into islands.</summary>
    /// <param name="recipes">The recipes.</param>
    /// <returns>One island each, in the same order.</returns>
    public static UvIsland[] Build(IReadOnlyList<IslandRecipe> recipes) {
        ArgumentNullException.ThrowIfNull(recipes);

        var islands = new UvIsland[recipes.Count];

        for (var index = 0; index < islands.Length; index++) {
            islands[index] = Build(recipes[index]);
        }

        return islands;
    }

    /// <summary>One line naming every island, for the failure message a shrunk case ends in.</summary>
    /// <param name="recipes">The recipes.</param>
    /// <returns>Something that can be pasted into a test as an array initialiser.</returns>
    public static string Print(IReadOnlyList<IslandRecipe> recipes) {
        ArgumentNullException.ThrowIfNull(recipes);

        return $"[{string.Join(", ", recipes)}]";
    }
}
