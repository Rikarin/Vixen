// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Imaging;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Importing;

/// <summary>A texture, open for editing: the pixels, the mip ladder, and the import settings.</summary>
/// <remarks>
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
///         exposes <see cref="Source" />, <see cref="Channels" /> and <see cref="MipLevel" />, and
///         the application uploads and sets <see cref="Preview" />'s number. It is exactly the split
///         <c>ScenePresenter</c> already has with the scene panel, and for the same reason.
///     </para>
///     <para>
///         ⚠ <b>What is decoded is the <i>source</i>, not the artefact.</b> An author editing import
///         settings wants to see what they are about to compress, and a preview of the last build's
///         output would be a picture of settings that have since been changed. The consequence worth
///         knowing about is that the preview never shows compression artefacts — comparing those
///         needs the artefact store and a second image beside this one, which is not built.
///     </para>
/// </remarks>
public sealed class TextureImportView : Control {
    readonly List<ToggleButton> channels = [];

    TextureImportDocument? document;
    TextureData? source;
    int mipLevel;
    bool listening;

    /// <inheritdoc />
    protected override string TagName => "texture-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Where the pixels are drawn once something has uploaded them.</summary>
    public Image Preview { get; private set; } = null!;

    /// <summary>What the file is, in numbers: extent, format, and what it will ship as.</summary>
    public UiElement Facts { get; private set; } = null!;

    /// <summary>The mip ladder, one row a level.</summary>
    public UiElement Ladder { get; private set; } = null!;

    /// <summary>Shown when the file has no decoder in this build, or would not decode.</summary>
    public Alert Undecodable { get; private set; } = null!;

    /// <summary>The settings, the overrides and the addressable block.</summary>
    public ImportSettingsView SettingsView { get; private set; } = null!;

    /// <summary>The decoded source, or <see langword="null" /> if nothing could decode it.</summary>
    public TextureData? Source => source;

    /// <summary>Which channels the viewer is asking for.</summary>
    public TextureChannels Channels { get; private set; } = TextureChannels.All;

    /// <summary>Which mip level the viewer is asking for.</summary>
    public int MipLevel => mipLevel;

    /// <summary>The ladder the current settings produce.</summary>
    public IReadOnlyList<TextureMipLevel> Levels { get; private set; } = [];

    /// <summary>Raised when the channels or the level change, for whatever is uploading the picture.</summary>
    public event Action<TextureImportView>? ViewChanged;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Preview = Part<Image>();
        Preview.Description = "The texture's source pixels";

        Undecodable = Part<Alert>();
        Undecodable.AddClass("hidden");
        Undecodable.Title = "No preview";

        var bar = Part("texture-channels");

        Channel(bar, "R", TextureChannels.Red);
        Channel(bar, "G", TextureChannels.Green);
        Channel(bar, "B", TextureChannels.Blue);
        Channel(bar, "A", TextureChannels.Alpha);

