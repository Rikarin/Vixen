// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Serialization;

namespace Vixen.Core.Reflection;

/// <summary>What an annotation says a type is for.</summary>
/// <remarks>
///     Flags rather than an enum, because one type is routinely several of these: a component that
///     is also a data contract and also appears in the inspector is the ordinary case, not the
///     exception.
/// </remarks>
[Flags]
public enum TypeTraits {
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Carries <c>[DataContract]</c>: it can be serialised.</summary>
    DataContract = 1 << 0,

    /// <summary>Carries <c>[Component]</c>: the ECS can attach it to an entity.</summary>
    Component = 1 << 1,

    /// <summary>Carries <c>[EditorVisible]</c>: the inspector shows it.</summary>
    EditorVisible = 1 << 2,

    /// <summary>An abstract type or an interface — a base to look up, never a thing to construct.</summary>
    Abstract = 1 << 3
}

/// <summary>How a member should be presented, and what it will accept.</summary>
/// <param name="Category">Which group the inspector puts it in.</param>
/// <param name="DisplayName">What the inspector calls it, or <see langword="null" /> for its name.</param>
/// <param name="Tooltip">What the inspector says about it.</param>
/// <param name="Minimum">The low end of its range, if it has one.</param>
/// <param name="Maximum">The high end of its range, if it has one.</param>
/// <param name="Step">How far one nudge moves it.</param>
/// <param name="Logarithmic">Whether the slider should be logarithmic.</param>
/// <param name="IsEditorVisible">Whether the inspector shows it at all.</param>
/// <param name="IsEditorReadOnly">Whether the inspector shows it without letting it be changed.</param>
public readonly record struct MemberPresentation(
    string? Category = null,
    string? DisplayName = null,
    string? Tooltip = null,
    double? Minimum = null,
    double? Maximum = null,
    double Step = 0,
    bool Logarithmic = false,
    bool IsEditorVisible = true,
    bool IsEditorReadOnly = false
);

/// <summary>One member of a described type, and the two delegates that read and write it.</summary>
/// <remarks>
///     <para>
///         The accessors are generated lambdas — <c>static instance =&gt; ((Foo)instance).X</c> —
///         not <see cref="System.Reflection.PropertyInfo" /> calls. That is the entire point of this
///         assembly: an inspector, a serializer fallback and a diff tool can all read and write
///         arbitrary members on a platform where <c>System.Reflection</c>'s member access has been
///         trimmed away.
///     </para>
///     <para>
///         They pass values as <see cref="object" />, which boxes a struct. That is deliberate and
///         it is why this is not a frame-loop API: it exists for tooling, inspection and boot-time
///         discovery, where one allocation per property read is invisible. Frame code uses the
///         generated serializer or touches the field directly.
///     </para>
/// </remarks>
public sealed class MemberDescriptor {
    readonly Func<object, object?>? getter;
    readonly Action<object, object?>? setter;

    /// <summary>The member's name in source.</summary>
    public string Name { get; }

    /// <summary>What it holds.</summary>
    public Type MemberType { get; }

    /// <summary>Where it sits in the type's declared order.</summary>
    public int Order { get; }

    /// <summary>How it should be presented.</summary>
    public MemberPresentation Presentation { get; }

    /// <summary>Whether it can be written at all.</summary>
    public bool CanWrite => setter is not null;

    /// <summary>Describes a member.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="memberType">What it holds.</param>
    /// <param name="order">Where it sits.</param>
    /// <param name="getter">Reads it, or <see langword="null" /> if it cannot be read.</param>
    /// <param name="setter">Writes it, or <see langword="null" /> if it cannot be written.</param>
    /// <param name="presentation">How it should be presented.</param>
    public MemberDescriptor(
        string name,
        Type memberType,
        int order,
        Func<object, object?>? getter,
        Action<object, object?>? setter,
        MemberPresentation presentation = default
    ) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(memberType);
        Name = name;
        MemberType = memberType;
        Order = order;
        this.getter = getter;
        this.setter = setter;
        Presentation = presentation;
    }

    /// <summary>Reads the member.</summary>
    /// <param name="instance">What to read it from.</param>
    /// <returns>Its value.</returns>
    /// <exception cref="InvalidOperationException">The member cannot be read.</exception>
    public object? GetValue(object instance) {
        ArgumentNullException.ThrowIfNull(instance);

        return getter is null
            ? throw new InvalidOperationException($"'{Name}' cannot be read.")
            : getter(instance);
    }

    /// <summary>Writes the member.</summary>
    /// <param name="instance">What to write it on.</param>
    /// <param name="value">What to write.</param>
    /// <exception cref="InvalidOperationException">The member cannot be written.</exception>
    public void SetValue(object instance, object? value) {
        ArgumentNullException.ThrowIfNull(instance);

        if (setter is null) {
            throw new InvalidOperationException($"'{Name}' cannot be written.");
        }

        setter(instance, value);
    }

    /// <summary>Renders the member as <c>Name : Type</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"{Name} : {MemberType.Name}";
}

