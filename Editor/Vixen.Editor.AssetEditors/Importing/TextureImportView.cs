// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>A texture, open for editing: the pixels, the mip ladder, and the import settings.</summary>
/// <remarks>
///     <para>
///         The panel is <c>TextureImportView.vxml</c>; this file is the accessibility modifier, the two
///         records the markup reads, and the three elements that exist only so that markup can write an
///         intrinsic tag's own <c>Text</c>.
///     </para>
///     <para>
///         <b>Doc 11 asks for four things and three of them are here in full.</b> Import settings
///         and the platform-override matrix are <see cref="ImportSettingsView" />'s; the mip
///         inspector is <see cref="TextureLadder" />'s arithmetic, drawn as the ladder the settings
///         will produce with what each level costs in the format it will ship in.
///     </para>
///     <para>
///         ⚠ <b>The channel viewer says which channels to show and does not draw them.</b> Nothing
///         in this assembly has a graphics device: a texture reaches the interface as a number a
///         <c>UiRenderer</c> handed out for a registered texture, and registering one means
///         uploading it. So the view decodes the file — that much is CPU work and belongs here —
///         exposes <c>Source</c>, <c>Channels</c> and <c>MipLevel</c>, and the application uploads and
///         sets <c>Preview</c>'s number. It is exactly the split <c>ScenePresenter</c> already has with
///         the scene panel, and for the same reason.
///     </para>
///     <para>
///         ⚠ <b>What is decoded is the <i>source</i>, not the artefact.</b> An author editing import
///         settings wants to see what they are about to compress, and a preview of the last build's
///         output would be a picture of settings that have since been changed. The consequence worth
///         knowing about is that the preview never shows compression artefacts — comparing those
///         needs the artefact store and a second image beside this one, which is not built.
///     </para>
/// </remarks>
public sealed partial class TextureImportView;

/// <summary>The four numbers at the top of the texture tab, as one value.</summary>
/// <param name="Source">What the file is, or that nothing decoded it.</param>
/// <param name="ShipsAs">What a build would produce from it.</param>
/// <param name="Levels">How many levels the chain has.</param>
/// <param name="Total">And what the whole chain costs.</param>
/// <remarks>
///     ⚠ <b>A snapshot record in one signal, and the reason is a stale-binding trap rather than
///     taste.</b> Three of the four are functions of <c>TextureImportSettings</c> — a plain mutable
///     object that no signal watches, edited through the inspector and reported by its
///     <c>ValueChanged</c> — so four separate bindings would depend on four different things and
///     three of them would not include the settings at all. <c>Refresh</c> computes all four
///     together, which is the only moment any of them is known to be right.
/// </remarks>
internal readonly record struct TextureFacts(string Source, string ShipsAs, string Levels, string Total);

/// <summary>One of the four channel toggles, as the <c>@for</c> keys it.</summary>
/// <param name="Label">The letter on the button.</param>
/// <param name="Channel">Which channel it turns on and off.</param>
internal readonly record struct ChannelButton(string Label, TextureChannels Channel);

/// <summary>
///     ⚠ The ladder's three cells, which exist only so that markup can set an intrinsic tag's own
///     <c>Text</c>.
/// </summary>
/// <remarks>
///     The panel ledger's shape 5. An interpolation is <c>BuildContext.Text</c>, which appends a
///     <c>text</c> <i>child</i>, and an attribute on a lowercase tag is a selector attribute rather
///     than <see cref="UiElement.Text" /> — so <c>row.Add("ladder-level").Text = …</c> has no markup
///     spelling and a four-line subclass answering to the tag the stylesheet already names is the
///     whole fix. <c>ladder-level</c>, <c>ladder-extent</c> and <c>ladder-bytes</c> are declared in
///     <c>AssetEditorTheme.vcss</c> and are unchanged by any of this.
/// </remarks>
internal sealed class LadderLevel : UiElement {
    /// <inheritdoc />
    protected override string TagName => "ladder-level";
}

/// <inheritdoc cref="LadderLevel" />
internal sealed class LadderExtent : UiElement {
    /// <inheritdoc />
    protected override string TagName => "ladder-extent";
}

/// <inheritdoc cref="LadderLevel" />
internal sealed class LadderBytes : UiElement {
    /// <inheritdoc />
    protected override string TagName => "ladder-bytes";
}
