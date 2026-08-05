// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Vixen.Shaders.Generators;
using Xunit;

namespace Tests;

/// <summary>
///     The generated bindings, compiled and run.
/// </summary>
/// <remarks>
///     <para>
///         <c>Generated/Lighting.Bindings.g.cs</c> is checked in and compiled as part of this
///         project, which is the only way to assert the two things that matter and that a string
///         comparison cannot reach: <strong>the emitted C# compiles</strong>, and
///         <strong>running it puts the right bytes in the right places</strong>. A generator whose
///         output is only ever compared to a golden string can emit something that does not build.
///     </para>
///     <para>
///         <see cref="The_checked_in_bindings_are_what_the_emitter_produces" /> keeps the file
///         honest: it is regenerated from the same fixture and compared, so it cannot drift from the
///         emitter without a test saying so. Set <c>VIXEN_REGENERATE=1</c> to rewrite it, then read
///         the diff — that is the review step, and it is the point of checking the file in.
///     </para>
/// </remarks>
public class GeneratedBindingsTests {
    static string GeneratedPath =>
        Path.Combine(
            Path.GetDirectoryName(typeof(GeneratedBindingsTests).Assembly.Location)!,
            "..", "..", "..", "Generated", "Lighting.Bindings.g.cs"
        );

    [Fact]
    public void The_checked_in_bindings_are_what_the_emitter_produces() {
        var emitted = BindingsEmitter.Emit("Lighting", Fixture.Reflection, "Lighting.reflect.json");

        if (Environment.GetEnvironmentVariable("VIXEN_REGENERATE") == "1") {
            Directory.CreateDirectory(Path.GetDirectoryName(GeneratedPath)!);
            File.WriteAllText(GeneratedPath, emitted);
        }

        Assert.Equal(emitted.ReplaceLineEndings(), File.ReadAllText(GeneratedPath).ReplaceLineEndings());
    }

    // --- The keys are usable ------------------------------------------------

    [Fact]
    public void The_keys_are_interned_under_their_shader_qualified_names() {
        Assert.Equal("Lighting.UseShadows", LightingKeys.UseShadows.Name);
        Assert.False(LightingKeys.UseShadows.DefaultValue);
        Assert.Equal(4, LightingKeys.MaxLights.DefaultValue);

        // Interning is by name, so asking again is the same object — which is what makes a key
        // cheap as a dictionary key and what makes two assemblies agree.
        Assert.Same(LightingKeys.UseShadows, ParameterKeys.NewPermutation(false, "Lighting.UseShadows"));
    }

    [Fact]
    public void A_permutation_key_and_a_value_key_are_different_kinds() {
        Assert.True(LightingKeys.UseShadows.IsPermutation);
        Assert.False(LightingKeys.Albedo.IsPermutation);
    }

    /// <summary>
    ///     Every value in the block has a key, so a caller that knows the shader only by name can
    ///     fill it.
    /// </summary>
    /// <remarks>
    ///     Two ways to fill one block, for two callers. Code that knows the shader at compile time
    ///     assigns <c>LightingConstants</c> fields and calls <c>Write</c>. Code that knows it by name
    ///     — a material read from an asset, a post-process node configured by a compositor document —
    ///     has no generated type to assign to and sets these through a <c>ParameterCollection</c>.
    ///     Without them it interns the key from a string, which works and gives up every guarantee
    ///     interning exists for.
    /// </remarks>
    [Fact]
    public void Every_value_in_the_block_has_a_key_of_its_own() {
        Assert.Equal("Lighting.exposure", LightingKeys.Exposure.Name);
        Assert.Equal("Lighting.worldViewProjection", LightingKeys.WorldViewProjection.Name);
        Assert.Equal("Lighting.ambient", LightingKeys.Ambient.Name);

        Assert.Same(LightingKeys.Exposure, ParameterKeys.New<float>("Lighting.exposure"));
    }

