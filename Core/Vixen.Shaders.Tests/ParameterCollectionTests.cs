// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Values and permutations set against keys — docs/plan/06 § Effect permutations.
/// </summary>
public class ParameterCollectionTests {
    static ParameterKey<T> Value<T>(string name, T fallback = default!) where T : unmanaged =>
        ParameterKeys.New($"Test.Collection.{name}", fallback);

    static PermutationKey<T> Permutation<T>(string name, T fallback) where T : notnull =>
        ParameterKeys.NewPermutation(fallback, $"Test.Collection.{name}");

    [Fact]
    public void A_value_comes_back_as_it_went_in() {
        var parameters = new ParameterCollection();
        var tint = Value<Vector4>("Tint");

        parameters.Set(tint, new(1f, 2f, 3f, 4f));

        Assert.Equal(new Vector4(1f, 2f, 3f, 4f), parameters.Get(tint));
    }

    /// <summary>
    ///     An unset key gives the shader's own default, not zero.
    /// </summary>
    /// <remarks>
    ///     A shader writes <c>var exposure: float = 1f</c> and the generated key carries that 1. A
    ///     material that never mentions exposure should render as its author intended rather than
    ///     black, which is what a <c>default(T)</c> would give.
    /// </remarks>
    [Fact]
    public void An_unset_key_gives_the_shaders_declared_default() {
        var parameters = new ParameterCollection();
        var exposure = Value("Exposure", 1.5f);

        Assert.Equal(1.5f, parameters.Get(exposure));
        Assert.False(parameters.Has(exposure));
    }

    [Fact]
    public void Overwriting_a_value_reuses_its_slot() {
        var parameters = new ParameterCollection();
        var count = Value<int>("Count");

        parameters.Set(count, 1);
        parameters.Set(count, 2);
        parameters.Set(count, 3);

        Assert.Equal(3, parameters.Get(count));
        Assert.Equal(1, parameters.Count);
    }

    [Fact]
    public void The_version_moves_when_a_value_changes() {
        var parameters = new ParameterCollection();
        var before = parameters.Version;

        parameters.Set(Value<float>("Versioned"), 1f);

        Assert.NotEqual(before, parameters.Version);
    }

    /// <summary>
    ///     Setting a value to the one it already holds does not move the version.
    /// </summary>
    /// <remarks>
    ///     The same rule permutations have always had, and for the same reason one level down: the
    ///     version is what a constant-buffer writer skips work on, and a node that re-asserts its
    ///     parameters every frame — a post-process chain reconfiguring itself — would otherwise
    ///     re-upload a block in which nothing had changed, every frame, forever.
    /// </remarks>
    [Fact]
    public void Setting_a_value_to_what_it_already_is_changes_nothing() {
        var parameters = new ParameterCollection();
        var key = Value<float>("Unchanged");

        parameters.Set(key, 1f);
        var settled = parameters.Version;

        parameters.Set(key, 1f);
        Assert.Equal(settled, parameters.Version);

        parameters.Set(key, 2f);
        Assert.NotEqual(settled, parameters.Version);
    }

    /// <summary>
    ///     A key set for the first time always moves the version, whatever was in the buffer.
    /// </summary>
    /// <remarks>
    ///     The check on the one above. A slot handed out after a <see cref="ParameterCollection.Clear" />
    ///     holds whatever the last fill left there, so comparing against it would occasionally decide
    ///     a brand-new key had not changed — a value that never reaches the GPU because nothing
    ///     believed it was new.
    /// </remarks>
    [Fact]
    public void A_key_set_after_a_clear_still_moves_the_version() {
        var parameters = new ParameterCollection();
        var key = Value<float>("Recycled");

        parameters.Set(key, 7f);
        parameters.Clear();

        var settled = parameters.Version;
        parameters.Set(key, 7f);

        Assert.NotEqual(settled, parameters.Version);
        Assert.Equal(7f, parameters.Get(key));
    }

