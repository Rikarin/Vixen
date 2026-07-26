// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     The annotations carry no behaviour, so what is worth testing is their shape: the generators
///     that read them assume particular targets and multiplicity, and a change to either would
///     break code that is emitted rather than written.
/// </summary>
public class AnnotationTests {
    [DataContract("Renamed", SerializedVersion = 2)]
    [DataAlias("OldName")]
    [DataAlias("OlderName")]
    sealed class Annotated {
        [DataMember(3, Name = "hp")]
        [Category("Stats")]
        [Range(0, 100, Step = 5)]
        [Tooltip("Hit points")]
        public int Health = 100;

        [DataMemberIgnore]
        [EditorVisible(false)]
        public int Cache = -1;
    }

    static AttributeUsageAttribute UsageOf<T>() where T : Attribute =>
        typeof(T).GetCustomAttribute<AttributeUsageAttribute>()!;

    [Fact]
    public void A_contract_carries_its_alias_and_schema_version() {
        var contract = typeof(Annotated).GetCustomAttribute<DataContractAttribute>()!;

        Assert.Equal("Renamed", contract.Alias);
        Assert.Equal(2, contract.SerializedVersion);
        Assert.False(contract.Inherited);
    }

    [Fact]
    public void A_type_can_carry_a_chain_of_former_names() {
        var aliases = typeof(Annotated).GetCustomAttributes<DataAliasAttribute>()
            .Select(static alias => alias.Name)
            .OrderBy(static name => name, StringComparer.Ordinal);

        Assert.Equal(new[] { "OldName", "OlderName" }, aliases);
        Assert.True(UsageOf<DataAliasAttribute>().AllowMultiple);
    }

    [Fact]
    public void A_member_carries_its_order_name_and_editor_metadata() {
        var member = typeof(Annotated).GetField(nameof(Annotated.Health))!;

        var data = member.GetCustomAttribute<DataMemberAttribute>()!;
        Assert.Equal(3, data.Order);
        Assert.Equal("hp", data.Name);

        Assert.Equal("Stats", member.GetCustomAttribute<CategoryAttribute>()!.Name);
        Assert.Equal("Hit points", member.GetCustomAttribute<TooltipAttribute>()!.Text);

        var range = member.GetCustomAttribute<RangeAttribute>()!;
        Assert.Equal(0, range.Minimum);
        Assert.Equal(100, range.Maximum);
        Assert.Equal(5, range.Step);
        Assert.False(range.Logarithmic);
    }

    [Fact]
    public void Ignoring_a_member_and_hiding_it_are_separate_decisions() {
        var member = typeof(Annotated).GetField(nameof(Annotated.Cache))!;

        Assert.NotNull(member.GetCustomAttribute<DataMemberIgnoreAttribute>());
        Assert.False(member.GetCustomAttribute<EditorVisibleAttribute>()!.Visible);
    }

    [Fact]
    public void Serialisation_annotations_apply_where_the_generators_expect_them() {
        Assert.Equal(
            AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum,
            UsageOf<DataContractAttribute>().ValidOn
        );

        Assert.Equal(AttributeTargets.Field | AttributeTargets.Property, UsageOf<DataMemberAttribute>().ValidOn);
        Assert.Equal(AttributeTargets.Field | AttributeTargets.Property, UsageOf<DataMemberIgnoreAttribute>().ValidOn);
        Assert.Equal(AttributeTargets.Struct | AttributeTargets.Class, UsageOf<ComponentAttribute>().ValidOn);
    }

    [Fact]
    public void No_annotation_allows_repetition_except_the_rename_chain() {
        Assert.False(UsageOf<DataContractAttribute>().AllowMultiple);
        Assert.False(UsageOf<DataMemberAttribute>().AllowMultiple);
        Assert.False(UsageOf<CategoryAttribute>().AllowMultiple);
        Assert.False(UsageOf<RangeAttribute>().AllowMultiple);
        Assert.False(UsageOf<TooltipAttribute>().AllowMultiple);
        Assert.True(UsageOf<DataAliasAttribute>().AllowMultiple);
    }
}
