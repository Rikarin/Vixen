// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Audio;

/// <summary>A mixer, open for editing: a strip per bus, its inserts, and the snapshots.</summary>
/// <remarks>
///     <para>
///         The panel is <c>AudioMixerView.vxml</c>; this file is the accessibility modifier, the
///         records the markup's snapshot is made of, and the handful of elements that exist only so
///         that markup can write an intrinsic tag's own <c>Text</c>. Same arrangement as
///         <c>GpuTimelineView</c>.
///     </para>
///     <para>
///         Doc 20's B5 row is "buses, sends, effects, snapshots", and the layout is the one every
///         mixer has had since the hardware ones: a horizontal row of vertical strips, because a
///         fader is a thing you compare against its neighbours and a list of numbers is not.
///     </para>
///     <para>
///         ⚠ <b>The fader is a slider in decibels with a fixed scale, not a normalised one.</b> A
///         0–1 fader is linear in amplitude, which puts every useful mix position in the top fifth of
///         its travel; −60 to +12 dB is what a person can actually aim at, and it is the number the
///         file stores, so what the strip shows and what the file says are the same number.
///     </para>
///     <para>
///         ⚠ <b>Solo is an editor state and is not written.</b> It is a thing you do while listening
///         and never a thing you ship, so it lives here and mutes the others in the preview rather
///         than being a member of <see cref="Vixen.Audio.Assets.MixerBusAsset" /> that a build would
///         honour.
///     </para>
/// </remarks>
public sealed partial class AudioMixerView;

/// <summary>One strip, as the <c>@for</c> keys it.</summary>
/// <param name="Name">The bus's name, which is also what the strip says and what a handler writes to.</param>
/// <param name="Parent">What it feeds, or empty.</param>
/// <param name="Master">Whether this is the implicit master strip, which is drawn and is not in the file.</param>
/// <remarks>
///     ⚠ <b>The three things whose change is a different <i>strip</i>, and deliberately not the
///     gain.</b> A <c>@for</c> key that carried the gain would replace the fader's region on every
///     step of a drag — the element under the pointer, mid-gesture — where these three replace it
///     exactly when the row genuinely became a different row. Everything that does change while a
///     strip lives (its gain, its mute, whether it is selected) reaches the screen through a binding
///     inside the body, which is what a surviving key leaves running.
///     <para>
///         <see cref="Master" /> is in the key rather than inferred from the name so that a file
///         which really does declare a bus called "Master" produces two distinguishable keys.
///         <c>BuildContext.For</c> cannot reconcile two equal keys in one loop, and the panel it
///         replaces drew both strips.
///     </para>
/// </remarks>
internal readonly record struct MixerColumn(string Name, string Parent, bool Master);

/// <summary>One send row.</summary>
/// <param name="Slot">Its index in the bus's list, which is what an edit writes back to.</param>
/// <param name="Target">The bus it feeds, or empty for a send that names nowhere.</param>
/// <remarks>
///     The level is not in the key, for <see cref="MixerColumn" />'s reason: scrubbing the field
///     must not replace the field.
/// </remarks>
internal readonly record struct MixerSend(int Slot, string Target);

/// <summary>One insert row.</summary>
/// <param name="Slot">Its index in the bus's list, which is what Remove writes back to.</param>
/// <param name="Name">What the row says, which is the effect asset's type name without the suffix.</param>
internal readonly record struct MixerInsert(int Slot, string Name);

/// <summary>One row of the snapshot list.</summary>
/// <param name="Name">The snapshot's name, or empty for the authored mix.</param>
/// <param name="Caption">What the row says.</param>
internal readonly record struct MixerSnapshotLine(string Name, string Caption);

/// <summary>One line of the analysis list.</summary>
/// <param name="Slot">Its position, so that two identical messages are two keys.</param>
/// <param name="Stage">Which stage said it.</param>
/// <param name="Message">What it said.</param>
internal readonly record struct MixerNote(int Slot, string Stage, string Message);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the panel ledger's shape 5, and these are the sanctioned escape from it.</b> An
///         interpolation is <c>BuildContext.Text</c>, which appends a <c>text</c> <i>child</i>; an
///         attribute on a lowercase tag is <c>BuildContext.Attribute</c>, which is a selector
///         attribute and not <see cref="UiElement.Text" />. So <c>row.Add("fact-name").Text = label</c>
///         has no markup spelling — but a <i>capitalised</i> tag is a real property assignment
///         (<c>ComponentEmitter.EmitParameter</c>), and <c>Text</c> is a <c>[UiProperty]</c> on every
///         element. A four-line subclass that answers to the tag the stylesheet already names is
///         therefore the whole fix, and it moves nothing: same tag, same position, same own text.
///     </para>
///     <para>
///         ⚠ <b>A C# subclass rather than a <c>.vxml</c> part, unlike <c>FactRow</c>.</b> A part is
///         worth a file when it has a shape — a row is four elements — and these have none: each is
///         one element whose only content is its own text. What <c>FactRow</c> is to a row, this is
///         to a word.
///     </para>
///     <para>
///         ⚠ <b>Internal, and the two analysis ones are the seed of something shared.</b>
///         <c>analysis-row</c>/<c>analysis-stage</c>/<c>analysis-message</c> is a triple this
///         assembly builds in more than one panel; hoisting it into a part is a job for whoever ports
///         the second of them, because doing it here would move pixels in a panel this change has not
///         measured.
///     </para>
/// </remarks>
internal sealed class MixerStripName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "mixer-strip-name";
}

