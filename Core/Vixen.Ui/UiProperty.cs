// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Vixen.Ui;

/// <summary>Marks a partial property as one the UI framework knows about by name.</summary>
/// <remarks>
///     <para>
///         A plain C# property is invisible to everything that has to find it at runtime: a
///         stylesheet naming it, an animation targeting it, a binding writing it, an inspector
///         listing it. This gives it an identity without giving up the typed accessor — the
///         generator emits both, so <c>element.Radius</c> is a field read and
///         <c>key.SetValue(element, 4f)</c> reaches the same field.
///     </para>
///     <para>
///         ⚠ <b>Storage is a field, not a sparse table</b>, which is the opposite of what WPF does
///         and deliberate. A dependency-property table pays a dictionary probe per read to save
///         memory on the hundreds of properties a WPF element declares and never sets; a Vixen
///         control declares perhaps a dozen, there are 10⁴ elements rather than 10⁶, and reads
///         happen every frame. The table would be the more famous design and the slower one.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UiPropertyAttribute : Attribute {
    /// <summary>Whether an element with no value of its own takes its nearest ancestor's.</summary>
    /// <remarks>
    ///     For the properties CSS inherits and for the same reason — a panel that dims its contents
    ///     should not have to name every one of them. Costs a walk up the tree on a read that finds
    ///     nothing locally, which is why it is off unless asked for.
    /// </remarks>
    public bool Inherits { get; set; }

    /// <summary>The value an element has before anything sets one.</summary>
    /// <remarks>
    ///     Any constant an attribute can carry. Anything else uses <c>default(T)</c>, because a
    ///     default that had to be computed would have to run somewhere and the only somewhere is a
    ///     static constructor whose ordering nobody wants to reason about.
    /// </remarks>
    public object? Default { get; set; }

    /// <summary>A method to call after the value changes. <c>void M(T oldValue, T newValue)</c>.</summary>
    public string? Changed { get; set; }

    /// <summary>A method to run a value through before storing it. <c>T M(T value)</c>.</summary>
    /// <remarks>
    ///     Coercion runs before the change test, so clamping a value to what it already was is not a
    ///     change and raises nothing.
    /// </remarks>
    public string? Coerce { get; set; }
}

/// <summary>One UI property, by name, with typed access that needs no reflection.</summary>
/// <remarks>
///     The accessors are lambdas over a cast rather than <c>PropertyInfo.GetValue</c> — the same
///     choice <c>Vixen.Core.Reflection</c>'s generator makes, and for the same reason: it is what
///     lets an inspector read and write arbitrary members after trimming and on iOS.
/// </remarks>
public sealed class UiPropertyKey {
    readonly Func<UiElement, object?> get;
    readonly Action<UiElement, object?> set;

    internal UiPropertyKey(
        string name,
        Type ownerType,
        Type valueType,
        bool inherits,
        Func<UiElement, object?> get,
        Action<UiElement, object?> set
    ) {
        Name = name;
        OwnerType = ownerType;
        ValueType = valueType;
        Inherits = inherits;

        this.get = get;
        this.set = set;
    }

    /// <summary>Its name, as a stylesheet or a binding would write it.</summary>
    public string Name { get; }

    /// <summary>The type that declares it.</summary>
    public Type OwnerType { get; }

    /// <summary>The type of its value.</summary>
    public Type ValueType { get; }

    /// <summary>Whether an element with no value of its own takes its nearest ancestor's.</summary>
    public bool Inherits { get; }

    /// <summary>Reads it from an element.</summary>
    /// <param name="element">The element. Must be of the owning type.</param>
    /// <returns>The value, boxed.</returns>
    public object? GetValue(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);
        return get(element);
    }

    /// <summary>Writes it to an element.</summary>
    /// <param name="element">The element. Must be of the owning type.</param>
    /// <param name="value">The value.</param>
    public void SetValue(UiElement element, object? value) {
        ArgumentNullException.ThrowIfNull(element);
        set(element, value);
    }

    /// <inheritdoc />
    public override string ToString() => $"{OwnerType.Name}.{Name}";
}

/// <summary>Every declared UI property, findable by owner and name.</summary>
/// <remarks>
///     <para>
///         Filled by generated static initialisers, which run the first time anything touches the
///         declaring type — so a lookup forces that type's class constructor before answering.
///         Without it, asking for a property of a type nothing has instantiated yet would correctly
///         report that it does not exist, which is the sort of bug that only appears in the one
///         build where the order changed.
///     </para>
///     <para>
///         The forcing is <see cref="RuntimeHelpers.RunClassConstructor" /> rather than a member
///         scan, so nothing here reads metadata a trimmer may have removed.
///     </para>
/// </remarks>
public static class UiPropertyRegistry {
    static readonly ConcurrentDictionary<Type, List<UiPropertyKey>> Declared = new();

