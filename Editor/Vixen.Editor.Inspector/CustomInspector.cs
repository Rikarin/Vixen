// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.Inspector;

/// <summary>A hand-written inspector for one type, instead of the rows the generator would produce.</summary>
/// <param name="Target">The type it draws. Exactly that type — a subclass gets the generated rows.</param>
/// <param name="Build">
///     Fills the panel's body from what is being edited. Called once per selection, and what it puts
///     there replaces the whole row list.
///     <para>
///         ⚠ <b>It fills a container rather than returning one, which is where this differs from
///         Unity's <c>CreateInspectorGUI</c>.</b> A <c>UiElement</c> in this framework is created by
///         its parent — <c>Document.Create(tag, parent, …)</c> — so there is no detached element to
///         hand back. Every other builder in the editor has the same shape; <c>PanelDescriptor</c>
///         takes an <c>Action&lt;DockPanel&gt;</c> for the same reason.
///     </para>
/// </param>
/// <param name="Order">
///     Which wins when two are registered for one type, high first. A plugin overriding a built-in
///     registers a higher one; ties go to whichever registered last, so a project's own beats a
///     package's by arriving later.
/// </param>
/// <remarks>
///     <para>
///         <b>F5: there was no path at all.</b> The descriptor generator is a <c>ProjectReference</c>
///         analyzer and there was no attribute-scanned runtime path either, so nothing outside this
///         repository could replace an inspector for its own type. This is the contribution that
///         answers it, through <see cref="EditorRegistry" />, so a plugin, a generated registration
///         and a project script are indistinguishable to the panel.
///     </para>
///     <para>
///         ⚠ <b>It is handed an <see cref="EditTarget" /> and not the objects.</b> That is what makes
///         a custom inspector as multi-object-aware, as undoable and as mixed-value-correct as the
///         generated one without writing any of it: <c>target.Find("Roughness")</c> is a binding over
///         however many objects are selected. A custom inspector that reached for
///         <c>target.Objects[0]</c> and assigned a field would lose all of it, which is precisely
///         Unity's warning about not going around <c>SerializedObject</c>.
///     </para>
///     <para>
///         <b>The generated rows are still reachable</b> — <c>InspectorRows.Add</c> builds one from a
///         field — so the ordinary shape of a custom inspector is "the usual rows, in my order, with
///         my two buttons between them" rather than a panel written from nothing.
///     </para>
/// </remarks>
public sealed record CustomInspector(Type Target, Action<UiElement, EditTarget> Build, int Order = 0);
