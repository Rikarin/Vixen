// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Testing;
using Xunit;

namespace Vixen.Ai.Perception.Tests;

public class StimuliGridTests {
    [Fact]
    public void AQueryFindsWhatIsInsideTheSphereAndNothingOnTheCorners() {
        var grid = new StimuliGrid();
        var points = new[] {
            new Vector3(0f, 0f, 0f),
            new Vector3(9f, 0f, 0f),
            new Vector3(0f, 0f, 9f),

            // Inside the box the cells make, outside the sphere: 12.7 metres away against a radius
            // of 10. A query that returned its cells rather than testing would hand this back.
            new Vector3(9f, 0f, 9f)
        };

        grid.Build(points, 8f);

        var found = new List<int>();

        grid.Query(Vector3.Zero, 10f, found, out var cells);

        Assert.Equal([0, 1, 2], found.Order().ToArray());
        Assert.True(cells > 1, "the query did not span more than one cell.");
    }

    [Fact]
    public void AQueryAgreesWithAScanOverAThousandRandomPoints() {
        var points = Scatter(1_000, 400f);
        var grid = new StimuliGrid();

        grid.Build(points, 20f);

        var found = new List<int>();

        foreach (var centre in Scatter(50, 400f)) {
            grid.Query(centre, 25f, found, out _);

            var scanned = points
                .Select((point, index) => (point, index))
                .Where(pair => (pair.point - centre).Length() <= 25f)
                .Select(pair => pair.index)
                .Order()
                .ToArray();

            Assert.Equal(scanned, found.Order().ToArray());
        }
    }

    /// <summary>
    ///     ⚠ The point of the whole structure: what it examines is a property of the query radius and
    ///     the local density, not of how many sources exist.
    /// </summary>
    [Fact]
    public void WhatAQueryExaminesDoesNotGrowWithThePopulation() {
        var small = Examined(500);
        var large = Examined(4_000);

        Assert.True(
            large < small * 3,
            $"a query examined {small} of 500 sources and {large} of 4 000 — an eightfold population."
        );

        static int Examined(int count) {
            var grid = new StimuliGrid();

            // The same density in both: eight times the sources over eight times the area.
            grid.Build(Scatter(count, MathF.Sqrt(count) * 20f), 25f);

            var found = new List<int>();

            return grid.Query(Vector3.Zero, 25f, found, out _);
        }
    }

    [Fact]
    public void ARebuiltGridAllocatesNothingOnceItsArraysHaveStopped() {
        var grid = new StimuliGrid();
        var points = Scatter(256, 200f);
        var found = new List<int>();

        Measured.NothingAllocated(
            () => {
                grid.Build(points, 20f);
                grid.Query(Vector3.Zero, 25f, found, out _);
            },
            warmUp: 20,
            passes: 50
        );
    }

    [Fact]
    public void AnEmptyGridAnswersNothingRatherThanThrowing() {
        var grid = new StimuliGrid();
        var found = new List<int> { 7 };

        Assert.Equal(0, grid.Query(Vector3.Zero, 10f, found, out _));
        Assert.Empty(found);

        grid.Build([Vector3.Zero], 8f);
        grid.Clear();

        Assert.Equal(0, grid.Query(Vector3.Zero, 10f, found, out _));
    }

    static Vector3[] Scatter(int count, float extent) {
        // A fixed integer hash rather than Random, so a failure is the same failure on every machine.
        var points = new Vector3[count];

        for (var index = 0; index < count; index++) {
            points[index] = new(
                Coordinate(index, 1) * extent,
                0f,
                Coordinate(index, 2) * extent
            );
        }

        return points;

        static float Coordinate(int index, uint salt) {
            var value = (uint)index * 2_654_435_761u ^ (salt * 0x9E3779B9u);

            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;

            return ((value >> 8) * (1f / 16777216f)) - 0.5f;
        }
    }
}