    /// <summary>Records a property. Called by generated code.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="ownerType">The type declaring it.</param>
    /// <param name="valueType">The type of its value.</param>
    /// <param name="inherits">Whether it inherits.</param>
    /// <param name="get">Reads it from an element of the owning type.</param>
    /// <param name="set">Writes it to one.</param>
    /// <returns>The key.</returns>
    public static UiPropertyKey Register(
        string name,
        Type ownerType,
        Type valueType,
        bool inherits,
        Func<UiElement, object?> get,
        Action<UiElement, object?> set
    ) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(ownerType);
        ArgumentNullException.ThrowIfNull(valueType);

        var key = new UiPropertyKey(name, ownerType, valueType, inherits, get, set);
        var keys = Declared.GetOrAdd(ownerType, static _ => []);

        lock (keys) {
            keys.Add(key);
        }

        return key;
    }

    /// <summary>The properties a type declares, including those it inherits from its bases.</summary>
    /// <param name="ownerType">The type.</param>
    /// <returns>The keys, bases first.</returns>
    public static IReadOnlyList<UiPropertyKey> Of(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type ownerType
    ) {
        ArgumentNullException.ThrowIfNull(ownerType);

        var result = new List<UiPropertyKey>();
        Collect(ownerType, result);
        return result;
    }

    /// <summary>Finds a property by name on an element that already exists.</summary>
    /// <param name="element">The element.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="key">Receives the key.</param>
    /// <returns>Whether it was found.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Constructing an element does <i>not</i> register its properties, which is the
    ///         opposite of what this used to assume.</b> The generator puts each key in a
    ///         <c>static readonly</c> field initialiser and adds no static constructor, so the class
    ///         is <c>beforefieldinit</c> — and the CLR is then free to defer the initialiser until
    ///         something reads a static field of that exact type. Making an instance is not that. So
    ///         a freshly built <c>&lt;Slider /&gt;</c> had no <c>Value</c> at all until some unrelated
    ///         code happened to touch <c>Slider.ValueProperty</c> first, and
    ///         <see cref="Vixen.Ui.Composition.BuildContext.TwoWay{T}" /> threw "'slider' has no
    ///         property called 'Value'" — a <c>bind:</c> that worked or did not depending on what the
    ///         rest of the application had already run.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Fixed in the generator, which now emits an empty static constructor, and not
    ///         here.</b> A class with one is no longer <c>beforefieldinit</c>, so the CLR must run
    ///         the initialisers before the first instance exists — which makes the premise below
    ///         true rather than merely assumed. The repair cannot be made on this side:
    ///         <see cref="Of" /> can call <see cref="RuntimeHelpers.RunClassConstructor" /> because
    ///         its parameter is an annotated <see cref="Type" />, and the type here comes from
    ///         <c>GetType()</c>, which the trimmer refuses to accept for that call (IL2059).
    ///     </para>
    ///     <para>
    ///         So this walks <c>BaseType</c> and reads the table and touches no metadata a trimmer
    ///         could remove, and needs no <c>DynamicallyAccessedMembers</c> annotation.
    ///     </para>
    /// </remarks>
    public static bool TryFindFor(UiElement element, string name, [NotNullWhen(true)] out UiPropertyKey? key) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(name);

        for (var type = element.GetType(); type is not null; type = type.BaseType) {
            if (!Declared.TryGetValue(type, out var keys)) {
                continue;
            }

            lock (keys) {
                foreach (var candidate in keys) {
                    if (string.Equals(candidate.Name, name, StringComparison.Ordinal)) {
                        key = candidate;
                        return true;
                    }
                }
            }
        }

        key = null;
        return false;
    }

    /// <summary>Finds a property by name on a type or one of its bases.</summary>
    /// <param name="ownerType">The type.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="key">Receives the key.</param>
    /// <returns>Whether it was found.</returns>
    public static bool TryFind(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type ownerType,
        string name,
        [NotNullWhen(true)] out UiPropertyKey? key
    ) {
        ArgumentNullException.ThrowIfNull(ownerType);
        ArgumentNullException.ThrowIfNull(name);

        foreach (var candidate in Of(ownerType)) {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal)) {
                key = candidate;
                return true;
            }
        }

        key = null;
        return false;
    }

    static void Collect(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type type,
        List<UiPropertyKey> into
    ) {
        if (type.BaseType is { } baseType) {
            Collect(baseType, into);
        }

        RuntimeHelpers.RunClassConstructor(type.TypeHandle);

        if (!Declared.TryGetValue(type, out var keys)) {
            return;
        }

        lock (keys) {
            into.AddRange(keys);
        }
    }
}