    /// <summary>
    ///     A key carries what the shader declared, so a parameter nobody set is not zero.
    /// </summary>
    /// <remarks>
    ///     The end of a chain that starts at <c>var exposure: float = 1f</c>: Raven reports the
    ///     initialiser, the generator spells it as a literal, the key holds it as bytes, and a buffer
    ///     writer fills the parameters nobody mentioned from it. Break any link and the shader gets
    ///     zero exposure — a black frame produced by a parameter nobody touched, reported by nothing.
    /// </remarks>
    [Fact]
    public void A_value_key_carries_the_default_the_shader_declared() {
        Assert.Equal(1f, LightingKeys.Exposure.DefaultValue);

        // ⚠ Zero because the shader declares `var lightCount: int = 0`. This asserted `2` for as
        // long as the fixture has existed — a checked-in reflection said so and the shader never
        // did. Every test in this file reads the JSON, so nothing compared the two until it was
        // regenerated. It is precisely the chain the remarks above describe, broken at link one.
        Assert.Equal(0, LightingKeys.LightCount.DefaultValue);
        Assert.Equal(1u, LightingKeys.Enabled.DefaultValue);

        Assert.Equal(1f, BitConverter.ToSingle(LightingKeys.Exposure.DefaultBytes));

        // Nothing declared is nothing carried, rather than a zero that looks authored.
        Assert.Equal(default, LightingKeys.Ambient.DefaultValue);
    }

    /// <summary>A key is set and read back through a collection, which is the path that needed it.</summary>
    [Fact]
    public void A_value_key_round_trips_through_a_collection() {
        var parameters = new ParameterCollection();

        Assert.Equal(1f, parameters.Get(LightingKeys.Exposure));

        parameters.Set(LightingKeys.Exposure, 4f);

        Assert.Equal(4f, parameters.Get(LightingKeys.Exposure));
        Assert.Equal(4f, BitConverter.ToSingle(parameters.Bytes(LightingKeys.Exposure)));
    }

    // --- The writer puts bytes where the shader reads them -------------------

    /// <summary>
    ///     The padding case, which is the one this whole arrangement exists to get right.
    /// </summary>
    /// <remarks>
    ///     <c>ambient</c> is a <c>float3</c> at 112 and <c>exposure</c> a <c>float</c> at 124: std140
    ///     put them in the same sixteen-byte slot. Writing sixteen bytes for the vector — which a
    ///     <c>Vector4</c>-shaped writer would — silently clears the scalar, and the symptom is
    ///     "setting the ambient colour resets the exposure", three subsystems away from the cause.
    /// </remarks>
    [Fact]
    public void A_float3_writes_twelve_bytes_and_leaves_the_float_beside_it_alone() {
        var buffer = new byte[LightingConstants.Size];

        var constants = new LightingConstants {
            Ambient = new(1f, 2f, 3f),
            Exposure = 9f
        };

        constants.Write(buffer);

        Assert.Equal(1f, BitConverter.ToSingle(buffer, 112));
        Assert.Equal(2f, BitConverter.ToSingle(buffer, 116));
        Assert.Equal(3f, BitConverter.ToSingle(buffer, 120));
        Assert.Equal(9f, BitConverter.ToSingle(buffer, 124));
    }

    /// <summary>
    ///     A <c>mat4</c> reaches the buffer unchanged, translation and all.
    /// </summary>
    /// <remarks>
    ///     The engine stores row-major with the translation in <c>M41..M43</c>; the shader reads the
    ///     same bytes as <c>ColMajor</c>, which makes its matrix the transpose and puts the
    ///     translation in column 3 — where <c>Mᵀ·v</c> expects it. So the correct thing to do with
    ///     the sixty-four bytes is nothing. See docs/plan/07 § E.
    /// </remarks>
    [Fact]
    public void A_mat4_reaches_the_buffer_byte_for_byte() {
        var buffer = new byte[LightingConstants.Size];

        var matrix = new Matrix4x4(
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f),
            new Vector4(7f, 8f, 9f, 1f)
        );

        new LightingConstants { WorldViewProjection = matrix }.Write(buffer);

