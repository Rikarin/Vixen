// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>A run of text.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Called <c>TextBlock</c> rather than <c>Text</c>, and the reason is not taste.</b>
///         <c>Vixen.Ui.Text</c> is a namespace — the shaping, itemisation and line-breaking assembly
///         — so a type called <c>Vixen.Ui.Controls.Text</c> would be ambiguous with it in every file
///         that used both, which is every file that draws a string. WPF reached the same name from a
///         different direction.
///     </para>
///     <para>
///         It carries its text directly rather than in a child, because it has nothing else to hold:
///         an element with text measures itself and may not have children, and that is exactly what
///         this is for.
///     </para>
/// </remarks>
public sealed partial class TextBlock : Control {
    /// <inheritdoc />
    protected override string TagName => "text";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;
}

/// <summary>Text that goes somewhere when it is activated.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here navigates.</b> A link raises <see cref="ClickEvent" /> and carries a
///         <see cref="Href" />, and what that means is the application's — a URL to open, a document
///         to switch to, an asset to reveal in a browser. A UI framework that shelled out to a
///         system browser on click would be a UI framework that can open arbitrary URLs on behalf of
///         a game's content, which is a security decision no widget should be making.
///     </para>
///     <para>
///         Activated by Enter rather than Space, which is what a link does everywhere: Space scrolls.
///     </para>
/// </remarks>
public sealed partial class Link : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "link";

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>link</c> rather than the base's <c>button</c>, and the distinction is one a
    ///     screen-reader user acts on: a link is announced in the links list and is understood to
    ///     take you somewhere, a button is understood to do something here.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Link;

    /// <summary>Where it points. Meaningful to the application and to nothing here.</summary>
    [UiProperty]
    public partial string? Href { get; set; }
}

/// <summary>A small count or status, attached to something else.</summary>
public sealed partial class Badge : Control {
    /// <inheritdoc />
    protected override string TagName => "badge";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>status</c>. A badge is a count or a state attached to something else, and it is
    ///     the half of that pairing that changes — an unread count going from three to four is a
    ///     thing a screen-reader user is entitled to hear about without walking back to it. The
    ///     name is its own <c>Text</c>, from the base.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Status;
}

/// <summary>A person or an entity, as a circle with their initials in it.</summary>
/// <remarks>
///     ⚠ <b>Initials only, for now, and it is the draw list rather than this control that says so.</b>
///     There is no texture command — <c>DrawCommandKind</c> has rectangles, borders, text, paths and
///     clips — so an avatar cannot show a picture until one exists. <see cref="Initials" /> derives
///     from a name so that the fallback every avatar needs anyway is the thing that works today, and
///     the picture is owed.
/// </remarks>
public sealed partial class Avatar : Control {
    /// <inheritdoc />
    protected override string TagName => "avatar";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    /// <remarks>
    ///     ARIA <c>img</c> — the member is <see cref="AccessibleRole.Img" /> because lowercasing a
    ///     member name is the ARIA token for every member with no exception table beside it.
    /// </remarks>
    protected override AccessibleRole NativeRole => AccessibleRole.Img;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><see cref="Name" /> and not the initials, and this is the case that makes the
    ///     distinction worth a line.</b> "AC" is what fits in a circle; it is not what a person is
    ///     called, and a screen reader spelling out two letters has told the user nothing. The
    ///     initials are the fallback only when nobody said who it is, which is the same order the
    ///     control derives them in.
    /// </remarks>
    protected override string? NativeAccessibleName => Name ?? Text;

    /// <summary>Who it is. Setting it sets the initials.</summary>
    [UiProperty(Changed = nameof(OnNameChanged))]
    public partial string? Name { get; set; }

    /// <summary>The letters shown, derived from <see cref="Name" /> unless set directly.</summary>
    public string? Initials {
        get => Text;
        set => Text = value;
    }

