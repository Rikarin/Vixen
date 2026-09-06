// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Reflection.Tests;

/// <summary>
///     ⚠ In <see cref="TypeRegistryTestGroup" /> because this suite registers into a process-wide
///     static and reads <c>Count</c> either side of one registration.
/// </summary>
[Collection(TypeRegistryTestGroup.Name)]
public class TypeRegistryTests {
    [Fact]
    public void EveryAnnotatedTypeRegisteredItselfBeforeAnyCodeRan() {
        // The module initializer the generator emits, which is the entire point: no scan, no
        // AppDomain.GetAssemblies, nothing that stops working when the trimmer runs.
        Assert.True(TypeRegistry.TryGet<Health>(out _));
        Assert.True(TypeRegistry.TryGet<Tag>(out _));
        Assert.True(TypeRegistry.TryGet<Velocity>(out _));
        Assert.True(TypeRegistry.TryGet<Spinner>(out _));
    }

    [Fact]
    public void TraitsComeFromTheAnnotationsAndCombine() {
        Assert.True(TypeRegistry.TryGet<Transform2D>(out var both));
        Assert.True(both.Has(TypeTraits.DataContract));
        Assert.True(both.Has(TypeTraits.Component));
        Assert.True(both.Has(TypeTraits.EditorVisible));

        Assert.True(TypeRegistry.TryGet<Tag>(out var component));
        Assert.True(component.Has(TypeTraits.Component));
        Assert.False(component.Has(TypeTraits.DataContract));

        Assert.True(TypeRegistry.TryGet<Behaviour>(out var abstractBase));
        Assert.True(abstractBase.Has(TypeTraits.Abstract));
        Assert.False(abstractBase.CanCreate);
    }

    [Fact]
    public void QueryingByTraitIsWhatUsedToBeAnAssemblyScan() {
        var components = TypeRegistry.With(TypeTraits.Component).Select(descriptor => descriptor.Type).ToArray();

        Assert.Contains(typeof(Tag), components);
        Assert.Contains(typeof(Transform2D), components);
        Assert.Contains(typeof(Velocity), components);
        Assert.DoesNotContain(typeof(Health), components);
    }

    [Fact]
    public void QueryingByBaseTypeFindsTheSubtypes() {
        var behaviours = TypeRegistry.AssignableTo<Behaviour>().Select(descriptor => descriptor.Type).ToArray();

        Assert.Contains(typeof(Spinner), behaviours);
        Assert.Contains(typeof(Behaviour), behaviours);
        Assert.DoesNotContain(typeof(Tag), behaviours);
    }

    [Fact]
    public void ATypeIsFoundByItsAliasAndItsFormerOnes() {
        Assert.True(TypeRegistry.TryGetByAlias("Sprite", out var current));
        Assert.True(TypeRegistry.TryGetByAlias("Billboard", out var former));

        Assert.Equal(typeof(SpriteRenderer), current.Type);
        Assert.Same(current, former);
        Assert.Equal("Sprite", current.Alias);
    }

    [Fact]
    public void MembersAreReadAndWrittenWithoutSystemReflection() {
        Assert.True(TypeRegistry.TryGet<Health>(out var descriptor));
        var health = (Health)descriptor.Create();

        var current = descriptor.FindMember("Current")!;
        current.SetValue(health, 42f);

        Assert.Equal(42f, health.Current);
        Assert.Equal(42f, current.GetValue(health));
    }

    /// <summary>
    ///     A struct arrives boxed, and assigning through a cast modifies a copy and silently does
    ///     nothing. The setter has to reach into the box itself.
    /// </summary>
    [Fact]
    public void SettingAMemberOnABoxedStructReachesTheBox() {
        Assert.True(TypeRegistry.TryGet<Velocity>(out var descriptor));
        object boxed = new Velocity();

        descriptor.FindMember("DeltaX")!.SetValue(boxed, 3f);

        Assert.Equal(3f, ((Velocity)boxed).DeltaX);
    }

    /// <summary>
    ///     An <c>init</c> setter is still a setter; only the language refuses to call it outside an
    ///     object initializer. A deserializer reading a <c>.meta</c> file has no initializer to write
    ///     it in, and the settings records
    ///     [08](../../docs/plan/08-asset-pipeline-and-addressables.md) specifies are all this shape,
    ///     so the generator binds to it through <c>[UnsafeAccessor]</c>.
    /// </summary>
    [Fact]
    public void AnInitOnlyMemberIsWrittenThroughAnUnsafeAccessor() {
        Assert.True(TypeRegistry.TryGet<ImportSettings>(out var descriptor));
        var settings = new ImportSettings();

        var maxSize = descriptor.FindMember("MaxSize")!;
        Assert.True(maxSize.CanWrite);
        Assert.True(maxSize.IsInitOnly);
        Assert.Equal(2048, maxSize.GetValue(settings));

        maxSize.SetValue(settings, 512);
        descriptor.FindMember("Compression")!.SetValue(settings, "Astc6x6");

        Assert.Equal(512, settings.MaxSize);
        Assert.Equal("Astc6x6", settings.Compression);
    }

    /// <summary>And on a struct it has to land in the box, like every other setter here.</summary>
    [Fact]
    public void AnInitOnlyMemberOnABoxedStructReachesTheBox() {
        Assert.True(TypeRegistry.TryGet<Extent>(out var descriptor));
        object boxed = new Extent();

        descriptor.FindMember("Width")!.SetValue(boxed, 1920);
        descriptor.FindMember("Height")!.SetValue(boxed, 1080);

        Assert.Equal(1920, ((Extent)boxed).Width);
        Assert.Equal(1080, ((Extent)boxed).Height);
    }

