// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     The cache's disk tier: a result kept by one evaluator is read by a fresh one, which stands for a later process,
///     instead of being run, and is the same picture.
/// </summary>
/// <remarks>
///     The plan is <see cref="TextureIncrementalDeviceTests" />' chain: <c>ramp → a → b → c</c> and <c>a → d</c>, with
///     <c>c</c> and <c>d</c> the outputs. Counters rather than clocks: <see cref="TextureBake.Ran" /> is the claim.
/// </remarks>
public class TextureResultStoreDeviceTests {
    const int Side = TextureKernelHarness.Side;
    const int Ramp = 0;
    const int A = 1;
    const int B = 2;
    const int C = 3;
    const int D = 4;

    static TextureOp Levels(int output, int input, float gamma = 1f) =>
        new() {
            Kernel = "Levels",
            Output = output,
            Inputs = [input],
            Parameters = [
                new("inputBlack", 0f),
                new("inputWhite", 1f),
                new("gamma", gamma),
                new("outputBlack", 0f),
                new("outputWhite", 1f),
                new("dither", 0f)
            ]
        };

    static TexturePlan Chain(float gamma = 1f) =>
        new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float)
            ],
            Ops = [Levels(A, Ramp), Levels(B, A), Levels(C, B, gamma), Levels(D, A)],
            Outputs = [C, D]
        };

    /// <summary>
    ///     ⚠ Kept, then read by an evaluator that never ran the plan: the outputs come from the store and nothing
    ///     runs, and an edit of the last op runs that op alone, reading what it needs from the store too. Without
    ///     the store the fresh evaluator runs all four ops, which is the sabotage this would go red under.
    /// </summary>
    [Fact]
    public void A_kept_result_is_read_by_a_later_evaluator_instead_of_run() {
        using var device = TextureKernelHarness.Open();
        var store = new MemoryStore();
        byte[] c;
        byte[] d;

        using (var first = new TexturePlanEvaluator(device) { Results = { Store = store } })
        using (var uploads = new TextureUploads(device)) {
            var plan = Chain();
            uploads.Add(plan, Ramp, Side, Side, TextureKernelHarness.Ramp(Side));

            using var bake = first.EvaluateIncremental(plan, uploads.Externals);

            Assert.Equal([0, 1, 2, 3], bake.Ran);
            Assert.Empty(store.Kept);

            // ⚠ A save's bake runs nothing and so holds only the outputs; b is the cache's, and is kept from there.
            using var saved = first.EvaluateIncremental(plan, uploads.Externals);

            Assert.Empty(saved.Ran);

            // The ramp is the caller's and not kept; b, c and d are, once each.
            Assert.Equal(3, first.Persist(saved, [Ramp, B, C, D]));
            Assert.Equal(0, first.Persist(saved, [B, C, D]));
            c = bake.ReadTexels(C);
            d = bake.ReadTexels(D);
        }

        using var later = new TexturePlanEvaluator(device) { Results = { Store = store } };
        using var again = new TextureUploads(device);
        var same = Chain();
        again.Add(same, Ramp, Side, Side, TextureKernelHarness.Ramp(Side));

        using (var reopened = later.EvaluateIncremental(same, again.Externals)) {
            Assert.Empty(reopened.Ran);
            Assert.Equal(2, later.Results.Recalled);
            Assert.Equal(c, reopened.ReadTexels(C));
            Assert.Equal(d, reopened.ReadTexels(D));
        }

        var edited = Chain(0.5f);
        using var uploaded = new TextureUploads(device);
        uploaded.Add(edited, Ramp, Side, Side, TextureKernelHarness.Ramp(Side));

        using var after = later.EvaluateIncremental(edited, uploaded.Externals);

        Assert.Equal([2], after.Ran);
        Assert.Equal(3, later.Results.Recalled);

        using var clean = new TexturePlanEvaluator(device);
        using var bare = clean.Evaluate(edited, uploaded.Externals);

        Assert.Equal(bare.ReadTexels(C), after.ReadTexels(C));
    }

    /// <summary>A store that holds nothing for a shape other than the one kept is a miss, not a wrong picture.</summary>
    [Fact]
    public void A_kept_result_of_another_shape_is_run_instead() {
        using var device = TextureKernelHarness.Open();
        var store = new MemoryStore();

        using (var first = new TexturePlanEvaluator(device) { Results = { Store = store } })
        using (var uploads = new TextureUploads(device)) {
            var plan = Chain();
            uploads.Add(plan, Ramp, Side, Side, TextureKernelHarness.Ramp(Side));

            using var bake = first.EvaluateIncremental(plan, uploads.Externals);

            first.Persist(bake, [C, D]);
        }

        foreach (var key in store.Kept.Keys.ToArray()) {
            store.Kept[key] = store.Kept[key][..^1];
        }

        using var later = new TexturePlanEvaluator(device) { Results = { Store = store } };
        using var again = new TextureUploads(device);
        var same = Chain();
        again.Add(same, Ramp, Side, Side, TextureKernelHarness.Ramp(Side));

        using var reopened = later.EvaluateIncremental(same, again.Externals);

        Assert.Equal([0, 1, 2, 3], reopened.Ran);
        Assert.Equal(0, later.Results.Recalled);
    }

    sealed class MemoryStore : ITextureResultStore {
        public Dictionary<TextureResultKey, byte[]> Kept { get; } = [];

        public bool Has(TextureResultKey key) => Kept.ContainsKey(key);

        public byte[]? Read(TextureResultKey key, TexturePoolSlot shape) => Kept.GetValueOrDefault(key);

        public void Write(TextureResultKey key, TexturePoolSlot shape, ReadOnlySpan<byte> texels) =>
            Kept[key] = texels.ToArray();
    }
}
