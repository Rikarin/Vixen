// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Inspector;

/// <summary>Marks a static method as the inspector for a type.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D3.</b> The method is what <see cref="CustomInspector.Build" /> takes: it is
///         handed the panel's body and the <c>EditTarget</c>, and fills the first from the second.
///     </para>
///     <code language="csharp">
///         [CustomInspector(typeof(TerrainComponent))]
///         public static void Draw(UiElement body, EditTarget target) { … }
///     </code>
///     <para>
///         ⚠ <b>A static method rather than a class, unlike Unity's <c>[CustomEditor]</c>.</b> Unity's
///         attribute goes on an <c>Editor</c> subclass because the thing it overrides is a virtual
///         method on a base with state. Ours has no base and no state — a custom inspector *is* an
///         <c>Action&lt;UiElement, EditTarget&gt;</c>, and a class whose only job is to hold one
///         method would be ceremony that buys nothing. It also matches <c>[EditorMenu]</c>, which is
///         the other thing an author writes on a loose static method.
///     </para>
///     <para>
///         ⚠ <b>Read by a scan of the assembly that declared it, and only for a plugin or a project
///         script.</b> ADR-002 forbids assembly scanning as a way of building the editor — a scan
///         reads metadata a trimmed publish has deleted and makes start-up cost grow with what is
///         installed. Neither applies to one discrete assembly the editor has just loaded or just
///         compiled, and the plugin loader already enumerates a plugin's types to find its entry
///         point. In-tree code registers a <see cref="CustomInspector" /> directly.
///     </para>
/// </remarks>
/// <param name="target">The type this draws the inspector for.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CustomInspectorAttribute(Type target) : Attribute {
    /// <summary>The type it draws.</summary>
    public Type Target { get; } = target;

    /// <summary>Which of two inspectors for one type wins; the higher one.</summary>
    /// <inheritdoc cref="CustomInspector" path="/remarks" />
    public int Order { get; init; }
}

/// <summary>Marks a property drawer as the one for a member's type, or for an attribute on it.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D3.</b> The class implements <see cref="IPropertyDrawer" /> and needs a
///         parameterless constructor — it is made by the editor, once, and registered into the
///         <see cref="DrawerRegistry" /> the host published.
///     </para>
///     <code language="csharp">
///         [CustomDrawer(typeof(Curve))]
///         public sealed class CurveDrawer : IPropertyDrawer { … }
///     </code>
///     <para>
///         ⚠ <b>A type <i>or</i> an attribute, because <c>DrawerRegistry</c> resolves both and the
///         difference is not cosmetic.</b> By type is "every <c>Curve</c> is drawn like this"; by
///         attribute is "every member carrying <c>[ColorUsage]</c> is", which is how one drawer serves
///         a member's *intent* rather than its storage. <see cref="ForAttribute" /> says which of the
///         two <see cref="Target" /> means.
///     </para>
/// </remarks>
/// <param name="target">The member type, or the attribute type when <see cref="ForAttribute" /> is set.</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CustomDrawerAttribute(Type target) : Attribute {
    /// <summary>What it is registered against.</summary>
    public Type Target { get; } = target;

    /// <summary>Whether <see cref="Target" /> is an attribute to match rather than a member type.</summary>
    public bool ForAttribute { get; init; }
}
