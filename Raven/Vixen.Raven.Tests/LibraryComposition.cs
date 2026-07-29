// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Raven;

namespace Tests;

/// <summary>
///     A complete set of <c>compose</c> bindings for the shipped library.
/// </summary>
/// <remarks>
///     <para>
///         Every declared slot in a compilation has to be bound, whether or not anything reaches it
///         — <c>RVN2073</c>, and rightly so: a slot with no implementation is a shader that cannot be
///         emitted, and finding that out per entry point rather than per declaration would report it
///         against the wrong file. The library declares ten between the material chain and the two
///         slots on a pass, so a test that only cares about one of them still has to fill the rest.
///     </para>
///     <para>
///         Which is what this is for. The default is the material the engine's own default descriptor
///         produces — a metal-roughness surface, shaded the standard way, with the chain's spare slots
///         taking the feature that contributes nothing.
///     </para>
/// </remarks>
static class LibraryComposition {
    /// <summary>The chain's slots, in the order <c>CompositeSurface</c> calls them.</summary>
    public static readonly string[] ChainSlots = [
        "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth"
    ];

    /// <summary>The default composition, with <paramref name="overrides" /> applied over it.</summary>
    /// <remarks>
    ///     An override may be bare (<c>surface</c>) or qualified (<c>CompositeSurface.first</c>); a
    ///     qualified binding wins over a bare one, so overriding one chain slot leaves the others
    ///     filled.
    /// </remarks>
    public static ComposeBindings With(params (string Slot, string Shader)[] overrides) {
        List<KeyValuePair<string, string>> bindings = [
            new("surface", "MetalRoughnessSurface"),
            new("shading", "StandardShading")
        ];

        foreach (var slot in ChainSlots) {
            bindings.Add(new(slot, "IdentitySurface"));
        }

        // The blend's two layers, for the same reason: declared, so they must be bound even by a
        // compilation with no layered material in it.
        bindings.Add(new("under", "IdentitySurface"));
        bindings.Add(new("over", "IdentitySurface"));

        // And the distance field a traced pass reads, which the clipmap fills.
        bindings.Add(new("distanceField", "GlobalDistanceField"));

        // And the irradiance field an indirect pass reads, which the probe pool fills.
        bindings.Add(new("irradiance", "IrradianceFieldProbes"));

        foreach (var (slot, shader) in overrides) {
            bindings.Add(new(slot, shader));
        }

        return ComposeBindings.Create(bindings);
    }
}