    /// <summary>
    ///     Setting a permutation to the value it already had does not invalidate the effect.
    /// </summary>
    /// <remarks>
    ///     The permutation version is what decides whether the effect is re-resolved, and a material
    ///     that re-asserts its settings every frame is entirely ordinary — without this, every one of
    ///     them would look like a shader change once a frame.
    /// </remarks>
    [Fact]
    public void Setting_a_permutation_to_what_it_already_was_changes_nothing() {
        var parameters = new ParameterCollection();
        var shadows = Permutation("Shadows", false);

        parameters.Set(shadows, true);
        var settled = parameters.PermutationVersion;

        parameters.Set(shadows, true);

        Assert.Equal(settled, parameters.PermutationVersion);

        parameters.Set(shadows, false);
        Assert.NotEqual(settled, parameters.PermutationVersion);
    }

    /// <summary>Values and permutations are separate: setting one does not disturb the other's version.</summary>
    /// <remarks>
    ///     The two versions are read by different machinery — the constant-buffer writer watches one,
    ///     the effect resolver the other — so a per-frame colour change must not look like a shader
    ///     change, or every material would recompile once a frame.
    /// </remarks>
    [Fact]
    public void A_value_change_is_not_a_shader_change() {
        var parameters = new ParameterCollection();
        parameters.Set(Permutation("SplitPermutation", false), true);

        var permutations = parameters.PermutationVersion;
        parameters.Set(Value<float>("SplitValue"), 3f);

        Assert.Equal(permutations, parameters.PermutationVersion);
    }

    /// <summary>Applying one collection over another overrides only what the source sets.</summary>
    /// <remarks>
    ///     What layering a material instance over its material is made of: the common case is
    ///     changing one colour, and a full replacement would lose everything else.
    /// </remarks>
    [Fact]
    public void Applying_overrides_only_what_the_source_sets() {
        var material = new ParameterCollection();
        var tint = Value<Vector4>("ApplyTint");
        var rough = Value("ApplyRough", 0.5f);

        material.Set(tint, new(1f, 1f, 1f, 1f));
        material.Set(rough, 0.25f);

        var instance = new ParameterCollection();
        instance.Set(tint, new(1f, 0f, 0f, 1f));

        material.Apply(instance);

        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), material.Get(tint));
        Assert.Equal(0.25f, material.Get(rough));
    }

    [Fact]
    public void Applying_carries_permutations_too() {
        var target = new ParameterCollection();
        var source = new ParameterCollection();
        var shadows = Permutation("ApplyShadows", false);

        source.Set(shadows, true);
        target.Apply(source);

        Assert.True(target.Get(shadows));
    }

    [Fact]
    public void Applying_a_collection_to_itself_is_harmless() {
        var parameters = new ParameterCollection();
        var value = Value("SelfApply", 2f);
        parameters.Set(value, 7f);

        parameters.Apply(parameters);

        Assert.Equal(7f, parameters.Get(value));
    }

    [Fact]
    public void Clearing_forgets_everything_and_restores_the_defaults() {
        var parameters = new ParameterCollection();
        var value = Value("Cleared", 4f);

        parameters.Set(value, 9f);
        parameters.Clear();

        Assert.Equal(4f, parameters.Get(value));
        Assert.Equal(0, parameters.Count);
    }

    /// <summary>The raw bytes are what a constant-buffer writer copies.</summary>
    [Fact]
    public void The_bytes_of_a_value_are_readable_without_knowing_its_type() {
        var parameters = new ParameterCollection();
        var value = Value<float>("Bytes");

        parameters.Set(value, 1f);

        Assert.Equal(4, parameters.Bytes(value).Length);
        Assert.Equal(1f, BitConverter.ToSingle(parameters.Bytes(value)));
        Assert.True(parameters.Bytes(Value<float>("BytesUnset")).IsEmpty);
    }

    /// <summary>Many values are packed into one buffer rather than one allocation each.</summary>
    [Fact]
    public void Many_values_share_one_buffer() {
        var parameters = new ParameterCollection();

        for (var i = 0; i < 200; i++) {
            parameters.Set(ParameterKeys.New<int>($"Test.Collection.Many.{i}"), i);
        }

        for (var i = 0; i < 200; i++) {
            Assert.Equal(i, parameters.Get(ParameterKeys.New<int>($"Test.Collection.Many.{i}")));
        }
    }
}