    /// <summary>The first letter of each of the first two words.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The initials, uppercased.</returns>
    /// <remarks>
    ///     ⚠ <b>Two at most, and by words rather than by characters.</b> Taking the first two
    ///     characters gives "AL" for "Alice", which reads as a different person's initials; taking
    ///     every word gives four letters for a Spanish name and does not fit the circle. The rule
    ///     below is what every product that does this has converged on, and it is still wrong for
    ///     scripts that do not put a space between given and family names — said out loud, because
    ///     an initials algorithm that pretends to be universal is worse than one that admits it.
    /// </remarks>
    public static string Of(string? name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return string.Empty;
        }

        var initials = new StringBuilder(2);

        foreach (var word in name.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries)) {
            initials.Append(char.ToUpperInvariant(word[0]));

            if (initials.Length == 2) {
                break;
            }
        }

        return initials.ToString();
    }

    void OnNameChanged(string? previous, string? current) => Text = Of(current);
}

/// <summary>A grey box standing in for content that has not arrived.</summary>
/// <remarks>
///     A shape and nothing else — no text, no children, and no behaviour. What makes it worth a type
///     rather than a class on a <c>div</c> is that the theme animates it, and an application that
///     wrote the class by hand would have to know the theme's name for it.
/// </remarks>
public sealed partial class Skeleton : Control {
    /// <inheritdoc />
    protected override string TagName => "skeleton";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;
}

/// <summary>A picture.</summary>
/// <remarks>
///     <para>
///         <b>Two halves, and only one of them is here.</b> <see cref="Source" /> names the asset the
///         way the application's asset system names things, and <c>aspect-ratio</c> in the stylesheet
///         keeps the box the right shape. What turns a name into pixels is the application's: it
///         loads the asset, registers the texture with the renderer, and puts the number it was given
///         in <see cref="Texture" />.
///     </para>
///     <para>
///         ⚠ <b>An unset <see cref="Texture" /> draws nothing</b>, which is what an image whose asset
///         has not finished loading should do. A background colour on it is a perfectly good
///         placeholder in the meantime.
///     </para>
/// </remarks>
public sealed partial class Image : Control {
    /// <inheritdoc />
    protected override string TagName => "image";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <inheritdoc />
    protected override AccessibleRole NativeRole => AccessibleRole.Img;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b><see cref="Description" /> was documented as being "for the accessibility bridge"
    ///     before there was one, and this line is the first thing that reads it.</b> Not
    ///     <see cref="Source" />: an asset name is a path, and a screen reader reading
    ///     <c>ui/icons/warning@2x</c> out loud is the worst of the three available answers. An
    ///     image nobody described reports <c>null</c>, which for a decorative picture is correct
    ///     and for a meaningful one is what a gate should fail on — nothing here can invent a name
    ///     for a texture.
    /// </remarks>
    protected override string? NativeAccessibleName => Description;

    /// <summary>What to show, named the way the application's asset system names things.</summary>
    [UiProperty]
    public partial string? Source { get; set; }

    /// <summary>What it is a picture of, for a tooltip and for the accessibility bridge.</summary>
    [UiProperty]
    public partial string? Description { get; set; }

    /// <summary>The renderer's number for the texture to draw, or zero for none.</summary>
    /// <remarks>
    ///     Opaque, for the reason <c>DrawCommand.Image</c> gives: a texture view is the graphics
    ///     layer's vocabulary and this assembly does not reference it. The application registers a
    ///     texture with <c>UiRenderer.RegisterImage</c> and puts the number it was handed here.
    /// </remarks>
    public ulong Texture { get; set; }

    /// <summary>Which part of the texture to draw, in UVs. The whole of it by default.</summary>
    /// <remarks>
    ///     Not called <c>Source</c> because that name is taken by the asset this shows, and the two
    ///     are different questions: one is <i>which picture</i>, this is <i>which part of it</i> — a
    ///     sprite sheet's cell, a flipped video frame.
    /// </remarks>
    public Rectangle SourceRectangle { get; set; } = new(0f, 0f, 1f, 1f);

