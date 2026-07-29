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
    [Fact]
    public void TheEngineBindsEverySlotTheLibraryDeclares() {
        var declared = Directory
            .EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Shaders"), "*.rvn", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("compose val ", StringComparison.Ordinal))
            .Select(line => line["compose val ".Length..].Split(':')[0].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(declared);

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
    ///     <b>The material path and the pass path fill the same slots with the same shaders.</b> They
    ///     are two lists because they answer different questions — one qualifies a slot by the shader
    ///     declaring it and the other does not — and two lists of the same fillers is exactly the
    ///     arrangement that drifts.
    /// </summary>
    [Fact]
    public void ThePassPathAndTheMaterialPathAgreeOnFillers() {
        foreach (var (slot, filler) in MaterialCompiler.PassSlots) {
            foreach (var entry in MaterialCompiler.OptionalSlots.Where(other => other.Slot == slot)) {
                Assert.Equal(filler, entry.Filler);
            }
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
    }
}