/// <inheritdoc cref="MixerStripName" />
internal sealed class MixerStripParent : UiElement {
    /// <inheritdoc />
    protected override string TagName => "mixer-strip-parent";
}

/// <inheritdoc cref="MixerStripName" />
internal sealed class MixerStripGain : UiElement {
    /// <inheritdoc />
    protected override string TagName => "mixer-strip-gain";
}

/// <inheritdoc cref="MixerStripName" />
internal sealed class MixerTitle : UiElement {
    /// <inheritdoc />
    protected override string TagName => "mixer-title";
}

/// <inheritdoc cref="MixerStripName" />
internal sealed class MixerSnapshotRow : UiElement {
    /// <inheritdoc />
    protected override string TagName => "mixer-snapshot";
}

/// <inheritdoc cref="MixerStripName" />
internal sealed class FactName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "fact-name";
}

/// <inheritdoc cref="MixerStripName" />
internal sealed class AnalysisStage : UiElement {
    /// <inheritdoc />
    protected override string TagName => "analysis-stage";
}

/// <inheritdoc cref="MixerStripName" />
internal sealed class AnalysisMessage : UiElement {
    /// <inheritdoc />
    protected override string TagName => "analysis-message";
}

/// <summary>A <c>fact-value</c> holding a dropdown whose options are data.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Item 4 of the ledger's build list, worked around rather than fixed.</b> A
///         <see cref="Select" />'s options are not its children — they live in a popover hanging off
///         the document root — so <c>AddOption</c> is a method and there is no markup spelling for
///         "these options". The panel it replaces called it in a loop while rebuilding the side
///         panel; markup has no per-region C#, and <c>OnComposed</c> runs once while this element is
///         inside a <c>@if</c> arm that is rebuilt whenever the selected bus changes.
///     </para>
///     <para>
///         ⚠ <b>So the list is a <i>property</i>, which is a thing markup can bind.</b>
///         <c>Choices="@…"</c> on a capitalised tag is <c>ctx.Bind(() =&gt; cell.Choices = …)</c> — a
///         real effect, registered against the arm's region and re-run whenever what it read changes.
///         The setter is guarded on the list it was last given, so a flush that changed something
///         else does not close and refill a dropdown the user is looking at.
///     </para>
///     <para>
///         The tag is <c>fact-value</c> and the control is its only child, which is exactly
///         <c>row.Add("fact-value").Add&lt;Select&gt;()</c>.
///     </para>
/// </remarks>
internal sealed class OptionCell : UiElement {
    ImmutableArray<string> choices = [];

    /// <inheritdoc />
    protected override string TagName => "fact-value";

    /// <summary>The dropdown.</summary>
    public Select Picker { get; private set; } = null!;

    /// <summary>What it says when nothing is chosen.</summary>
    public string? Placeholder {
        get => Picker.Placeholder;
        set => Picker.Placeholder = value;
    }

    /// <summary>What is on offer.</summary>
    public ImmutableArray<string> Choices {
        get => choices;
        set {
            if (choices.SequenceEqual(value, StringComparer.Ordinal)) {
                return;
            }

            choices = value;
            Picker.ClearOptions();

            foreach (var option in value) {
                Picker.AddOption(option);
            }
        }
    }

    /// <summary>Run when one is chosen, with what it was.</summary>
    public Action<string?>? Chosen { get; set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Picker = Add<Select>();
        Picker.SelectionChanged += (_, value) => Chosen?.Invoke(value);
    }
}

/// <summary>Opens an audio mixer.</summary>
public sealed class AudioMixerEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Audio Mixer";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [AudioMixerDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new AudioMixerDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // ⚠ A mixer is a row of strips that scrolls sideways, and its faders fill whatever height they
        // are given. Let the panel scroll and the faders stretch to their content instead of to the
        // window — which is a mixer with no bounded height at all, and a horizontal strip that has
        // lost the box it was scrolling inside.
        DockPanel.Fills(panel);

        var view = panel.Add<AudioMixerView>();
        view.Show((AudioMixerDocument) document);

        return view;
    }
}