    /// <summary>How far the corners reach in, in document pixels. Empty stretches the whole image.</summary>
    /// <remarks>
    ///     What turns one small texture into a panel, a button and a tooltip at three different
    ///     sizes with the same corners. Set it together with <see cref="SourceBorder" /> — either one
    ///     alone draws the ordinary stretched image, because a nine-slice needs both halves of the
    ///     cut.
    /// </remarks>
    public NineSlice Border { get; set; }

    /// <summary>The same cut of the texture, in UVs.</summary>
    /// <remarks>
    ///     ⚠ In UVs rather than texels, for the reason <see cref="SourceRectangle" /> is: this
    ///     assembly does not know how big the texture is, so the application that registered it
    ///     divides. A 16-pixel border on a 128-pixel sheet is <c>NineSlice.Uniform(16f / 128f)</c>.
    /// </remarks>
    public NineSlice SourceBorder { get; set; }

    /// <summary>Whether the middle of a nine-slice is left undrawn.</summary>
    /// <remarks>A frame with a hole in it — a selection outline, a window chrome over a viewport.</remarks>
    public bool HollowCentre { get; set; }

    /// <summary>How big the picture actually is, in its own pixels. Zero where nobody said.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Supplied by the application rather than measured here, and that is a layering
    ///         decision rather than an omission.</b> <see cref="Texture" /> is an opaque number the
    ///         renderer owns — this assembly does not reference <c>Vixen.Graphics</c>, which is the
    ///         whole bargain — so nothing on this side can ask a texture how big it is. The asset
    ///         layer that registered it knows, and whoever writes <see cref="Texture" /> writes this
    ///         beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero means unknown, and unknown means <c>object-fit: fill</c> whatever the
    ///         stylesheet says.</b> CSS Images 3 § 5.5 defines every keyword but <c>fill</c> as a
    ///         relation between the intrinsic ratio and the box, so with no ratio there is nothing to
    ///         relate — <c>contain</c>, <c>cover</c>, <c>none</c> and <c>scale-down</c> are not merely
    ///         unimplemented there, they are undefined. Stretching to the box is CSS's own answer for
    ///         content with no intrinsic dimensions, so an application that never sets this sees
    ///         exactly the picture it saw before the property existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It sizes the <i>picture</i> and not the element</b>, which is the half of CSS's
    ///         replaced-element model that is deliberately not here. A browser lets an
    ///         <c>&lt;img&gt;</c> with no width take its box from the intrinsic size; this control
    ///         still takes its box entirely from <c>width</c>, <c>height</c> and <c>aspect-ratio</c>,
    ///         because sizing from content needs a measure hook the control has no equivalent of. So
    ///         an unsized <c>Image</c> is still a zero-height box, and this changes what is drawn
    ///         inside a box rather than what the box is.
    ///     </para>
    ///     <para>
    ///         In the texture's own pixels rather than in UVs, unlike <see cref="SourceBorder" />:
    ///         what <c>object-fit</c> needs is a <i>ratio</i>, and a ratio in UVs is always 1:1.
    ///         ⚠ It is the size of the whole texture and not of <see cref="SourceRectangle" />'s cut —
    ///         the cut is applied to it here, so a sprite sheet's cell fits by the cell's ratio.
    ///     </para>
    /// </remarks>
    public Vector2 IntrinsicSize { get; set; }

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        if (Texture == 0) {
            return;
        }

        if (Border.IsEmpty || SourceBorder.IsEmpty) {
            var (destination, source) = Fitted(context.Bounds);

            // ⚠ Nothing is drawn rather than a degenerate quad. `object-fit: none` on a picture wider
            // than its box that `object-position` has pushed entirely outside it is a real
            // arrangement, and a zero- or negative-extent rectangle would reach the geometry builder
            // as a quad whose winding is the wrong way round.
            if (destination.Width > 0f && destination.Height > 0f) {
                context.DrawImage(destination, Texture, source: source);
            }

            return;
        }