/// <summary>Everything the engine knows about one annotated type, decided at compile time.</summary>
/// <remarks>
///     <para>
///         This is the replacement for <c>Type</c>-driven discovery. Stride injects module
///         initialisers with Cecil to build the equivalent; ADR-002 forbids that, and
///         <c>[ModuleInitializer]</c> does the same job in the language. What is registered here is
///         what a generator saw in the source, so it survives trimming, survives NativeAOT, and is a
///         build error rather than a start-up surprise when it is wrong.
///     </para>
///     <para>
///         Reading a member is <see cref="MemberDescriptor.GetValue" />; writing one is
///         <see cref="MemberDescriptor.SetValue" />; making one is <see cref="Create" />. None of
///         those touches <c>System.Reflection</c>.
///     </para>
/// </remarks>
public sealed class TypeDescriptor {
    readonly Func<object>? factory;

    /// <summary>The CLR type.</summary>
    public Type Type { get; }

    /// <summary>The name it is known by in data and in tools.</summary>
    public string Alias { get; }

    /// <summary>Names it used to be known by.</summary>
    public IReadOnlyList<string> FormerAliases { get; }

    /// <summary>What the annotations say it is for.</summary>
    public TypeTraits Traits { get; }

    /// <summary>Which group the inspector puts it in.</summary>
    public string? Category { get; }

    /// <summary>Its members, in declared order.</summary>
    public IReadOnlyList<MemberDescriptor> Members { get; }

    /// <summary>Whether an instance can be made without arguments.</summary>
    public bool CanCreate => factory is not null;

    /// <summary>Its serializer, if it has one.</summary>
    /// <remarks>
    ///     Resolved on demand rather than stored, because the two module initializers that produce
    ///     descriptors and serializers run in an order nobody chose. Looking it up at the moment of
    ///     asking removes the question.
    /// </remarks>
    public DataSerializer? Serializer =>
        SerializerRegistry.TryGetByAlias(Alias, out var serializer) && serializer.SerializedType == Type
            ? serializer
            : null;

    /// <summary>Describes a type.</summary>
    /// <param name="type">The CLR type.</param>
    /// <param name="alias">Its name in data and tools.</param>
    /// <param name="traits">What the annotations say it is for.</param>
    /// <param name="members">Its members, in declared order.</param>
    /// <param name="factory">Makes one, or <see langword="null" /> if it cannot be made.</param>
    /// <param name="category">Which group the inspector puts it in.</param>
    /// <param name="formerAliases">Names it used to be known by.</param>
    public TypeDescriptor(
        Type type,
        string alias,
        TypeTraits traits,
        MemberDescriptor[] members,
        Func<object>? factory = null,
        string? category = null,
        string[]? formerAliases = null
    ) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(alias);
        ArgumentNullException.ThrowIfNull(members);
        Type = type;
        Alias = alias;
        Traits = traits;
        Members = members;
        this.factory = factory;
        Category = category;
        FormerAliases = formerAliases ?? [];
    }

    /// <summary>Makes an instance.</summary>
    /// <returns>The instance.</returns>
    /// <exception cref="InvalidOperationException">The type has no accessible parameterless constructor.</exception>
    public object Create() =>
        factory is null
            ? throw new InvalidOperationException(
                $"'{Type}' cannot be created: it is abstract, or has no accessible parameterless constructor."
            )
            : factory();

    /// <summary>Finds a member by name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The member, or <see langword="null" />.</returns>
    public MemberDescriptor? FindMember(string name) {
        foreach (var member in Members) {
            if (string.Equals(member.Name, name, StringComparison.Ordinal)) {
                return member;
            }
        }

        return null;
    }

    /// <summary>Whether the type carries every one of <paramref name="traits" />.</summary>
    /// <param name="traits">What to look for.</param>
    /// <returns><see langword="true" /> if it carries them all.</returns>
    public bool Has(TypeTraits traits) => (Traits & traits) == traits;

    /// <summary>Renders the descriptor as its alias.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Alias;
}