        // M41..M43 — the translation — is where the host put it, at floats 12..14.
        Assert.Equal(7f, BitConverter.ToSingle(buffer, 48));
        Assert.Equal(8f, BitConverter.ToSingle(buffer, 52));
        Assert.Equal(9f, BitConverter.ToSingle(buffer, 56));
    }

    /// <summary>A <c>mat3</c>'s rows become the shader's columns, each padded to sixteen bytes.</summary>
    [Fact]
    public void A_mat3_is_written_as_three_padded_columns() {
        var buffer = new byte[LightingConstants.Size];

        new LightingConstants {
            NormalMatrix = new Matrix3x3(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f)
        }.Write(buffer);

        // Host row 1 at column 0, row 2 at column 1, row 3 at column 2 — 16 bytes apart, not 12.
        Assert.Equal(1f, BitConverter.ToSingle(buffer, 64));
        Assert.Equal(3f, BitConverter.ToSingle(buffer, 72));
        Assert.Equal(4f, BitConverter.ToSingle(buffer, 80));
        Assert.Equal(7f, BitConverter.ToSingle(buffer, 96));
    }

    /// <summary>A flag is one bit of meaning and four bytes of buffer, and the writer owes all four.</summary>
    /// <remarks>
    ///     The field was a <c>bool</c> until <c>RVN2137</c> refused one in a binding — SPIR-V allows
    ///     <c>bool</c> only in storage classes a host cannot write. The type changed and the
    ///     assertion did not, which is the point: the four bytes were never the <c>bool</c>'s size.
    ///     They are the block's, and a writer that fills one of them leaves three <c>0xFF</c>s that
    ///     read as a flag nobody set.
    /// </remarks>
    [Fact]
    public void A_flag_occupies_the_four_bytes_the_shader_reads() {
        var buffer = new byte[LightingConstants.Size];
        Array.Fill(buffer, (byte)0xFF);

        new LightingConstants { Enabled = 0u }.Write(buffer);

        Assert.Equal(0, BitConverter.ToInt32(buffer, 132));
    }

    [Fact]
    public void A_scalar_array_is_spaced_by_its_std140_stride() {
        var buffer = new byte[LightingConstants.Size];

        new LightingConstants { Weights = [1f, 2f, 3f, 4f] }.Write(buffer);

        Assert.Equal(1f, BitConverter.ToSingle(buffer, 144));
        Assert.Equal(2f, BitConverter.ToSingle(buffer, 160));
        Assert.Equal(4f, BitConverter.ToSingle(buffer, 192));
    }

    /// <summary>An array longer than the shader declared is truncated, not overflowed.</summary>
    /// <remarks>
    ///     Truncated rather than throwing because the length is the host's every frame: a list sized
    ///     for four and handed six is more lights than this variant was compiled for, which is worth
    ///     noticing where the count is decided rather than inside a memcpy. Nothing is written past
    ///     the array's own room either way.
    /// </remarks>
    [Fact]
    public void An_over_long_array_stops_at_the_declared_length() {
        var buffer = new byte[LightingConstants.Size];

        new LightingConstants { Weights = [1f, 2f, 3f, 4f, 5f, 6f] }.Write(buffer);

        // Slot 4 would start at 208, which is where the light list begins.
        Assert.Equal(0f, BitConverter.ToSingle(buffer, 208));
    }

    // --- The struct array ---------------------------------------------------

    [Fact]
    public void A_light_is_written_into_its_slot_at_the_element_stride() {
        var buffer = new byte[LightingConstants.Size];

        var light = new LightingLightsElement {
            Position = new(1f, 2f, 3f),
            Range = 4f,
            Color = new(5f, 6f, 7f),
            Intensity = 8f
        };

        light.Write(buffer, 2);

        var at = LightingLightsElement.BaseOffset + 2 * LightingLightsElement.Stride;
        Assert.Equal(1f, BitConverter.ToSingle(buffer, at));
        Assert.Equal(4f, BitConverter.ToSingle(buffer, at + 12));
        Assert.Equal(5f, BitConverter.ToSingle(buffer, at + 16));
        Assert.Equal(8f, BitConverter.ToSingle(buffer, at + 28));
    }

    [Fact]
    public void A_slot_past_the_declared_count_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LightingLightsElement().Write(new byte[LightingConstants.Size], LightingLightsElement.Count)
        );

    [Fact]
    public void A_buffer_too_small_for_the_block_is_refused_before_anything_is_written() {
        var error = Assert.Throws<ArgumentException>(() => new LightingConstants().Write(new byte[16]));
        Assert.Contains("336", error.Message, StringComparison.Ordinal);
    }
}
