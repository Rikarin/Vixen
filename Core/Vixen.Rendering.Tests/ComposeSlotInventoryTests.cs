// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering.Materials;
using Xunit;

namespace Tests;

/// <summary>
///     Every compose slot the shipped library declares is one the engine binds.
/// </summary>
/// <remarks>
///     <para>
///         <b>Not a style rule — a compile.</b> <c>RVN2073</c> requires every slot declared anywhere
///         in a compilation to be bound, whether or not the shader being compiled reaches it. So a
///         slot added to the library and not added to <see cref="MaterialCompiler" /> does not break
///         the shader that declared it; it breaks <i>every material in the project</i>, at the first
///         thing that compiles one.
///     </para>
///     <para>
///         There is a test in the Raven tree that lists the slots and compares them against a written
///         array. That one is a reminder, and it can be satisfied by editing the array — which is
///         exactly what happened when <c>distanceField</c> was added, and the two device tests that
///         actually compile a material are what caught it. This reads the engine's own inventory
///         instead, so the only way to satisfy it is to bind the slot.
///     </para>
/// </remarks>
public class ComposeSlotInventoryTests {
    /// <summary>
    ///     Every compose slot the shipped library declares without naming its own default.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A slot that names a default is not the engine's problem.</b>
    ///         <c>compose val irradiance: IIrradianceSource = NoIrradiance</c> resolves to that shader
    ///         in any compilation that says nothing about it, so forgetting it here breaks nothing —
    ///         and a test that failed anyway would be asserting a rule that no longer exists, which is
    ///         worse than no test because it blocks the change that made it obsolete.
    ///     </para>
    ///     <para>
    ///         The slots below it still is: <c>surface</c>, <c>shading</c> and the feature chain are
    ///         choices a material has to make, and a default for those would be a material that
    ///         silently compiled as something else.
    ///     </para>
    /// </remarks>
    static string[] Declared() {
        var declared = Directory
            .EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Shaders"), "*.rvn", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("compose val ", StringComparison.Ordinal))
            .Where(line => !line.Contains('=', StringComparison.Ordinal))
            .Select(line => line["compose val ".Length..].Split(':')[0].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(declared);

        return declared;
    }

    /// <summary>
    ///     And the library does default the optional ones, which is what makes the filter above honest.
    /// </summary>
    /// <remarks>
    ///     Without this the filter is a hole: a slot could stop being declared, or stop being defaulted,
    ///     and the inventory test would go quiet either way. These four are the slots a shader declares
    ///     that its consumers cannot be expected to know about — a pass that can trace, a pass that can
    ///     read a field, a surface that can blend two others.
    /// </remarks>
    [Theory]
    [InlineData("irradiance", MaterialCompiler.EmptyIrradianceShader)]
    [InlineData("distanceField", MaterialCompiler.EmptyFieldShader)]
    [InlineData("surfaceCache", MaterialCompiler.EmptySurfaceCacheShader)]
    [InlineData("under", MaterialCompiler.IdentityShader)]
    [InlineData("over", MaterialCompiler.IdentityShader)]
    public void EveryOptionalSlotNamesItsOwnDefaultInTheLibrary(string slot, string expected) {
        var declarations = Directory
            .EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Shaders"), "*.rvn", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith($"compose val {slot}:", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(declarations);

        foreach (var declaration in declarations) {
            Assert.EndsWith($"= {expected}", declaration, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheEngineBindsEverySlotTheLibraryDeclares() {
        var declared = Declared();

        var bound = new HashSet<string>(MaterialCompiler.ChainSlots, StringComparer.Ordinal) {
            "surface",
            "shading"
        };

        foreach (var (_, slot, _) in MaterialCompiler.OptionalSlots) {
            bound.Add(slot);
        }

        foreach (var slot in declared) {
            Assert.True(
                bound.Contains(slot),
                $"the library declares compose slot '{slot}' and MaterialCompiler binds nothing for it — "
                + "every material in the project fails to compile with RVN2073 until it does"
            );
        }
    }

    /// <summary>
    ///     And a slot's filler has to be a shader that satisfies its protocol, which the identity
    ///     surface does not for a distance field.
    /// </summary>
    /// <param name="slot">The slot, which more than one shader may declare.</param>
    /// <param name="expected">What every one of them has to be filled with.</param>
    /// <remarks>
    ///     Every entry rather than the only one, because a slot is not unique to a shader: both the
    ///     traced pass and the fill shader declare <c>distanceField</c>, and each has to be filled
    ///     where it is <i>declared</i> rather than where it is used.
    /// </remarks>
    [Theory]
    [InlineData("distanceField", MaterialCompiler.EmptyFieldShader)]
    [InlineData("irradiance", MaterialCompiler.EmptyIrradianceShader)]
    public void ATypedSlotIsFilledByItsOwnKindRatherThanASurface(string slot, string expected) {
        var entries = MaterialCompiler.OptionalSlots.Where(entry => entry.Slot == slot).ToArray();

        Assert.NotEmpty(entries);

        foreach (var (_, _, filler) in entries) {
            Assert.Equal(expected, filler);
            Assert.NotEqual(MaterialCompiler.IdentityShader, filler);
        }
    }

    /// <summary>
    ///     <b>And a pass's composition binds every one of them too — not only the typed ones.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The same rule as the test above and the same reading of the library, applied to the
    ///         other path. <c>RVN2073</c> asks the compilation rather than the shader, so a compute
    ///         shader or a post pass compiled beside <c>ForwardPlus</c> has to answer for
    ///         <c>surface</c>, <c>shading</c> and every link of the feature chain, none of which it can
    ///         reach.
    ///     </para>
    ///     <para>
    ///         ⚠ This is the assertion that was missing rather than failing. The pass path had a
    ///         cross-check against the material path's fillers and no completeness check at all, and
    ///         every test that compiled a pass narrowed its source set to that pass's own packages —
    ///         so a composition that could never compile in an application, which has one effect
    ///         system serving the whole library, passed everything for as long as it existed.
    ///     </para>
    /// </remarks>
    [Fact]
    public void APassCompositionBindsEverySlotTheLibraryDeclares() {
        var bound = MaterialCompiler.PassComposition().Slots
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var slot in Declared()) {
            Assert.True(
                bound.Contains(slot),
                $"the library declares compose slot '{slot}' and a pass composition names nothing for "
                + "it — every compute and post-process variant fails with RVN2073 against an effect "
                + "system that serves the whole library, which is every application"
            );
        }
    }

    /// <summary>
    ///     And a pass naming one typed slot still names the others, because a compilation refuses an
    ///     unbound slot wherever its sources declare it — not only where the shader reaches it.
    /// </summary>
    [Fact]
    public void APassComposesEveryTypedSlotAndNotOnlyItsOwn() {
        var composition = MaterialCompiler.PassComposition("irradiance", "IrradianceFieldProbes");
        var slots = composition.Slots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        Assert.Equal("IrradianceFieldProbes", slots["irradiance"]);
        Assert.Equal(MaterialCompiler.EmptyFieldShader, slots["distanceField"]);

        // And the surface slots it will never reach, which is what lets it compile beside a material.
        Assert.Equal(MaterialCompiler.IdentityShader, slots["surface"]);
        Assert.Equal(MaterialCompiler.DefaultShadingShader, slots["shading"]);
        Assert.Equal(MaterialCompiler.IdentityShader, slots[MaterialCompiler.ChainSlots[0]]);
    }

    /// <summary>
    ///     <b>And the shading model a pass names is the one a material gets by default.</b>
    /// </summary>
    /// <remarks>
    ///     A pass cannot leave <c>shading</c> unbound and has nothing to say about it, so what it names
    ///     should be the model already being compiled rather than a second one. Naming a different
    ///     model would compile <c>ForwardPlus</c> again under a second key, for a slot no pass reaches
    ///     — a pipeline nobody draws with, paid for at load.
    /// </remarks>
    [Fact]
    public void ThePassDefaultShadingModelIsTheMaterialDefault() =>
        Assert.Equal(MaterialCompiler.DefaultShadingShader, new MaterialDescriptor().Shading.ShaderName);
}
