// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>One "label: value" row in a derived-facts readout.</summary>
/// <remarks>
///     <para>
///         The part is <c>FactRow.vxml</c>; this file is the accessibility modifier, on the same
///         arrangement <c>GpuTimelineView</c>, <c>MemoryView</c> and <c>StatisticsView</c> use. The
///         emitter's partial carries no modifier, so a type another assembly holds needs a
///         declaration that says <c>public</c> — and every caller of this one is in another
///         assembly, which is the whole point of it being here.
///     </para>
///     <para>
///         ⚠ <b>The shape is doc 20's <c>(derived)</c> convention and not a new one.</b> A row, a
///         name, a value, styled by <c>fact-row</c>/<c>fact-name</c>/<c>fact-value</c> — which is
///         what <c>EditorWorlds</c>, <c>TerrainModule</c>, <c>WaterModule</c>, <c>FontView</c>,
///         <c>TextureImportView</c> and <c>CompiledSceneView</c> each wrote out for themselves.
///         Three of those six are in assemblies that can see this one and now do; the other three
///         are under <c>Vixen.Editor.AssetEditors</c>, which does not reference
///         <c>Vixen.Editor.Ui</c> and cannot without a new edge in the graph.
///     </para>
///     <para>
///         ⚠ <b>And two of the six disagreed about where the value's text goes.</b>
///         <c>TextureImportView</c> and <c>CompiledSceneView</c> set <c>fact-value</c>'s own
///         <c>Text</c> where the other four add a <c>text</c> child; this reproduces the four,
///         because those are the callers it replaces. Reconciling the other two is a layout change
///         and not a tidy-up — a <c>text</c> child is a box and a parent's <c>Text</c> is not.
///     </para>
///     <para>
///         ⚠ <b>Both of those two are markup now (wave 5) and both still set the cell's own
///         <c>Text</c>, through <c>Vixen.Editor.AssetEditors</c>'s own <c>FactName</c>/<c>FactValue</c>
///         in <c>Captions.cs</c>.</b> The disagreement above therefore survives the ports on purpose,
///         and is now written down in two places rather than six. Whoever reconciles it moves pixels
///         in four panels and should say so in the commit.
///     </para>
/// </remarks>
public sealed partial class FactRow;