    /// <summary>
    ///     Reaching <c>init</c> setters must not make everything settable: a get-only property has no
    ///     setter to bind to, and a plain <c>set</c> is not init-only.
    /// </summary>
    [Fact]
    public void InitOnlyIsRecordedSeparatelyFromWritableAndFromUnwritable() {
        Assert.True(TypeRegistry.TryGet<ImportSettings>(out var descriptor));

        var streaming = descriptor.FindMember("Streaming")!;
        Assert.True(streaming.CanWrite);
        Assert.False(streaming.IsInitOnly);

        var derived = descriptor.FindMember("IsHighResolution")!;
        Assert.False(derived.CanWrite);
        Assert.False(derived.IsInitOnly);
    }

    [Fact]
    public void AMemberWithNoSetterIsDescribedButRefusesToBeWritten() {
        Assert.True(TypeRegistry.TryGet<Health>(out var descriptor));
        var fraction = descriptor.FindMember("Fraction")!;

        Assert.False(fraction.CanWrite);
        Assert.Equal(0f, fraction.GetValue(new Health()));
        Assert.Throws<InvalidOperationException>(() => fraction.SetValue(new Health(), 1f));
    }

    [Fact]
    public void PresentationCarriesWhatTheInspectorNeeds() {
        Assert.True(TypeRegistry.TryGet<Health>(out var descriptor));
        var current = descriptor.FindMember("Current")!.Presentation;

        Assert.Equal("Vitals", current.Category);
        Assert.Equal("How much damage is left before death.", current.Tooltip);
        Assert.Equal(0d, current.Minimum);
        Assert.Equal(1000d, current.Maximum);
        Assert.Equal(5d, current.Step);
        Assert.False(current.Logarithmic);
        Assert.True(current.IsEditorVisible);
    }

    [Fact]
    public void EditorVisibilityAndSerialisationAreDifferentQuestions() {
        Assert.True(TypeRegistry.TryGet<Health>(out var descriptor));

        // Hidden from the inspector, but still a member and still serialised.
        var hidden = descriptor.FindMember("LastDamageTime")!;
        Assert.False(hidden.Presentation.IsEditorVisible);
        Assert.True(hidden.CanWrite);

        var readOnly = descriptor.FindMember("Regeneration")!;
        Assert.True(readOnly.Presentation.IsEditorVisible);
        Assert.True(readOnly.Presentation.IsEditorReadOnly);
        Assert.Equal("Regeneration", readOnly.Presentation.DisplayName);
        Assert.True(readOnly.CanWrite);
    }

    [Fact]
    public void ADerivedTypeDescribesItsBaseMembersFirst() {
        Assert.True(TypeRegistry.TryGet<Spinner>(out var descriptor));

        Assert.Equal(["Enabled", "Speed"], descriptor.Members.Select(member => member.Name));
    }

    [Fact]
    public void ATypeWithNoParameterlessConstructorSaysSoRatherThanFailingLater() {
        Assert.True(TypeRegistry.TryGet<Anchored>(out var descriptor));

        Assert.False(descriptor.CanCreate);
        var thrown = Assert.Throws<InvalidOperationException>(() => descriptor.Create());
        Assert.Contains("parameterless constructor", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACategoryOnTheTypeIsCarriedToo() {
        Assert.True(TypeRegistry.TryGet<Health>(out var descriptor));
        Assert.Equal("Gameplay", descriptor.Category);
    }

    /// <summary>
    ///     The two registries are filled by two module initializers in an order nobody chose, so the
    ///     descriptor resolves its serializer at the moment of asking rather than at registration.
    /// </summary>
    [Fact]
    public void ADescriptorFindsTheSerializerForItsOwnType() {
        Assert.True(TypeRegistry.TryGet<Health>(out var contract));
        Assert.NotNull(contract.Serializer);
        Assert.Equal(typeof(Health), contract.Serializer.SerializedType);

        // `Tag` is a component and not a contract, so nothing generated a serializer for it.
        Assert.True(TypeRegistry.TryGet<Tag>(out var component));
        Assert.Null(component.Serializer);
    }

    [Fact]
    public void TwoTypesClaimingOneNameIsAnErrorRatherThanLastOneWins() {
        var first = new TypeDescriptor(typeof(int), "Collision", TypeTraits.None, []);
        var second = new TypeDescriptor(typeof(long), "Collision", TypeTraits.None, []);

        TypeRegistry.Register(first);

        try {
            var thrown = Assert.Throws<InvalidOperationException>(() => TypeRegistry.Register(second));
            Assert.Contains("claim the name", thrown.Message, StringComparison.Ordinal);
        } finally {
            // Leave the shared registry as it was found; every other test reads it.
            TypeRegistry.Register(new(typeof(int), "System.Int32", TypeTraits.None, []));
        }
    }

    [Fact]
    public void RegisteringATypeTwiceReplacesRatherThanDuplicates() {
        Assert.True(TypeRegistry.TryGet<Health>(out var original));
        var before = TypeRegistry.Count;

        TypeRegistry.Register(original);

        Assert.Equal(before, TypeRegistry.Count);
    }
}
