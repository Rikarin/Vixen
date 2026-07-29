// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Inspector;

/// <summary>Which drawer edits which member.</summary>
/// <remarks>
///     <para>
///         <b>Attributes are asked before types.</b> A <c>float</c> is a numeric field and a
///         <c>float</c> under <c>[Range]</c> is a slider; a <c>Guid</c> is a text box and a
///         <c>Guid</c> under <c>[AssetPicker&lt;Texture&gt;]</c> is a picker. The attribute is the
///         more specific statement about the member, so it wins — and a member with several is
///         matched in the order they were declared, so the answer does not depend on a dictionary's
///         iteration order.
///     </para>
///     <para>
///         <b>Registration is per instance, and there is a shared default.</b> A plugin adding a
///         drawer for its own type adds it to <see cref="Default" />; a test wanting to prove a
///         drawer in isolation makes an empty registry. The alternative — a single static — makes two
///         tests that register drawers for one type unable to run in the same process.
///     </para>
/// </remarks>
public sealed class DrawerRegistry {
    readonly Dictionary<Type, List<IPropertyDrawer>> byAttribute = [];
    readonly Dictionary<Type, List<IPropertyDrawer>> byType = [];
    readonly List<IPropertyDrawer> fallbacks = [];

    /// <summary>The registry the inspector uses unless it is handed another.</summary>
    public static DrawerRegistry Default { get; } = CreateDefault();

    /// <summary>Every drawer registered, once each, in no particular order.</summary>
    /// <remarks>
    ///     ⚠ <b>For a host that has to configure a drawer it did not create.</b>
    ///     <see cref="AssetDrawer" /> needs a project to turn a GUID into a name and to open a
    ///     picker, and <see cref="CreateDefault" /> makes two of them — one for the type and one for
    ///     the attribute — before any project exists. The alternative is a registry that hands out
    ///     the instances it made, which is a second API saying the same thing, or an application that
    ///     builds its own default set, which is the same list maintained twice.
    ///     <para>
    ///         Distinct, because a drawer registered for both a type and an attribute is one drawer.
    ///         A caller that subscribed to its event per registration would get two picker dialogs
    ///         for one click.
    ///     </para>
    /// </remarks>
    public IEnumerable<IPropertyDrawer> Drawers =>
        byType.Values.Concat(byAttribute.Values).SelectMany(candidates => candidates).Concat(fallbacks).Distinct();

    /// <summary>Registers a drawer for a value type.</summary>
    /// <param name="type">The member type it edits.</param>
    /// <param name="drawer">The drawer.</param>
    /// <remarks>
    ///     The most recently registered wins, so a game overriding the built-in <c>Color4</c> drawer
    ///     registers its own and does not have to remove anything. What it replaces is still there
    ///     and is used the moment the newer one declines through <see cref="IPropertyDrawer.CanDraw" />.
    /// </remarks>
    public void ForType(Type type, IPropertyDrawer drawer) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(drawer);

