// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.HotReload;

namespace Vixen.Editor.AssetEditors.Frame;

/// <summary>Doc 39's editor surface: the knobs, the look, the two resolved stacks, and Explode.</summary>
/// <remarks>
///     <para>
///         <b>Four things that are each a view over something that already exists.</b> The knobs and
///         the look are inspectors over mirrors of the node's own members; the quality table is
///         <see cref="ResolvedQualityTable" /> walking the waterfall's own schema; the volume panel
///         is <see cref="ResolvedVolumes" /> reading the engine's fold. Nothing here computes a
///         rendering fact of its own, which is the property that keeps the panel from becoming a
///         second opinion about what the frame is.
///     </para>
///     <para>
///         ⚠ <b>Both stacks say where a number came from, and that is the whole reason they are
///         panels rather than readouts.</b> The quality waterfall folds per parameter across three
///         files and the volume fold across four layers, so every number on this screen has a
///         provenance that is not visible in the number. A table of decided values would answer
///         "what is it" while the question somebody opens this panel with is always "why is it
///         that", and sending them to the wrong file to change it is worse than showing nothing.
///     </para>
///     <para>
///         ⚠ <b>Every write re-expands and the facts move with it.</b> The document rebuilds the
///         expansion on each edit — see <see cref="StandardFrameDocument.Changed" /> — so turning
///         shadows off takes a stage and two targets out of the count under the form, and a
///         guardrail refusal arrives on the edit that caused it rather than at the next launch.
///     </para>
///     <para>
///         ⚠ <b>A view is rebuilt on every reopen, so nothing durable lives here.</b> The same rule
///         <c>CompositorView</c> states, and the same consequence: the subscription to the document
///         is dropped in <c>OnRemoved</c>, because a view still listening to a document it has left
///         writes into elements that are no longer in the tree.
///     </para>
///     <para>
///         The panel is <c>StandardFrameView.vxml</c>; this file is the accessibility modifier, the
///         two records its lists key on, and the four elements that exist only so that markup can
///         write an intrinsic tag's own <c>Text</c>.
///     </para>
/// </remarks>
public sealed partial class StandardFrameView;

/// <summary>One line of an <c>analysis-list</c>, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the order the panel says things.</param>
/// <param name="Class"><c>error</c>, <c>warning</c>, or empty for an ordinary statement.</param>
/// <param name="Stage">Which part of the panel is speaking.</param>
/// <param name="Message">What it said.</param>
/// <remarks>
///     ⚠ <b>Not <c>AnalysisNote</c>, and the difference is one field.</b> The shared record in
///     <c>Captions.cs</c> carries a slot, a stage and a message; both of this panel's lists also
///     carry a <i>severity</i>, which reaches the row as its <c>class</c> — and <c>class</c> is
///     deliberately not a parameter of <c>AnalysisRow</c>, so a caller that has one has to hold it
///     somewhere. Widening the shared record would give seven other callers a field they do not
///     use.
/// </remarks>
internal readonly record struct FrameNote(int Slot, string Class, string Stage, string Message);

/// <summary>One row of a provenance table — a group heading, or a name and a resolved value.</summary>
/// <param name="Slot">Where it is in the table, headings counted.</param>
/// <param name="Group">Whether it is a heading, which is a different tag rather than a class.</param>
/// <param name="Class"><c>overridden</c> for a value something above the engine table decided.</param>
/// <param name="Name">The group's name, or the knob's.</param>
/// <param name="Value">What it resolved to and where that came from. Empty on a heading.</param>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Group" /> is in the key, which is <c>QueryView</c>'s rule.</b> A heading is
///         <c>&lt;frame-group&gt;</c> and a row is <c>&lt;fact-row&gt;</c> — a <i>tag</i> chosen by the
///         data, which cannot be bound — so the choice is an <c>@if</c> inside the loop body, and an
///         <c>@if</c> inside a surviving region is not re-evaluated. It costs nothing here because a
///         row does not turn into a heading without everything else about it changing too.
///     </para>
///     <para>
///         ⚠ <b>The slot is load-bearing.</b> The volume fold can report the same parameter at the
///         same value from two layers while an author is moving a volume, and
///         <c>BuildContext.For</c> cannot reconcile two equal keys in one loop.
///     </para>
/// </remarks>
internal readonly record struct FrameKnobRow(int Slot, bool Group, string Class, string Name, string Value);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     <para>
///         The panel ledger's shape 5 and its sanctioned escape; <c>FactName</c> in
///         <c>Captions.cs</c> carries the full argument, and this panel uses that one and
///         <c>FactValue</c> for its two cells.
///     </para>
///     <para>
///         ⚠ <b><c>world-title</c> and not <c>World-title</c>.</b> This panel was one of the four
///         that wrote the capital and were therefore never styled at all — <c>NameTable</c> interns
///         ordinally, so <c>WorldTheme.vcss</c>'s rule reached one call site out of five. That was
///         fixed in place before this port; the type here answers to the spelling the sheet means.
///     </para>
/// </remarks>
internal sealed class WorldTitle : UiElement {
    /// <inheritdoc />
    protected override string TagName => "world-title";
}

