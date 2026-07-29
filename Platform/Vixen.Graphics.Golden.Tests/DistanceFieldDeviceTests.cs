// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Shaders;
using Vixen.ShaderCompiler;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The traced pass, put through the real compiler and asked what it binds.
/// </summary>
/// <remarks>
///     <para>
///         Everything about this pass has so far been checked by something that agrees with whatever
///         it is told. The reflection is what the compiler produced, and the bindings generator turned
///         it into keys — but nothing had ever compiled the variant a frame actually asks for, and
///         nothing had held the names a host writes against the names the shader declares. Two tests
///         agreeing with each other is what that is, and the default prefix on <c>Apply</c> was wrong
///         for months of commits under exactly that arrangement.
///     </para>
///     <para>
///         This compiles it for real and reads the plan back. It does not yet draw — see the note on
///         <see cref="TheClipmapsNamesAreTheOnesTheShaderDeclares" /> for what a picture would need.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class DistanceFieldDeviceTests {
    /// <summary>
    ///     The variant a frame asks for compiles, with the clipmap behind the slot rather than the
    ///     empty field a material defaults it to.
    /// </summary>
    [Fact]
    public void TheTracedVariantCompiles() {
        var data = Compiler().TryGet(Key(Tracing()));

        Assert.NotNull(data);
        Assert.NotEmpty(data!.Stages);
    }

    /// <summary>
    ///     And with the null field, which is what every material in a project that traces nothing
    ///     compiles against — the case that broke every material when the slot had no filler.
    /// </summary>
    [Fact]
    public void TheEmptyFieldVariantCompilesToo() {
        var data = Compiler().TryGet(Key(Composition()));

        Assert.NotNull(data);
        Assert.NotEmpty(data!.Stages);
    }

    /// <summary>
    ///     <b>The contract, resolved by the compiler rather than agreed between two tests.</b> Every
    ///     name <see cref="GlobalDistanceFieldTexture" /> writes is a name the compiled shader
    ///     declares — so a rename on either side fails here instead of binding nothing, silently, and
    ///     rendering a world with a surface everywhere.
    /// </summary>
    /// <remarks>
    ///     What a picture would additionally need, and why this stops short of one: the pass reads
    ///     four volume textures, and <see cref="Fixture" /> can upload a 2D texture but has no
    ///     three-dimensional path. That is the next piece, and it is plumbing rather than a question.
    /// </remarks>
    [Fact]
    public void TheClipmapsNamesAreTheOnesTheShaderDeclares() {
        var data = Compiler().TryGet(Key(Tracing()));

        Assert.NotNull(data);

        var declared = data!.Bindings.Select(binding => binding.Name).ToHashSet(StringComparer.Ordinal);
        var parameters = data.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);

        // A binding's own name carries the shader that declared it and no pass — `EffectSetWriter`
        // is what puts the pass back on, resolving `{ShaderName}.{binding.Name}`. So the key a host
        // writes is the two joined, and comparing a raw binding name against a parameter key compares
        // two different things. This asserts the join, which is what actually has to match.
        const string Pass = "DistanceFieldAo";

        var keys = declared.Select(name => $"{Pass}.{name}").ToHashSet(StringComparer.Ordinal);

        Assert.Contains(GlobalDistanceFieldTexture.SamplerBinding($"{Pass}.{Source}"), keys);

        // The array is one binding with a count, so its key has no index — the index is the element
        // `EffectSetWriter` appends when it fills the array's slots one at a time.
        Assert.Contains($"{Pass}.{Source}.distanceFieldLevels", keys);

        for (var level = 0; level < 4; level++) {
            foreach (var member in (string[]) ["minimum", "maximum", "inverseCellSize", "maxDistance"]) {
                Assert.Contains($"{Pass}.{Source}.distanceFieldVolumes[{level}].{member}", parameters);
            }
        }

        // And the indexed form a host writes is the one the writer looks for.
        Assert.Equal(
            $"{Pass}.{Source}.distanceFieldLevels[2]",
            GlobalDistanceFieldTexture.LevelBinding(2, $"{Pass}.{Source}")
        );
    }

    /// <summary>
    ///     <b>A composed shader's permutations are not selectable through the pass that composes it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>GlobalDistanceField</c> declares <c>LevelCount</c>, and it sizes a binding — the
    ///         array of volume textures is exactly that many descriptors. But the compiler surfaces
    ///         only the pass's own permutations, so a host has no key to set it with and every
    ///         compilation gets the shader's declared default.
    ///     </para>
    ///     <para>
    ///         The consequence is worth being plain about: <b>a clipmap with a level count other than
    ///         the shader's default binds the wrong number of descriptors</b>, and nothing on either
    ///         side currently notices. Until the compiler forwards a composed shader's keys, four
    ///         levels is not a default — it is the only value, and a project wanting two needs the
    ///         shader edited rather than a setting changed.
    ///     </para>
    ///     <para>
    ///         Asserted rather than left as a note, so that the day it becomes selectable this test
    ///         fails and says where to look.
    ///     </para>
    /// </remarks>
    [Fact]
    public void OnlyThePassesOwnPermutationsAreSelectable() {
        var declared = Compiler().Declared("DistanceFieldAo", Tracing()).Select(info => info.Name).ToArray();

        Assert.Equal(["SunShadow"], declared);
        Assert.DoesNotContain("LevelCount", declared);
    }

    /// <summary>The shader behind the slot, whose name every binding of the field carries.</summary>
    const string Source = "GlobalDistanceField";

    static EffectKey Key(ShaderComposition composition) => EffectKey.Of("DistanceFieldAo", [], composition);

    /// <summary>The composition a material produces, with the clipmap put behind the field slot.</summary>
    static ShaderComposition Tracing() {
        var slots = Composition()
            .Slots
            .Where(pair => !pair.Key.EndsWith("distanceField", StringComparison.Ordinal))
            .ToList();

        slots.Add(new("distanceField", "GlobalDistanceField"));

        return ShaderComposition.Of(slots);
    }

    /// <summary>Everything the library declares, filled the way the engine fills it.</summary>
    static ShaderComposition Composition() {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "DistanceFieldAo",
                Features = [new MetalRoughnessFeature { BaseColor = Vector3.One, Metalness = 0f, Roughness = 0.6f }]
            }
        );

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!.Composition;
    }

    static RavenEffectCompiler Compiler() => RavenEffects.Everything();
}
