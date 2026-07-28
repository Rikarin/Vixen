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
        registry.ForType<AnimationCurve>(new CurveDrawer());
        registry.ForType<AssetId>(new AssetDrawer());

        registry.ForAttribute<ColorUsageAttribute>(new ColorDrawer());
        registry.ForAttribute<CurveAttribute>(new CurveDrawer());
        registry.ForAttribute<AssetPickerAttribute>(new AssetDrawer());
        registry.ForAttribute<MultilineAttribute>(new MultilineDrawer());

        // Last resort first: the enum drawer answers for a family of types that cannot be
        // enumerated, and the read-only one answers for everything, so it has to be behind it.
        registry.Fallback(new ReadOnlyDrawer());
        registry.Fallback(new EnumDrawer());

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