        context.DrawNineSlice(
            context.Bounds,
            Texture,
            Border,
            SourceBorder,
            source: SourceRectangle,
            hollowCentre: HollowCentre
        );
    }

    /// <summary>Where the picture goes and which part of it shows, per CSS Images 3 § 5.5.</summary>
    /// <param name="box">The element's content box.</param>
    /// <returns>The rectangle to draw and the UV rectangle to sample.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One arrangement covers all five keywords, and the reason it can is that the answer
    ///         is always "place a rectangle, then clip it to the box".</b> The tempting shape is two
    ///         cases — shrink the <i>destination</i> for <c>contain</c>, narrow the <i>source</i> for
    ///         <c>cover</c> — and it is two chances to get the position arithmetic subtly different.
    ///         Here the concrete object size is placed in the box by <c>object-position</c> whether it
    ///         is bigger or smaller, the result is intersected with the box, and the surviving part is
    ///         mapped back into UV space. <c>none</c>, which can overflow on one axis and underfill on
    ///         the other at the same time, is the case that makes the two-branch version wrong.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The UV mapping is affine and therefore survives a flipped
    ///         <see cref="SourceRectangle" />.</b> A negative width is how this engine spells a
    ///         mirrored sample — <c>Viewport</c> flips vertically with one — and multiplying a
    ///         fraction of the placed rectangle by that width carries the sign through, so a flipped
    ///         video still fits by the same rule. Clamping the extents to be positive first is the
    ///         mistake that would silently un-flip it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>fill</c> returns the box and the whole cut without computing anything</b>, which
    ///         is not thrift: it is the value CSS gives when there is no intrinsic size at all, so it
    ///         has to be reachable without one — and it is the path every existing caller takes.
    ///     </para>
    /// </remarks>
    (Rectangle Destination, Rectangle Source) Fitted(Rectangle box) {
        if (IntrinsicSize.X <= 0f || IntrinsicSize.Y <= 0f || box.Width <= 0f || box.Height <= 0f) {
            return (box, SourceRectangle);
        }

        if (fitProperty == 0) {
            fitProperty = Document.PropertyId("object-fit");
            positionProperty = Document.PropertyId("object-position");
        }

        // ⚠ The cut's own size, not the texture's. `SourceRectangle` is a fraction of the texture, so
        // a sprite sheet cell that is a third as wide as the sheet has a third of its intrinsic width
        // — and fitting by the sheet's ratio would letterbox every cell by the sheet's shape.
        // Absolute, so a flipped cut has the ratio of the picture rather than a negative one.
        var intrinsic = new Vector2(
            IntrinsicSize.X * MathF.Abs(SourceRectangle.Width),
            IntrinsicSize.Y * MathF.Abs(SourceRectangle.Height)
        );

        if (intrinsic.X <= 0f || intrinsic.Y <= 0f) {
            return (box, SourceRectangle);
        }

        var contain = MathF.Min(box.Width / intrinsic.X, box.Height / intrinsic.Y);

        var scale = Document.KeywordOf(Style, fitProperty) switch {
            "contain" => contain,
            "cover" => MathF.Max(box.Width / intrinsic.X, box.Height / intrinsic.Y),
            "none" => 1f,

            // ⚠ CSS defines this as the smaller of `none` and `contain` by CONCRETE SIZE, and because
            // both preserve the ratio that is exactly the smaller scale. Written as the comparison
            // rather than as a clamp so it still reads as the specification's rule.
            "scale-down" => MathF.Min(contain, 1f),

            // `fill`, an unrecognised keyword, and the property being absent, which are one answer:
            // `fill` is the initial value, so a stylesheet that says nothing says this.
            _ => 0f
        };

        if (scale <= 0f) {
            return (box, SourceRectangle);
        }

        var size = intrinsic * scale;

        // ⚠ Resolved against the SLACK and not against the box — see <c>UiDocument.PositionOf</c>.
        // Centred when nothing said, which is CSS's initial `50% 50%` and not a house convention.
        var slack = new Vector2(box.Width - size.X, box.Height - size.Y);

        var offset = Document.PositionOf(Style, positionProperty, slack.X, slack.Y)
            ?? new Vector2(slack.X * 0.5f, slack.Y * 0.5f);

        var placedX = box.X + offset.X;
        var placedY = box.Y + offset.Y;

        // The part of the placed picture the box lets through. CSS Images 3 § 5.5 clips the content to
        // the element's box, which is what makes `cover` and `none` show a crop rather than overflow.
        var left = MathF.Max(placedX, box.X);
        var top = MathF.Max(placedY, box.Y);
        var right = MathF.Min(placedX + size.X, box.X + box.Width);
        var bottom = MathF.Min(placedY + size.Y, box.Y + box.Height);

        if (right <= left || bottom <= top) {
            return (default, SourceRectangle);
        }

        // Back into UV space, through the cut rather than through the whole texture — so a sprite
        // sheet cell crops within its own cell.
        var u0 = (left - placedX) / size.X;
        var v0 = (top - placedY) / size.Y;
        var u1 = (right - placedX) / size.X;
        var v1 = (bottom - placedY) / size.Y;

        var source = new Rectangle(
            SourceRectangle.X + (u0 * SourceRectangle.Width),
            SourceRectangle.Y + (v0 * SourceRectangle.Height),
            (u1 - u0) * SourceRectangle.Width,
            (v1 - v0) * SourceRectangle.Height
        );

        return (new Rectangle(left, top, right - left, bottom - top), source);
    }

    int fitProperty;
    int positionProperty;
}