        Facts = Part("texture-facts");
        Ladder = Part("texture-ladder");
        SettingsView = Part<ImportSettingsView>();
    }

    /// <summary>Shows a texture.</summary>
    /// <param name="texture">The document.</param>
    public void Show(TextureImportDocument texture) {
        ArgumentNullException.ThrowIfNull(texture);

        document = texture;
        source = TextureLadder.TryDecode(texture.AssetPath, out var reason);

        if (reason is null) {
            Undecodable.AddClass("hidden");
        } else {
            Undecodable.RemoveClass("hidden");
            Undecodable.Message = reason;
        }

        SettingsView.Show(texture);

        // Every settings edit moves the ladder — a size limit, a format, mips on or off — so the one
        // event the inspector already raises is what keeps the numbers honest, rather than the panel
        // polling or the document growing a second change signal.
        //
        // ⚠ Subscribed once per view rather than once per Show, because a view shown twice would
        // otherwise recompute the ladder twice per keystroke, and three times the time after that.
        if (!listening) {
            listening = true;
            SettingsView.Settings.ValueChanged += (_, _) => Refresh();
        }

        Refresh();
    }

    /// <summary>Turns a channel on or off.</summary>
    /// <param name="channel">Which one.</param>
    /// <param name="shown">Whether to show it.</param>
    public void SetChannel(TextureChannels channel, bool shown) {
        var updated = shown ? Channels | channel : Channels & ~channel;

        if (updated == Channels) {
            return;
        }

        Channels = updated;

        foreach (var toggle in channels) {
            // Restated from the value rather than left to the button that was pressed, so a caller
            // setting the channels in code and a user pressing a button leave the bar in the same
            // state.
            toggle.IsChecked = (Channels & ChannelOf(toggle.Label)) != 0;
        }

        ViewChanged?.Invoke(this);
    }

    /// <summary>Shows a different level of the chain.</summary>
    /// <param name="level">Which one, clamped to what the ladder has.</param>
    public void SetMipLevel(int level) {
        var clamped = Math.Clamp(level, 0, Math.Max(Levels.Count - 1, 0));

        if (clamped == mipLevel) {
            return;
        }

        mipLevel = clamped;
        Restate();

        ViewChanged?.Invoke(this);
    }

    /// <summary>Recomputes the ladder and the facts from the settings as they stand.</summary>
    public void Refresh() {
        if (document is not { } texture) {
            return;
        }

        var width = source?.Width ?? 0;
        var height = source?.Height ?? 0;

        Levels = TextureLadder.Build(width, height, texture.Texture);
        mipLevel = Math.Clamp(mipLevel, 0, Math.Max(Levels.Count - 1, 0));

        Clear(Facts);

        var shipped = TextureLadder.Resolve(texture.Texture);
        var (shippedWidth, shippedHeight) = TextureLadder.Fit(width, height, texture.Texture.MaxSize);

        Fact("Source", source is null ? "not decoded" : $"{width}×{height} {source.Format}");
        Fact("Ships as", $"{shippedWidth}×{shippedHeight} {shipped}");
        Fact("Levels", Levels.Count.ToString(CultureInfo.InvariantCulture));
        Fact("Total", Measure(TextureLadder.TotalBytes(Levels)));

        Restate();
    }

    void Restate() {
        Clear(Ladder);

        for (var index = 0; index < Levels.Count; index++) {
            var level = Levels[index];
            var row = Ladder.Add("ladder-row");

            if (index == mipLevel) {
                row.AddClass("selected");
            }

            row.Add("ladder-level").Text = level.Level.ToString(CultureInfo.InvariantCulture);
            row.Add("ladder-extent").Text = $"{level.Width}×{level.Height}";
            row.Add("ladder-bytes").Text = Measure(level.Bytes);
        }
    }

    void Channel(UiElement bar, string label, TextureChannels channel) {
        var toggle = bar.Add<ToggleButton>();

        toggle.Label = label;
        toggle.IsChecked = (Channels & channel) != 0;
        toggle.CheckedChanged += (_, shown) => SetChannel(channel, shown);

        channels.Add(toggle);
    }

    static TextureChannels ChannelOf(string? label) => label switch {
        "R" => TextureChannels.Red,
        "G" => TextureChannels.Green,
        "B" => TextureChannels.Blue,
        "A" => TextureChannels.Alpha,
        _ => TextureChannels.None
    };

    void Fact(string name, string value) {
        var row = Facts.Add("fact-row");

        row.Add("fact-name").Text = name;
        row.Add("fact-value").Text = value;
    }

    static void Clear(UiElement element) {
        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }

    /// <summary>A byte count as something a person reads.</summary>
    /// <param name="bytes">How many.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     Binary units, because what this is counting is memory a device has to find rather than
    ///     bytes going down a wire.
    /// </remarks>
    internal static string Measure(long bytes) => bytes switch {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KiB",
        _ => (bytes / (1024d * 1024d)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB"
    };
}
