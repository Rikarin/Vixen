// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>Nothing in this assembly claims a serialisation alias that nothing registers.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/989">#989</a>.</b> Eight types here
///         carried <c>[DataContract("…")]</c> — the terrain, foliage, growth, grass, spline, brush,
///         layer and create settings — and the attribute registered nothing at all, because
///         <c>Vixen.Editor.Terrain.csproj</c> names neither <c>Vixen.Core.Reflection.Generator</c> nor
///         <c>Vixen.Core.Serialization.Generator</c>. The attribute compiles regardless:
///         <c>DataContractAttribute</c> lives in <c>Vixen.Core</c> and arrives transitively, while the
///         generator that gives it meaning does not flow through a <c>ProjectReference</c> at all —
///         which is the same shape as the missing <c>Vixen.Ui.Generators</c> reference this csproj's
///         own comment records as VX4003.
///     </para>
///     <para>
///         ⚠ <b>The attributes were the lie and they are gone, so this is a guard rather than a
///         report.</b> The eight are the terrain panel's form model: every row is an
///         <c>[Inspector]</c> member, <c>InspectorDescriptorGenerator</c> keys on <c>[Inspector]</c>
///         and not on <c>[DataContract]</c>, and nothing anywhere writes one of these to a file or
///         reads one back. What the attribute promised — polymorphic serialisation and a lookup by
///         the stated name — was a promise no caller had ever taken up.
///     </para>
///     <para>
///         ⚠ <b>Which is why the check is the implication and not "there are none".</b> A brush
///         preset or a project-level default is exactly the thing that would want one, and the day
///         somebody adds it the answer is to name the generator rather than to delete the attribute
///         again. This goes red on an attribute added without one and stays green on an attribute
///         added with one — and a defect invisible at compile time, at run time and in every other
///         test is the only kind worth a gate.
///     </para>
/// </remarks>
public class TerrainContractTests {
    /// <summary>Every type this assembly declares, including the ones that are not public.</summary>
    static Type[] Declared() => typeof(TerrainBrushSettings).Assembly.GetTypes();

    /// <summary>⚠ A <c>[DataContract]</c> here would have to be registered, and today there are none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both instruments first, because the interesting direction of this assertion is the
    ///         one a vacuous pass looks exactly like.</b> The walk is required to have found the
    ///         assembly's types — an empty <c>GetTypes</c> would satisfy the loop having checked
    ///         nothing — and the registry is required to be able to answer <em>false</em>, since a
    ///         lookup that said yes to everything would satisfy the loop having proved nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both registries, because <c>[DataContract]</c> feeds two generators and the csproj
    ///         names neither.</b> <c>TypeRegistry</c> is what a YAML tag resolves through and
    ///         <c>SerializerRegistry</c> is what the binary writer looks the alias up in; an assembly
    ///         with one and not the other would read back in one format and not the other, which is a
    ///         worse state than having neither.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_data_contract_here_registers_the_alias_it_claims() {
        var declared = Declared();

        Assert.True(
            declared.Length >= 30,
            $"Only {declared.Length} types were read out of Vixen.Editor.Terrain, and there were over "
            + "sixty when this was written. The walk is finding almost nothing, which is a pass over no "
            + "work rather than an assembly with nothing wrong in it."
        );

        // The registries can say no. A `TryGetByAlias` that answered yes to anything would make the
        // loop below green against an assembly registering nothing at all, which is #989 exactly.
        Assert.False(TypeRegistry.TryGetByAlias("VixenEditorTerrainNoSuchContract", out _));
        Assert.False(SerializerRegistry.TryGetByAlias("VixenEditorTerrainNoSuchContract", out _));

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
            + "Vixen.Editor.Terrain.csproj names neither Vixen.Core.Reflection.Generator nor "
            + "Vixen.Core.Serialization.Generator, and analyzers do not flow through a ProjectReference, "
            + "so the attribute registers nothing: it cannot be serialised and it cannot be looked up "
            + "under the name it states. Name the generators in the csproj, or drop the attribute — "
            + "#989."
        );
    }
}