        Insert(byType, type, drawer);
    }

    /// <summary>Registers a drawer for a value type.</summary>
    /// <typeparam name="T">The member type it edits.</typeparam>
    /// <param name="drawer">The drawer.</param>
    public void ForType<T>(IPropertyDrawer drawer) => ForType(typeof(T), drawer);

    /// <summary>Registers a drawer for members carrying an attribute.</summary>
    /// <param name="attribute">The attribute type.</param>
    /// <param name="drawer">The drawer.</param>
    public void ForAttribute(Type attribute, IPropertyDrawer drawer) {
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(drawer);

        Insert(byAttribute, attribute, drawer);
    }

    /// <summary>Registers a drawer for members carrying an attribute.</summary>
    /// <typeparam name="TAttribute">The attribute type.</typeparam>
    /// <param name="drawer">The drawer.</param>
    public void ForAttribute<TAttribute>(IPropertyDrawer drawer) where TAttribute : Attribute =>
        ForAttribute(typeof(TAttribute), drawer);

    /// <summary>Registers a drawer consulted when nothing more specific matched.</summary>
    /// <param name="drawer">The drawer.</param>
    /// <remarks>
    ///     What the enum drawer is registered as: it applies to a whole family of types that cannot
    ///     be enumerated, and asking it "is this an enum?" is cheaper than registering one entry per
    ///     enum in the process.
    /// </remarks>
    public void Fallback(IPropertyDrawer drawer) {
        ArgumentNullException.ThrowIfNull(drawer);

        fallbacks.Insert(0, drawer);
    }

    /// <summary>Takes a drawer out of everything it was registered for.</summary>
    /// <param name="drawer">The drawer.</param>
    /// <returns>Whether it was registered anywhere.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>By instance, and everywhere at once</b>, because one drawer is commonly registered
    ///         for a type and for an attribute and its owner should not have to remember which.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What unloading a plugin does, and it is not optional.</b> <see cref="Default" />
    ///         is a static, so a drawer left in it after its plugin was unloaded is a reference from
    ///         a live registry into an assembly the editor is trying to collect — and the symptom is
    ///         not a stale drawer, it is a load context that stays in memory for the session with
    ///         nothing reporting it. Registrations that go through <c>PluginContext</c> are undone
    ///         for the plugin; this one goes through <c>PluginContext.OnUnload</c>.
    ///     </para>
    /// </remarks>
    public bool Remove(IPropertyDrawer drawer) {
        ArgumentNullException.ThrowIfNull(drawer);

        var removed = fallbacks.Remove(drawer);

        foreach (var candidates in byType.Values) {
            removed |= candidates.Remove(drawer);
        }

        foreach (var candidates in byAttribute.Values) {
            removed |= candidates.Remove(drawer);
        }

        return removed;
    }

    /// <summary>The drawer that edits a member.</summary>
    /// <param name="member">The member.</param>
    /// <returns>The drawer, or <see langword="null" /> when nothing can edit it.</returns>
    /// <remarks>
    ///     A member nothing can edit is not an error and is not omitted: the caller draws it
    ///     read-only, because a member the inspector cannot edit is still a member somebody needs to
    ///     see the value of.
    /// </remarks>
    public IPropertyDrawer? Resolve(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        foreach (var attribute in member.Attributes) {
            if (byAttribute.TryGetValue(attribute, out var candidates) && Pick(candidates, member) is { } drawer) {
                return drawer;
            }
        }

        var type = Nullable.GetUnderlyingType(member.MemberType) ?? member.MemberType;

        if (byType.TryGetValue(type, out var byExactType) && Pick(byExactType, member) is { } exact) {
            return exact;
        }

        return Pick(fallbacks, member);
    }

    /// <summary>The built-in drawers, which is what <see cref="Default" /> starts as.</summary>
    /// <returns>The registry.</returns>
    public static DrawerRegistry CreateDefault() {
        var registry = new DrawerRegistry();

        registry.ForType<bool>(new BooleanDrawer());
        registry.ForType<string>(new StringDrawer());

        var number = new NumberDrawer();

        foreach (var type in NumberDrawer.SupportedTypes) {
            registry.ForType(type, number);
        }

        registry.ForType<Vector2>(new Vector2Drawer());
        registry.ForType<Vector3>(new Vector3Drawer());
        registry.ForType<Vector4>(new Vector4Drawer());
        registry.ForType<Quaternion>(new QuaternionDrawer());
        registry.ForType<Color4>(new ColorDrawer());

        // ⚠ Its own drawer, and without it every `Color3` in the editor was drawn by the read-only
        // last resort — a light's colour included, which is the property people open a light for.
        registry.ForType<Color3>(new Color3Drawer());
        registry.ForType<AnimationCurve>(new CurveDrawer());
        registry.ForType<AssetId>(new AssetDrawer());

        // ⚠ Both, and the order does not matter: `PropertyDrawer.CanDraw` answers for its own value
        // type, so the one that does not match the member declines and the registry moves on. A
        // `[ColorUsage]` on a `Color3` would otherwise land on the `Color4` drawer, be refused, and
        // fall all the way through to the read-only last resort — which is worse than having no
        // attribute at all, because the attribute is what somebody wrote to ask for a picker.
        registry.ForAttribute<ColorUsageAttribute>(new ColorDrawer());
        registry.ForAttribute<ColorUsageAttribute>(new Color3Drawer());
        registry.ForAttribute<CurveAttribute>(new CurveDrawer());
        registry.ForAttribute<AssetPickerAttribute>(new AssetDrawer());
        registry.ForAttribute<MultilineAttribute>(new MultilineDrawer());

        // Last resort first: the enum drawer answers for a family of types that cannot be
        // enumerated, and the read-only one answers for everything, so it has to be behind it.
        registry.Fallback(new ReadOnlyDrawer());
        registry.Fallback(new EnumDrawer());

        // ⚠ The two composites go in front of the enum drawer and behind nothing else, because both
        // answer for a family rather than for a type. The list one is in front of the nested one: a
        // described type that happens to implement `IList` is a list first, and drawing its own
        // members instead would show `Count` and `Capacity` where the elements belong.
        var nested = new NestedDrawer();
        var lists = new ListDrawer();

        registry.Fallback(nested);
        registry.Fallback(lists);

        // ⚠ Told which registry they are in rather than reading the static. A test with a registry
        // of its own, or a game that replaced the colour drawer, must see its own drawers inside a
        // nested object and inside a list as well as outside them — and reading `Default` here would
        // also be a cycle, since this is what builds it.
        nested.Drawers = registry;
        lists.Drawers = registry;

        return registry;
    }

    static void Insert(Dictionary<Type, List<IPropertyDrawer>> map, Type key, IPropertyDrawer drawer) {
        if (!map.TryGetValue(key, out var list)) {
            map[key] = list = [];
        }

        list.Insert(0, drawer);
    }

    static IPropertyDrawer? Pick(List<IPropertyDrawer> candidates, InspectorMember member) {
        foreach (var drawer in candidates) {
            if (drawer.CanDraw(member)) {
                return drawer;
            }
        }

        return null;
    }
}
