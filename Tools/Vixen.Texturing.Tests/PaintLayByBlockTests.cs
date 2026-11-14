// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Texturing.Painting;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>A big stamp of a plain colour is laid a block of texels to a thread, and lays what one thread lays.</summary>
public class PaintLayByBlockTests {
    const int Side = 2048;
    const uint Red = 0xFF2020E0u;

    /// <summary>A sheet two units wide, cut down its middle into two islands, each a quarter of the atlas across.</summary>
    static PaintMeshSurface Sheet() =>
        new(
            PaintProjection.Over(
                [
                    new(-1f, -1f, 0f), new(0f, -1f, 0f), new(0f, 1f, 0f), new(-1f, 1f, 0f),
                    new(0f, -1f, 0f), new(1f, -1f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f)
                ],
                [
                    new(0.1f, 0.1f), new(0.35f, 0.1f), new(0.35f, 0.6f), new(0.1f, 0.6f),
                    new(0.6f, 0.1f), new(0.85f, 0.1f), new(0.85f, 0.6f), new(0.6f, 0.6f)
                ],
                [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]
            ),
            Side,
            Side
        );

    static PaintView Facing() =>
        new(new(0f, 0f, 4f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, -1f), 2f * MathF.Atan(0.5f), 512, 512);

    /// <summary>
    ///     ⚠ A stencil that lets everything through keeps a stroke on one thread and changes no texel, so it is the
    ///     one-thread oracle: the same drag across the seam, by blocks and by one thread, is the same picture and the
    ///     same record. The first stamp must be past the size at which the blocks start, or this would compare one
    ///     thread with itself.
    /// </summary>
    [Fact]
    public void A_big_stamp_laid_by_blocks_is_the_stamp_one_thread_lays() {
        var surface = Sheet();
        var view = Facing();
        var brush = PaintBrush.Default with { Radius = 90f, Flow = 0.6f };
        PaintImage blocks = new(Side, Side);
        PaintImage alone = new(Side, Side);
        var fast = new PaintStroke(blocks, surface.Coverage, brush, Red, placement: surface.Through(view));
        var slow = new PaintStroke(
            alone,
            surface.Coverage,
            brush,
            Red,
            placement: surface.Through(view),
            stencil: new Open()
        );

        for (var step = 0; step < 12; step++) {
            var at = new Vector2(160f + step * 16f, 250f + step * 2f);

            fast.MoveTo(at);
            slow.MoveTo(at);
        }

        Assert.True(fast.WeightsEvaluated / fast.StampCount > 16384, $"{fast.WeightsEvaluated / fast.StampCount}");
        Assert.Equal(slow.StampCount, fast.StampCount);
        Assert.Equal(slow.RecordedTexels, fast.RecordedTexels);
        Assert.Equal(alone.ToTexels(), blocks.ToTexels());
        Assert.Equal(alone.Digest, blocks.Digest);
    }

    sealed class Open : IPaintStencil {
        public float At(Vector2 picture) => 1f;
    }
}
