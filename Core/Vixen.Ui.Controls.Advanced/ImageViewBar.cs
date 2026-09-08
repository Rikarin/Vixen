// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls.Advanced;

/// <summary>The two pickers that drive an <see cref="ImageView" />'s channel and colour space.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="ImageView.Channels" /> and <see cref="ImageView.ColorSpace" /> worked and
///         nothing set them — <a href="https://github.com/Rikarin/Vixen/issues/1012">#1012</a>.</b>
///         The picker was correct and unreachable: a sweep of <c>.cs</c> and <c>.vxml</c> found the
///         two enums named nowhere outside the control and its own tests. Three panels want the same
///         strip — the texture graph's node preview, the layer stack's, the paint view's — so the
///         strip is a control rather than three hand-rolled copies that drift.
///     </para>
///     <para>
///         ⚠ <b>Five segments and two, rather than one control with seven.</b> They are two
///         questions: which channels, and through which transfer function. <c>ImageColorSpace</c>'s
///         own remark is the argument — an sRGB texture shown as linear and a linear one shown as
///         sRGB look like different bugs and are the commonest false bug report in a texturing tool
///         — and a viewer asking "the alpha, as stored" needs both answers at once, which one enum
///         of seven cannot express.
///     </para>
///     <para>
///         ⚠ <b>It writes the control's properties and asserts nothing about the picture, and the
///         test is where that distinction is paid.</b> A binding that sets the property and a
///         binding that reaches the picture are the same assertion only until somebody stops passing
///         <c>view:</c> at the <c>DrawImage</c> call — so <c>ImageViewBarTests</c> clicks a segment
///         and reads the <em>draw command's</em> <c>View</c>, not the property back.
///     </para>
///     <para>
///         <b>Three panels host one</b> — <c>TextureGraphView</c>, <c>LayerStackView</c> and
///         <c>PaintUvView</c>, each pointed at that pane's own <see cref="ImageView" />, which is
///         all of <a href="https://github.com/Rikarin/Vixen/issues/1012">#1012</a>. ⚠ <b>The strip
///         is added <em>before</em> the viewer and <c>View</c> assigned after</b>: <c>Add</c>
///         appends, so the toggles sit above the picture, and assigning <c>View</c> adopts what the
///         pane is already showing rather than pushing this strip's first segment over it.
///     </para>
/// </remarks>
public sealed partial class ImageViewBar : Control {
    ImageView? view;
    SegmentedControl channels = null!;
    SegmentedControl space = null!;

    /// <inheritdoc />
    protected override string TagName => "image-view-bar";

    /// <inheritdoc />
    /// <remarks>
    ///     False: the two segmented controls inside are the tab stops, exactly as a toolbar's
    ///     buttons are. A strip that took the focus itself would be a stop that does nothing.
    /// </remarks>
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ ARIA <c>toolbar</c> rather than <c>group</c> or nothing, and the difference a screen
    ///     reader acts on is arrow-key navigation: a toolbar is announced as one stop whose contents
    ///     the arrows move through, which is exactly what two segmented controls side by side are.
    ///     A roleless container would put five radios and two radios into the tree as siblings of
    ///     whatever the pane holds, with nothing saying they are one row of settings about one
    ///     picture.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Toolbar;

    /// <summary>The view being driven, or <c>null</c> for a strip that drives nothing.</summary>
    /// <remarks>
    ///     ⚠ <b>Assigning one adopts the view's current answers rather than imposing the strip's.</b>
    ///     A viewer restored from a saved layout already holds what the author last chose, and a
    ///     strip that pushed <c>Rgb</c>/<c>Srgb</c> on attachment would silently discard it — which
    ///     is the "a mechanism whose caller passes the default" failure, one level up from where it
    ///     usually sits.
    /// </remarks>
    public ImageView? View {
        get => view;
        set {
            view = value;
            Show();
        }
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        // ⚠ A class rather than a tag override. `Add<T>(string)` takes the *tag*, so naming the
        // strip that way would rename the element out of every `segmented-control` rule the theme
        // has — a picker with no joined border and no chosen segment, styled by nothing.
        channels = Add<SegmentedControl>(null, null, "image-view-channels");

        // ⚠ The value is the enum member's own name and the label is not, which is the only place
        // the two may differ: `Enum.Parse` reads the value back, so a label shortened to "R" for a
        // strip four segments wider than it wants to be costs nothing.
        channels.AddSegment(nameof(ImageChannels.Rgb), "RGB");
        channels.AddSegment(nameof(ImageChannels.Red), "R");
        channels.AddSegment(nameof(ImageChannels.Green), "G");
        channels.AddSegment(nameof(ImageChannels.Blue), "B");
        channels.AddSegment(nameof(ImageChannels.Alpha), "A");

        space = Add<SegmentedControl>(null, null, "image-view-space");
        space.AddSegment(nameof(ImageColorSpace.Srgb), "sRGB");
        space.AddSegment(nameof(ImageColorSpace.Linear), "Linear");

        channels.ValueChanged += (_, chosen) => {
            if (view is not null && Enum.TryParse<ImageChannels>(chosen, out var picked)) {
                view.Channels = picked;
            }
        };

        space.ValueChanged += (_, chosen) => {
            if (view is not null && Enum.TryParse<ImageColorSpace>(chosen, out var picked)) {
                view.ColorSpace = picked;
            }
        };

        Show();
    }

    /// <summary>Reads the view's current request back onto the two strips.</summary>
    /// <remarks>
    ///     ⚠ <b>One direction only, and it runs on attachment rather than on every change.</b> A
    ///     strip that also subscribed to the view would be a second writer of the same two
    ///     properties, and the loop it closes — segment writes property, property writes segment,
    ///     segment raises again — is only broken today by <c>SegmentedControl.Value</c> comparing
    ///     before it raises. Depending on that is a bug waiting for the day the comparison moves.
    ///     A host that changes <see cref="ImageView.Channels" /> behind the strip's back reassigns
    ///     <see cref="View" />, which is one line and is honest about what it costs.
    /// </remarks>
    public void Show() {
        if (channels is null || space is null) {
            return;
        }

        channels.Value = (view?.Channels ?? ImageChannels.Rgb).ToString();
        space.Value = (view?.ColorSpace ?? ImageColorSpace.Srgb).ToString();
    }
}
