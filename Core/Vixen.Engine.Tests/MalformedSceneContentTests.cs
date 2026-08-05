// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Scenes;
using Vixen.Engine.Worlds;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>A column and its block disagreeing about how many entities there are.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The one thing <c>Validate</c> cannot check, and therefore the one that was not
///         checked.</b> It relates the tables to each other — parents to count, block members to
///         count — and every one of those is a comparison against a number already in a register. A
///         column's <i>bytes</i> are not: only a binder knows how wide one value is, and it only
///         knows by reading it. So the check has to be after the read, and being after the read is
///         what made it easy to leave out.
///     </para>
///     <para>
///         The two directions fail differently and both are here. A <b>long</b> column was ignored in
///         silence: the first n values were read and the rest dropped, loading a scene that is wrong
///         rather than one that refuses. A <b>short</b> one threw from the middle of the walk, after
///         <c>CreateMany</c> had already run — so the caller saw an exception and a world with half a
///         level in it.
///     </para>
/// </remarks>
public sealed class MalformedSceneContentTests {
    public MalformedSceneContentTests() => SceneComponentRegistry.Register<Health>();

    /// <summary>Two entities that both carry a Health, with a column of the caller's choosing.</summary>
    static SceneContent Scene(byte[] column) =>
        new() {
            Count = 2,
            Parents = [-1, -1],
            Positions = new Vector3[2],
            Rotations = new Quaternion[2],
            Scales = new Vector3[2],
            Blocks = [
                new() { Entities = [0, 1], Columns = [new() { Component = "SceneTestHealth", Data = column }] }
            ]
        };

    /// <summary>Two entities' worth of Health bytes, taken from the writer rather than counted here.</summary>
    /// <remarks>
    ///     A literal length would be a number a change to the component silently invalidates, and the
    ///     test would then be asserting about a column that is the wrong size for a different reason.
    /// </remarks>
    static byte[] TwoValues() {
        using var world = new World();

        var first = world.Create();
        var second = world.Create();

        world.Add(first, new Health { Value = 70, Regeneration = 0.5f });
        world.Add(second, new Health { Value = 12, Regeneration = 0.25f });

        return HealthColumn(SceneContent.Capture(world, [first, second]).Blocks);
    }

    /// <summary>The Health column out of a set of blocks, which the caller asserts exists.</summary>
    static byte[] HealthColumn(SceneBlock[] blocks) {
        foreach (var block in blocks) {
            foreach (var column in block.Columns) {
                if (string.Equals(column.Component, "SceneTestHealth", StringComparison.Ordinal)) {
                    return column.Data;
                }
            }
        }

        throw new InvalidOperationException("The capture wrote no Health column.");
    }

    [Fact]
    public void AColumnOfTheRightLengthStillLoads() {
        using var world = new World();

        Scene(TwoValues()).Instantiate(world, []);

        Assert.Equal(2, world.EntityCount);
    }

    /// <summary>Bytes left over after the block was read is a refusal, not something to drop.</summary>
    [Fact]
    public void AColumnLongerThanItsBlockIsRefusedRatherThanIgnored() {
        using var world = new World();

        var failure = Assert.Throws<ArgumentException>(() => Scene([.. TwoValues(), 9, 9, 9, 9]).Instantiate(world, []));

        Assert.Contains("left after", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A column that runs out is this format's refusal rather than the reader's.</summary>
    [Fact]
    public void AColumnShorterThanItsBlockIsAnArgumentExceptionNamingTheComponent() {
        using var world = new World();

        var failure = Assert.Throws<ArgumentException>(() => Scene([]).Instantiate(world, []));

        Assert.Contains("SceneTestHealth", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>And the world it failed to load into is left as it was found.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure that made this worth fixing.</b> Without the undo, a scene that refuses
    ///     halfway leaves every entity it had already created — a level that is half there, with
    ///     components on some of it, behind a caller who saw an exception and reasonably concluded
    ///     nothing happened.
    /// </remarks>
    /// <param name="delta">How many bytes the column is off by, either way.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void ASceneThatRefusesLeavesNoEntitiesBehind(int delta) {
        var right = TwoValues();
        var column = delta < 0 ? right[..(right.Length + delta)] : [.. right, .. new byte[delta]];

        using var world = new World();

        var already = world.Create();

        Assert.Throws<ArgumentException>(() => Scene(column).Instantiate(world, []));

        // Only what was there before this was asked for.
        Assert.Equal(1, world.EntityCount);
        Assert.True(world.IsAlive(already));
    }

    /// <summary>A captured world has the same gap, and its own remarks say the data must be checked.</summary>
    [Fact]
    public void ACapturedWorldsColumnIsCheckedTheSameWay() {
        using var source = new World();

        var first = source.Create();
        var second = source.Create();

        source.Add(first, new Health { Value = 1, Regeneration = 1 });
        source.Add(second, new Health { Value = 2, Regeneration = 2 });

        var content = WorldSerializer.Capture(source);
        var lengthened = 0;

        foreach (var block in content.Blocks) {
            foreach (var column in block.Columns) {
                if (string.Equals(column.Component, "SceneTestHealth", StringComparison.Ordinal)) {
                    column.Data = [.. column.Data, 7, 7, 7, 7];
                    lengthened++;
                }
            }
        }

        Assert.Equal(1, lengthened);

        using var target = new World();

        Assert.Throws<ArgumentException>(() => WorldSerializer.Restore(content, target));

        // Emptied rather than left half-restored: Restore clears before it builds, so "it did not
        // happen" cannot mean "as it was" — but it can mean empty, which a caller can tell apart.
        Assert.Equal(0, target.EntityCount);
    }
}
