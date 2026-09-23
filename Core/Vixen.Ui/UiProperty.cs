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

    /// <summary>
    ///     The properties a type declares, together with its bases' — complete for any type,
    ///     whether or not it declares one itself and whether or not anything has run its bases.
    /// </summary>
    /// <param name="ownerType">The type.</param>
    /// <returns>The keys, bases first.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every level's class constructor is forced, and the parameter's annotation is
    ///         what makes that legal</b> (#1336). The walk that did this before #1240 was one
    ///         <c>IL2072</c>, and the warning described something live: a NativeAOT publish answered
    ///         <c>Of(typeof(Derived))</c> with the derived property and nothing from any base,
    ///         because ILC preserves a constructor it can <em>name</em> and not one reached through
    ///         <c>BaseType</c> at run time. #1240 answered that by moving the forcing into generated
    ///         static constructors — each runs its nearest property-declaring ancestor's by
    ///         <c>typeof</c> — and narrowed this method to what the chain reaches, which is nothing
    ///         for a leaf that declares no property of its own and so gets no generated file.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Refuted: <c>Type.BaseType</c> <em>can</em> name it, given
    ///         <see cref="DynamicallyAccessedMemberTypes.All" />.</b> That is the one annotation the
    ///         trimmer propagates across <c>BaseType</c> in full — the rest propagate only their
    ///         public halves, because a non-public member of a base is not inherited and a class
    ///         constructor is non-public, which is exactly why
    ///         <c>NonPublicConstructors</c> could not carry the walk and <c>All</c> can. So the
    ///         requirement moved from the callee to the caller: whoever names the leaf keeps the
    ///         chain, and <c>typeof(Leaf)</c> satisfies it statically.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>CheckAot</c> is the witness, and the Roslyn trim analyzer is not</b> — which
    ///         matters, because the obvious way to check this annotation cannot see it. Measured on
    ///         2026-09-23, an ILC publish of a probe rooting <c>Vixen.Ui</c>: with
    ///         <c>NonPublicConstructors</c> here the recursive
    ///         <c>RunClassConstructor(type.BaseType.TypeHandle)</c> below is <c>IL2059</c> and
    ///         <c>IL2072</c> ("the return value of method <c>System.Type.BaseType.get</c> does not
    ///         have matching annotations"), and with <c>All</c> the same publish reports nothing.
    ///         <c>dotnet build Core/Vixen.Ui</c> reports <b>0 warnings either way</b>, and with the
    ///         attribute deleted outright as well — and it is not asleep: a
    ///         <c>type.GetMethods()</c> added to the unannotated parameter is <c>IL2070</c> on the
    ///         spot. So the analyzer that runs on an ordinary build does not model this flow and
    ///         ILC does, and <c>./build.sh CheckAot</c> is the only thing here that can go red on an
    ///         edit to these three attributes. Run on the merged tree the same day: succeeded in
    ///         1 m 31 s, 44.6 MB native binary, no findings.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The price is paid by a trimmed application and not by this method</b>: a call
    ///         with <c>typeof(MyPanel)</c> now roots every member of <c>MyPanel</c> and of every
    ///         base up to <see cref="UiElement" />. That is the honest cost of a complete answer
    ///         from a reflective API, and it is charged only where such a call exists — every
    ///         binding in the tree goes through <see cref="TryFindFor" /> on an element that exists,
    ///         which forces nothing because constructing an element already runs every base's
    ///         constructor. ⚠ Since #1359 the requirement is a line of its own in
    ///         <c>PublicAPI.Unshipped.txt</c>, so widening or narrowing it is a reviewed diff; before
    ///         that, the move from <c>NonPublicConstructors</c> to <c>All</c> passed
    ///         <c>CheckApi</c> unseen. Whether a public reflective entry point should carry
    ///         <c>All</c> at all — the only callers are tests — is #1359's open decision.
    ///     </para>
    ///     <para>
    ///         The generated chain stays as it is. It is redundant for this method on both runtimes
    ///         now — the executed publish below answers completely without it for a leaf that has
    ///         one and for a leaf that does not — and it stays because deleting it is a decision
    ///         rather than a patch.
    ///         <c>UiPropertyTests.An_untouched_base_is_registered_by_its_leaf_s_generated_constructor</c>
    ///         reads the table without forcing anything, so the chain keeps a test that can see it
    ///         disappear.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<UiPropertyKey> Of(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type ownerType
    ) {
        ArgumentNullException.ThrowIfNull(ownerType);

        RuntimeHelpers.RunClassConstructor(ownerType.TypeHandle);

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
    /// <remarks>
    ///     This is <see cref="Of" /> read for one name, and it carries <see cref="Of" />'s contract
    ///     with it — complete for an <paramref name="ownerType" /> that declares nothing of its own,
    ///     cold, and asked about by <c>typeof</c> (#1336) — and its annotation too, which is what
    ///     lets the walk force each base. <see cref="TryFindFor" /> takes an element instead and
    ///     needs neither.
    /// </remarks>
    public static bool TryFind(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type ownerType,
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

    /// <summary>The properties one type has registered, forcing nothing and walking nowhere.</summary>
    /// <param name="type">The type, asked about exactly.</param>
    /// <returns>Its own keys, empty while its class constructor has not run.</returns>
    /// <remarks>
    ///     ⚠ <b>The only read here that can observe a registration <i>not</i> happening</b>, which
    ///     is why it exists: <see cref="Of" /> forces every level, so it answers the same whether
    ///     the generated chain runs or not, and a test written against it could no longer go red on
    ///     a generator that stopped emitting the chain — a predicate that cannot be false. This one
    ///     asks the table what it holds at this instant and is internal because that is a question
    ///     only a test has any business asking.
    /// </remarks>
    internal static IReadOnlyList<UiPropertyKey> DeclaredBy(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        if (!Declared.TryGetValue(type, out var keys)) {
            return [];
        }

        lock (keys) {
            return keys.ToArray();
        }
    }

    /// <summary>Fills <paramref name="into" /> with a type's properties and its bases', bases first.</summary>
    /// <param name="type">The type.</param>
    /// <param name="into">Where the keys go.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The forcing on the way down is back, and the <c>IL2072</c> it used to raise is
    ///         not</b> (#1336, undoing #1240's half of the trade). The warning was a true positive:
    ///         measured on 2026-09-10 with a NativeAOT publish of a probe declaring
    ///         <c>Base : UiElement</c> and <c>Derived : Base</c>, <c>Of(typeof(Derived))</c> came
    ///         back with the derived property and <b>nothing else</b>, where CoreCLR returned all
    ///         ten. What was wrong was the conclusion drawn from it — that a base reached through
    ///         <c>BaseType</c> can never be named. <see cref="DynamicallyAccessedMemberTypes.All" />
    ///         on <paramref name="type" /> names it: the trimmer propagates <c>All</c>, and only
    ///         <c>All</c>, across <c>BaseType</c> intact, so the recursive call below inherits the
    ///         requirement from its caller instead of asking for one <c>Type.BaseType</c> cannot
    ///         supply.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured on a publish that was executed, not argued from the propagation rule</b>
    ///         (2026-09-23, win-x64, ILC 10.0.11, a probe rooting <c>Vixen.Ui</c> and declaring
    ///         <c>ProbeBase : UiElement</c> with a property and <c>ProbeLeaf : ProbeBase</c> with
    ///         none — the exact shape #1240 got wrong — in its own <b>un-rooted</b> assembly, so
    ///         nothing but this walk can reach <c>ProbeBase</c>'s class constructor).
    ///         <c>Of(typeof(ProbeLeaf))</c> answered <c>[AllowDrop, …, ProbeWeight]</c> from the
    ///         native binary: the base's property is there, and so are
    ///         <see cref="UiElement" />'s from two links up and another assembly. Reverting this one
    ///         attribute to <c>NonPublicConstructors</c> and republishing the same probe drops
    ///         <c>ProbeWeight</c> from the same binary's answer. So the annotation is load-bearing at
    ///         run time and not merely warning-silencing, and #1240's finding reproduces on demand.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The claim that nothing here publishes ahead of time is false and was the reason
    ///         this was left unproved.</b> <c>./build.sh CheckAot</c> publishes
    ///         <c>Tools/Vixen.AotProbe</c> with <c>ILLinkTreatWarningsAsErrors</c> and
    ///         <c>Vixen.Ui</c> among its <c>TrimmerRootAssembly</c> entries, and <c>ci.yml</c> runs
    ///         it per platform. #1255 is about not <em>executing</em> the binary it produces, which
    ///         is a narrower thing. ⚠ <c>CheckAot</c> is not in the default <c>./build.sh</c> chain,
    ///         so an edit here that passes <c>Test</c> and <c>CheckFormat</c> is still owed that
    ///         target before it is believed.
    ///     </para>
    /// </remarks>
    static void Collect(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type,
        List<UiPropertyKey> into
    ) {
        if (type.BaseType is { } baseType) {
            RuntimeHelpers.RunClassConstructor(baseType.TypeHandle);
            Collect(baseType, into);
        }

        if (!Declared.TryGetValue(type, out var keys)) {
            return;
        }

        lock (keys) {
            into.AddRange(keys);
        }
    }
}
