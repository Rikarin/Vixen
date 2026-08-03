// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Reflection;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Assets.Textures;
using Xunit;

namespace Vixen.Editor.Scripts.Tests;

/// <summary>A reflected descriptor and a generated one, for the same type, compared.</summary>
/// <remarks>
///     <para>
///         <b>The thing standing between an editor script's importer and a content bug with no
///         error.</b> A settings type moved from a project's <c>Editor/</c> folder into a plugin
///         changes which describer produced its <c>TypeDescriptor</c> — and if the two disagree about
///         a name or an order, the same <c>.meta</c> reads back as a different set of values.
///     </para>
///     <para>
///         ⚠ <b>Against types that ship rather than a fixture.</b> These are real importer settings
///         with real annotations, described by <c>Vixen.Core.Reflection.Generator</c> at build time —
///         so the comparison is with what the generator actually does rather than with what this test
///         assumes it does.
///     </para>
/// </remarks>
public class ReflectedTypeTests {
    /// <remarks>
    ///     ⚠ <b>Loading an assembly does not run its module initializers</b>, and the generated
    ///     descriptors arrive through one. Nothing in this suite otherwise touches
    ///     <c>Vixen.Editor.Assets</c>'s module, so every comparison below would have been against an
    ///     empty registry — passing or failing for a reason that has nothing to do with the describer.
    ///     It is the same fact <c>AuthoringAssembly</c> exists for, met from the other side.
    /// </remarks>
    static ReflectedTypeTests() =>
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(
            typeof(RawImportSettings).Module.ModuleHandle
        );

    public static TheoryData<Type> Settings => new(
        typeof(RawImportSettings),
        typeof(TextureImportSettings),
        typeof(ModelImportSettings)
    );

    [Theory]
    [MemberData(nameof(Settings))]
    public void The_reflected_descriptor_says_what_the_generated_one_says(Type type) {
        Assert.True(TypeRegistry.TryGet(type, out var generated), $"{type.Name} has no generated descriptor");

        var reflected = ReflectedTypes.Describe(type);

        // The alias is the tag a .meta carries and the name an importer answers to. Nothing matters
        // more than this one.
        Assert.Equal(generated.Alias, reflected.Alias);

        // ⚠ Name *and* order, because YAML is written in member order and a reordering is a diff in
        // every sidecar in a project.
        Assert.Equal(
            generated.Members.Where(member => member.IsSerialized).Select(member => (member.Name, member.Order)),
            reflected.Members.Where(member => member.IsSerialized).Select(member => (member.Name, member.Order))
        );

        Assert.Equal(
            generated.Members.Select(member => member.MemberType),
            reflected.Members.Select(member => member.MemberType)
        );
    }

    /// <summary>
    ///     ⚠ <b>A member the generator excluded must be excluded here too.</b> <c>[DataMemberIgnore]</c>
    ///     is how a cache field or a façade stays out of a file, and a reflected describer that kept
    ///     one would write a key nothing reads back.
    /// </summary>
    [Theory]
    [MemberData(nameof(Settings))]
    public void The_two_agree_about_what_is_written_to_a_file(Type type) {
        Assert.True(TypeRegistry.TryGet(type, out var generated));

        var reflected = ReflectedTypes.Describe(type);

        Assert.Equal(
            generated.Members.Where(member => member.IsSerialized).Select(member => member.Name).Order(),
            reflected.Members.Where(member => member.IsSerialized).Select(member => member.Name).Order()
        );
    }

    /// <summary>
    ///     ⚠ <b>Reading and writing through the reflected descriptor has to reach the same storage the
    ///     generated one does.</b> Two descriptors agreeing about a member's name and disagreeing
    ///     about where its value lives is the worst of both.
    /// </summary>
    [Fact]
    public void A_value_written_through_one_is_read_by_the_other() {
        Assert.True(TypeRegistry.TryGet<RawImportSettings>(out var generated));

        var reflected = ReflectedTypes.Describe(typeof(RawImportSettings));
        var settings = generated.Create();

        reflected.FindMember("Version")!.SetValue(settings, 7);

        Assert.Equal(7, generated.FindMember("Version")!.GetValue(settings));
    }
}
