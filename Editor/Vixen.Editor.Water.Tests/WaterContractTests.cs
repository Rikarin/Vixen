// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Xunit;

namespace Vixen.Editor.Water.Tests;

/// <summary>Nothing in this assembly claims a serialisation alias that nothing registers.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1025">#1025</a>, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/989">#989</a> two assemblies along.</b>
///         <c>WaterZoneSettings</c> and <c>WaterBodySettings</c> carried an unnamed
///         <c>[DataContract]</c> and registered nothing: <c>Vixen.Editor.Water.csproj</c> names three
///         analyzers — the inspector's and the two markup ones, each with a comment about why a
///         generator has to be named — and neither <c>Vixen.Core.Reflection.Generator</c> nor
///         <c>Vixen.Core.Serialization.Generator</c>. The attribute compiles anyway, because
///         <c>DataContractAttribute</c> lives in <c>Vixen.Core</c> and arrives transitively while the
///         generator that reads it does not.
///     </para>
///     <para>
///         ⚠ <b>The attributes were the lie and they are gone, so this is a guard rather than a
///         report.</b> Both are the zone and body panels' form model, drawn from <c>[Inspector]</c>.
///         What this toolset writes is a <c>.vxspline</c>, and <c>SplineAsset</c> is declared and
///         registered in <c>Core/</c> where the reference index and the YAML tag can both see it.
///     </para>
///     <para>
///         ⚠ <b>Which is why the check is the implication and not "there are none".</b> A water
///         preset saved beside a scene is exactly the thing that would want one, and the day somebody
///         adds it the answer is to name the generators rather than to delete the attribute again.
///     </para>
/// </remarks>
public class WaterContractTests {
    /// <summary>Every type this assembly declares, including the ones that are not public.</summary>
    static Type[] Declared() => typeof(WaterZoneSettings).Assembly.GetTypes();

    /// <summary>⚠ A <c>[DataContract]</c> here would have to be registered, and today there are none.</summary>
    /// <remarks>
    ///     <b>Both instruments first, because the interesting direction of this assertion is the one
    ///     a vacuous pass looks exactly like.</b> The walk is required to have found the assembly's
    ///     types — an empty <c>GetTypes</c> would satisfy the loop having checked nothing — and the
    ///     registries are required to be able to answer <em>false</em>, since a lookup that said yes
    ///     to everything would satisfy it having proved nothing. Both registries, because
    ///     <c>[DataContract]</c> feeds two generators and this csproj names neither: an assembly with
    ///     one and not the other reads back in one format and not in the other, which is worse than
    ///     having neither.
    /// </remarks>
    [Fact]
    public void Every_data_contract_here_registers_the_alias_it_claims() {
        var declared = Declared();

        Assert.True(
            declared.Length >= 20,
            $"Only {declared.Length} types were read out of Vixen.Editor.Water, and there were far more "
            + "than that when this was written. The walk is finding almost nothing, which is a pass over "
            + "no work rather than an assembly with nothing wrong in it."
        );

        // The registries can say no. A `TryGetByAlias` that answered yes to anything would make the
        // loop below green against an assembly registering nothing at all, which is #1025 exactly.
        Assert.False(TypeRegistry.TryGetByAlias("VixenEditorWaterNoSuchContract", out _));
        Assert.False(SerializerRegistry.TryGetByAlias("VixenEditorWaterNoSuchContract", out _));

        var unregistered = declared
            .Select(type => (Type: type,
                Contract: (DataContractAttribute?)Attribute.GetCustomAttribute(type, typeof(DataContractAttribute))))
            .Where(entry => entry.Contract is not null)
            .Select(entry => (entry.Type, Alias: entry.Contract!.Alias ?? entry.Type.Name))
            .Where(entry => !TypeRegistry.TryGetByAlias(entry.Alias, out _)
                || !SerializerRegistry.TryGetByAlias(entry.Alias, out _))
            .Select(entry => $"{entry.Type.Name} as '{entry.Alias}'")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unregistered.Length == 0,
            $"{string.Join(", ", unregistered)} — a [DataContract] whose alias nothing claims. "
            + "Vixen.Editor.Water.csproj names neither Vixen.Core.Reflection.Generator nor "
            + "Vixen.Core.Serialization.Generator, and analyzers do not flow through a ProjectReference, "
            + "so the attribute registers nothing: it cannot be serialised and it cannot be looked up "
            + "under the name it states. Name the generators in the csproj, or drop the attribute — "
            + "#1025."
        );
    }
}
