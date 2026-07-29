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
    [Fact]
    public void ADistanceFieldSlotIsFilledByAFieldRatherThanASurface() {
        var (_, _, filler) = Assert.Single(
            MaterialCompiler.OptionalSlots,
            entry => entry.Slot == "distanceField"
        );

        Assert.Equal(MaterialCompiler.EmptyFieldShader, filler);
        Assert.NotEqual(MaterialCompiler.IdentityShader, filler);
    }
}
