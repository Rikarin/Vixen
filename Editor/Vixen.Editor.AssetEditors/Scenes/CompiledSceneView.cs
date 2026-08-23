// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Scenes;

/// <summary>What a build makes of this scene: the blocks, the columns and the complaints.</summary>
/// <remarks>
///     <para>
///         The panel is <c>CompiledSceneView.vxml</c>; this file is the accessibility modifier, the two
///         records its lists key on, and the handful of elements that exist only so that markup can
///         write an intrinsic tag's own <c>Text</c>. Same arrangement as <c>AudioMixerView</c> and
///         <c>VariationHarnessView</c>.
///     </para>
///     <para>
///         <b>K1's last unopened door, and the question it answers is one nothing else could.</b> An
///         authored <c>.vxscene</c> nests its entities and spells its numbers out; a compiled
///         <c>SceneAsset</c> is flat, positional and archetype-major, and the two are shaped by
///         opposite forces. Everything an author can see today is the first of them. So "why does my
///         entity arrive in the player without its <c>Health</c>" has had no answer short of building
///         the project, shipping it, and noticing — which is the shape of defect this pane exists to
///         make visible at the moment somebody can still act on it.
///     </para>
///     <para>
///         ⚠ <b>It compiles the open document rather than reading the artefact the last import
///         wrote, and that is a choice with a cost.</b> What it shows is what this scene <i>would</i>
///         compile to, not what the build store currently holds — so it cannot show you a stale
///         artefact, and it cannot be wrong about an edit you have not saved. The alternative
///         answers a different and also useful question ("what is actually in the build") and needs
///         the artefact store, an import to have run, and a staleness story of its own. This is the
///         same trade the shader graph's *show generated code* makes, and for the same reason:
///         during authoring the actionable question is what the thing in front of you produces.
///     </para>
///     <para>
///         ⚠ <b>The diagnostics are the point, more than the tables are.</b>
///         <c>SceneCompiler.Compile</c> reports every problem and then fails once, so a hand-merged
///         scene with four duplicate ids says all four rather than making the author find them one
///         build at a time. A pane that showed only the happy result would throw that away.
///     </para>
///     <para>
///         <b>Compiled on demand and not on every keystroke.</b> A scene compile walks every entity
///         and serialises every component, which is the wrong thing to do behind a gizmo drag. The
///         button is the trigger, and the pane says when what it is showing was produced.
///     </para>
/// </remarks>
public sealed partial class CompiledSceneView;

/// <summary>One block's row, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the content's block list.</param>
/// <param name="Archetype">Which components it carries, or that it carries none.</param>
/// <param name="Count">How many entities are in it, in words.</param>
/// <param name="Bytes">And how many bytes its columns hold.</param>
/// <remarks>
///     ⚠ <b>The whole record is the key, which is the immutable-data half of the <c>@for</c> rule.</b>
///     Nothing in a block row is signal-backed, so there is no binding inside the body that would
///     notice a changed number — the value has to <i>be</i> the identity, so that recompiling into a
///     different byte count is a different key and the region is rebuilt.
///     <para>
///         ⚠ <b>And the slot is in it because two rows may otherwise be equal.</b> Blocks are
///         distinguished by archetype today, but <c>BuildContext.For</c> cannot reconcile two equal
///         keys in one loop at all, and a panel that threw on a scene shape nobody has written yet is
///         not worth the four characters saved. See <see cref="CompiledDiagnosticRow" />, where it is
///         not hypothetical.
///     </para>
/// </remarks>
internal readonly record struct CompiledBlockRow(int Slot, string Archetype, string Count, string Bytes);

/// <summary>One complaint's row.</summary>
/// <param name="Slot">Where it is in the order the compiler said things.</param>
/// <param name="Severity">Error, Warning or Information, as the compiler spells it.</param>
/// <param name="Message">What it said.</param>
/// <param name="Class">Which of the three colours the severity gets.</param>
/// <remarks>
///     ⚠ <b>The slot is load-bearing here.</b> The compiler reports every problem before failing once
///     — which is this pane's whole argument — so a scene with four duplicate ids produces four
///     diagnostics, and nothing stops two of them being the same severity and the same sentence. Two
///     equal keys in one loop is a case <c>BuildContext.For</c> has no answer to.
/// </remarks>
internal readonly record struct CompiledDiagnosticRow(int Slot, string Severity, string Message, string Class);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The panel ledger's shape 5, and the sanctioned escape from it.</b> An interpolation is
///         <c>BuildContext.Text</c>, which appends a <c>text</c> <i>child</i>; an attribute on a
///         lowercase tag is <c>BuildContext.Attribute</c>, which is a selector attribute and not
///         <see cref="UiElement.Text" />. A four-line subclass answering to the tag the stylesheet
///         already names moves nothing: same tag, same position, same own text.
///     </para>
///     <para>
///         ⚠ <b>Six here and not eight.</b> <c>fact-name</c> and <c>fact-value</c> are the assembly's,
///         in <c>Captions.cs</c> — four panels write them now, which is the point at which a fourth
///         private copy of a tag name becomes the one that drifts.
///     </para>
/// </remarks>
internal sealed class CompiledSceneLabel : UiElement {
    /// <inheritdoc />
    protected override string TagName => "compiled-scene-label";
}

/// <inheritdoc cref="CompiledSceneLabel" />
internal sealed class CompiledSceneArchetype : UiElement {
    /// <inheritdoc />
    protected override string TagName => "compiled-scene-archetype";
}

/// <inheritdoc cref="CompiledSceneLabel" />
internal sealed class CompiledSceneCount : UiElement {
    /// <inheritdoc />
    protected override string TagName => "compiled-scene-count";
}

/// <inheritdoc cref="CompiledSceneLabel" />
internal sealed class CompiledSceneBytes : UiElement {
    /// <inheritdoc />
    protected override string TagName => "compiled-scene-bytes";
}

/// <inheritdoc cref="CompiledSceneLabel" />
internal sealed class CompiledSceneSeverity : UiElement {
    /// <inheritdoc />
    protected override string TagName => "compiled-scene-severity";
}

/// <inheritdoc cref="CompiledSceneLabel" />
internal sealed class CompiledSceneMessage : UiElement {
    /// <inheritdoc />
    protected override string TagName => "compiled-scene-message";
}