/// <summary>A key combination, drawn the way a menu draws one.</summary>
/// <remarks>
///     ⚠ <b>It shows a shortcut and does not listen for one.</b> Binding a key is
///     <c>Vixen.Input</c>'s job, and a label that registered a hotkey as a side effect of being drawn
///     would fire twice when a menu showed the same shortcut in two places.
/// </remarks>
public sealed partial class KeyboardShortcut : Control {
    /// <inheritdoc />
    protected override string TagName => "kbd";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The key this stands for. Setting it sets the text.</summary>
    [UiProperty(Changed = nameof(OnKeyChanged))]
    public partial InputKey Key { get; set; }

    /// <summary>The modifiers. Setting them sets the text.</summary>
    [UiProperty(Changed = nameof(OnModifiersChanged))]
    public partial ModifierKeys Modifiers { get; set; }

    /// <summary>How every shortcut in the process is written.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One property over <see cref="ShortcutFormat.Formatter" />, not a second copy
    ///         of it.</b> The formatter is process-wide because a shortcut is drawn by menus, by
    ///         toolbar tooltips and by the command palette, and two settable statics would be two
    ///         answers to the same question — an application that replaced this one and a menu that
    ///         read the other would disagree about how the same chord is written. This is the
    ///         spelling that already exists in every caller; the state is in <c>Vixen.Ui</c>,
    ///         because writing a chord down needs neither an element nor a document.
    ///     </para>
    ///     <para>
    ///         Defaulted, through there, to <see cref="Describe" />, which is deliberately not
    ///         platform-adapted: <c>Vixen.Ui</c> sits below <c>Vixen.Platform</c> and does not know
    ///         what it is running on. Knowing is the application's, and so is saying so.
    ///     </para>
    /// </remarks>
    public static Func<InputKey, ModifierKeys, string> Formatter {
        get => ShortcutFormat.Formatter;
        set => ShortcutFormat.Formatter = value;
    }

    /// <summary>Writes a combination the way a menu would.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What is held with it.</param>
    /// <returns>Something like <c>Ctrl+Shift+S</c>.</returns>
    /// <remarks>
    ///     <see cref="ShortcutFormat.Describe" />, kept here because it is what every call site
    ///     names and because <see cref="Formatter" /> defaults to it. The modifier order and the
    ///     key-name table are written down once, over there.
    /// </remarks>
    public static string Describe(InputKey key, ModifierKeys modifiers) => ShortcutFormat.Describe(key, modifiers);

    void OnKeyChanged(InputKey previous, InputKey current) => Text = Formatter(current, Modifiers);

    void OnModifiersChanged(ModifierKeys previous, ModifierKeys current) => Text = Formatter(Key, current);
}
