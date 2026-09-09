// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Reflection;
using Vixen.Core.Serialization;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Nothing in this assembly claims a serialisation alias that nothing registers.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1025">#1025</a>, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/989">#989</a> two assemblies along.</b>
///         <c>BlockoutRetopologySettings</c>, <c>BlockoutChartSettings</c> and
///         <c>BlockoutPackSettings</c> carried <c>[DataContract("…")]</c> and registered nothing:
///         <c>Vixen.Editor.Blockout.csproj</c> names <c>Vixen.Editor.Inspector.Generator</c> — with a
///         comment saying precisely why analyzers have to be named — and neither
///         <c>Vixen.Core.Reflection.Generator</c> nor <c>Vixen.Core.Serialization.Generator</c>. The
///         attribute compiles anyway, because <c>DataContractAttribute</c> lives in <c>Vixen.Core</c>
///         and arrives transitively while the generator that gives it meaning does not.
///     </para>
///     <para>
///         ⚠ <b>The attributes were the lie and they are gone, so this is a guard rather than a
///         report.</b> All three are the blockout settings panel's form model,
///         <c>InspectorDescriptorGenerator</c> keys on <c>[Inspector]</c>, and nothing in the tree
///         writes one of them to a file or reads one back.
///     </para>
///     <para>
///         ⚠ <b>Which is why the check is the implication and not "there are none".</b> A saved
///         retopology preset is exactly the thing that would want one, and the day somebody adds it
///         the answer is to name the generators rather than to delete the attribute again.
///     </para>
/// </remarks>
public class BlockoutContractTests {
    /// <summary>Every type this assembly declares, including the ones that are not public.</summary>
    static Type[] Declared() => typeof(BlockoutRetopologySettings).Assembly.GetTypes();

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
            $"Only {declared.Length} types were read out of Vixen.Editor.Blockout, and there were far "
            + "more than that when this was written. The walk is finding almost nothing, which is a "
            + "pass over no work rather than an assembly with nothing wrong in it."
        );

        Assert.False(TypeRegistry.TryGetByAlias("VixenEditorBlockoutNoSuchContract", out _));
        Assert.False(SerializerRegistry.TryGetByAlias("VixenEditorBlockoutNoSuchContract", out _));

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
            + "Vixen.Editor.Blockout.csproj names neither Vixen.Core.Reflection.Generator nor "
            + "Vixen.Core.Serialization.Generator, and analyzers do not flow through a ProjectReference, "
            + "so the attribute registers nothing: it cannot be serialised and it cannot be looked up "
            + "under the name it states. Name the generators in the csproj, or drop the attribute — "
            + "#1025."
        );
    }
}