/// <inheritdoc cref="WorldTitle" />
internal sealed class FrameGroup : UiElement {
    /// <inheritdoc />
    protected override string TagName => "frame-group";
}

/// <summary>A bare sentence where a table would be, under the tag an interpolation would have made.</summary>
/// <remarks>
///     ⚠ <b>A type rather than an interpolation, because the target is the element's <i>own</i>
///     text.</b> <c>Quality.Add("text").Text = …</c> puts the words on the <c>text</c> element;
///     writing <c>&lt;text&gt;@Message&lt;/text&gt;</c> would put a second <c>text</c> inside the
///     first. Shape 5 again, and the same four lines.
/// </remarks>
internal sealed class TextLine : UiElement {
    /// <inheritdoc />
    protected override string TagName => "text";
}

/// <summary>Opens a frame document as knobs.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It claims <c>.vxcompositor</c>, which nothing claimed before it.</b>
///         <c>CompositorEditorFactory</c> claims <c>.vxcomp</c> — a node graph that <em>compiles
///         to</em> a frame — and the frame document itself opened in nothing at all: double-clicking
///         the file a project actually ships did nothing. So this is the editor for the format,
///         with the knobs as its main view and the resolved stacks shown for a hand-authored
///         document too.
///     </para>
///     <para>
///         The four last-mile services are properties rather than constructor arguments because the
///         registry is built before the modules are activated — see <c>AssetEditorsModule</c>, whose
///         whole job is exactly this kind of binding.
///     </para>
/// </remarks>
public sealed class StandardFrameEditorFactory : IAssetEditorFactory {
    /// <summary>What this editor is called, which is how the module finds it to bind it.</summary>
    public const string EditorName = "Frame";

    /// <inheritdoc />
    public string Name => EditorName;

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [StandardFrameDocument.Extension];

    /// <summary>Where the markup inspectors are registered, or null for the generated rows.</summary>
    public IEditorRegistry? Contributions { get; set; }

    /// <summary>The scene whose volumes the stack panel folds.</summary>
    public IActiveScene? Scene { get; set; }

    /// <summary>The view the editor is looking through.</summary>
    public IActiveView? Eye { get; set; }

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new StandardFrameDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // ⚠ This view owns the scroller and keeps its banner outside it, and it is also the view whose
        // remarks record what nesting one scroll region in another did to it: the inner content became
        // `align-self: flex-start` against its own width, and the fixed-width provenance column
        // resolved to nothing. A panel that scrolled would rebuild that arrangement from the outside.
        DockPanel.Fills(panel);

        var view = panel.Add<StandardFrameView>();

        view.Extensions = Contributions;
        view.Scene = Scene;
        view.Eye = Eye;

        view.Show((StandardFrameDocument) document);

        return view;
    }

    /// <summary>The two markup forms, contributed to a registry.</summary>
    /// <param name="registry">Where they go.</param>
    /// <param name="reload">The document's reload host, or null for an editor without one.</param>
    /// <returns>What removes them again.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry" /> is null.</exception>
    /// <remarks>
    ///     Doc 36 § P4's shape, unchanged from the terrain module's: a <c>.vxml</c> component
    ///     mounted through the reload host, so changing the markup changes the panel a second later
    ///     rather than at the next launch.
    /// </remarks>
    public static IDisposable[] Contribute(IEditorRegistry registry, HotReloadHost? reload) {
        ArgumentNullException.ThrowIfNull(registry);

        return [
            registry.Add(
                new CustomInspector(
                    typeof(StandardFrameSettings),
                    MarkupInspector.Of<StandardFrameInspector>(reload)
                )
            ),
            registry.Add(
                new CustomInspector(typeof(LookSettings), MarkupInspector.Of<LookInspector>(reload))
            )
        ];
    }
}
