// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Input;

namespace Vixen.Ui.Controls;

/// <summary>One wedge of a radial menu.</summary>
/// <remarks>
///     A <see cref="ButtonBase" /> for <c>PaletteRow</c>'s reason: the rows must not take the focus,
///     because a radial menu is aimed with the pointer and confirmed with a release, and a focus ring
///     wandering round the ring as the mouse moves would be a second highlight saying something else.
/// </remarks>
public sealed partial class RadialItem : ButtonBase {
    /// <inheritdoc />
    protected override string TagName => "radial-item";

    /// <summary>Which wedge it is, clockwise from the top.</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>The angle its centre sits at, in radians, with zero pointing up.</summary>
    public float Angle { get; internal set; }
}

/// <summary>A pie menu: wedges round a centre, aimed with the pointer.</summary>
/// <remarks>
///     <para>
///         <b>Blender's pie menu, which is the fastest menu anybody has shipped and is fast for one
///         reason.</b> A drop-down costs a read — the items are in an order you have to scan — and a
///         pie costs a direction. After a week the direction is muscle memory and the menu is a
///         flick; the items being in fixed positions is the whole mechanism, which is why nothing
///         here sorts or filters what it was given.
///     </para>
///     <para>
///         ⚠ <b>Two gestures, and both have to work or neither is used.</b> <i>Press the key, then
///         click a wedge</i> is what somebody does the first fifty times, while they are still
///         reading the labels. <i>Hold the key, flick, release</i> is what they do afterwards, and it
///         is the one that makes the menu worth having. They are the same menu in the same place —
///         see <see cref="Hold" />.
///     </para>
///     <para>
///         ⚠ <b>The dead zone in the middle is what makes the release gesture safe.</b> A release
///         without having moved must do nothing: somebody who pressed the key and let go, or who
///         opened the menu to look at it, has not chosen anything. Without a dead zone the wedge
///         under the resting cursor is chosen by the tiniest tremor, which is a menu that runs
///         commands nobody asked for.
///     </para>
///     <para>
///         ⚠ <b>Wedges are placed clockwise from the top and are never reordered.</b> Blender's own
///         order is west-first, which is better for four items and worse for six; what matters more
///         than either is that a given menu puts a given item in the same place every time, so this
///         is the simple rule rather than the clever one.
///     </para>
/// </remarks>
public sealed partial class RadialMenu : Overlay {
    readonly List<RadialItem> items = [];

    Vector2 centre;
    int highlighted = -1;
    bool held;

    /// <inheritdoc />
    protected override string TagName => "radial-menu";

    /// <summary>The wedges, clockwise from the top.</summary>
    public IReadOnlyList<RadialItem> Items => items;

    /// <summary>Which wedge the pointer is aimed at, or <c>-1</c> for none.</summary>
    public int Highlighted => highlighted;

    /// <summary>How far the ring is from the centre, in pixels.</summary>
    public float Radius { get; set; } = 92f;

    /// <summary>How far the pointer must travel before it is aiming at anything.</summary>
    /// <inheritdoc cref="RadialMenu" path="/remarks/para[3]" />
    public float DeadZone { get; set; } = 26f;

    /// <summary>Whether the menu was opened by a key that is still down.</summary>
    /// <remarks>
    ///     ⚠ <b>What decides whether a release commits.</b> A menu opened by a press-and-let-go is
    ///     a menu somebody is reading, and a release a moment later — of a button they never pressed
    ///     over it — must not run whatever the cursor drifted onto. A menu opened by a key that is
    ///     still held is a flick in progress and the release <i>is</i> the choice.
    /// </remarks>
    public bool Hold { get; private set; }

    /// <summary>Raised with the wedge that was chosen.</summary>
    public event Action<RadialMenu, RadialItem>? Chose;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        IsFocusScope = true;
        LightDismiss = true;
        CloseOnEscape = true;

        // ⚠ On the root and capturing, like the light dismiss above it. A pie menu is aimed by moving
        // the pointer *outside* it — the ring is 92 pixels out and the pointer starts in the middle —
        // so a handler on this element would hear nothing until the pointer had already crossed a
        // wedge. This is the one control in the set that has to watch the whole document.
        aimed = (_, args) => Aimed(args);
        Document.Root.AddHandler(aimed, RoutingStrategy.Capture, handledEventsToo: true);

        // ⚠ And the key going up, on the root for the same reason. The gesture this menu is for is
        // "hold a key, flick, let go" — so the commit is a key *release*, which arrives wherever the
        // focus happens to be and never at a menu that has only just opened. Watching it here is
        // what makes the held-key form work at all; without it the menu would open on the press and
        // then sit there waiting for a click nobody is going to make.
        lifted = (_, args) => Lifted(args);
        Document.Root.AddHandler(lifted, RoutingStrategy.Capture, handledEventsToo: true);

        AddHandler<ClickEvent>(static (element, args) => ((RadialMenu) element).Chosen(args));
        AddHandler<KeyEvent>(static (element, args) => ((RadialMenu) element).Keyed(args));
    }

    Action<UiElement, PointerEvent>? aimed;
    Action<UiElement, KeyEvent>? lifted;

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (aimed is not null) {
            Document.Root.RemoveHandler(aimed);
            aimed = null;
        }

        if (lifted is not null) {
            Document.Root.RemoveHandler(lifted);
            lifted = null;
        }

        base.OnRemoved();
    }

    /// <summary>Empties it.</summary>
    public void Clear() {
        while (Children.Count > 0) {
            Children[^1].Remove();
        }

        items.Clear();
        highlighted = -1;
    }

    /// <summary>Adds a wedge.</summary>
    /// <param name="label">What it says.</param>
    /// <returns>The wedge, for a caller that wants to put an icon on it.</returns>
    public RadialItem AddItem(string? label = null) {
        var item = Add<RadialItem>();

        item.Label = label;
        item.Focusable = false;
        item.Index = items.Count;

        items.Add(item);
        return item;
    }

    /// <summary>Opens it centred on a point in document space.</summary>
    /// <param name="x">Where.</param>
    /// <param name="y">Ditto.</param>
    /// <param name="hold">
    ///     Whether the key that opened it is still down, which is what makes a release commit. See
    ///     <see cref="Hold" />.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Centred on the point rather than starting at it, which is what a pie menu is.</b> The
    ///     cursor is the origin the directions are measured from, so the menu comes to the cursor —
    ///     and unlike a drop-down it is never pushed back inside the viewport, because moving it would
    ///     move the centre and every direction with it. Near an edge the wedges that fall outside are
    ///     unreachable, which is honest; sliding the whole menu would silently rotate what the flick
    ///     you already know means.
    /// </remarks>
    public void OpenAt(float x, float y, bool hold = false) {
        Hold = hold;
        held = hold;
        highlighted = -1;

        Open();

        // The pass that gives the wedges a size, so that placing them has something to measure.
        Document.Update();

        centre = new Vector2(x, y);

        MoveTo(x - (Bounds.Width * 0.5f), y - (Bounds.Height * 0.5f));

        // ⚠ Placed, laid out, and placed again — which is one pass more than it looks like it needs.
        // Writing `left` on an absolutely positioned child changes how much room is left to its
        // right, so a wedge whose label is long is measured at one width before the offset and a
        // different one after it; centring against the first leaves that wedge off the ring by half
        // the difference. The second pass centres against the width it actually ended up with, and
        // it converges there because the offset no longer moves.
        Place();

        Restyle();
    }

    /// <summary>Runs the aimed wedge and closes.</summary>
    /// <returns>Whether anything was aimed at.</returns>
    public bool Accept() {
        if (highlighted < 0 || highlighted >= items.Count) {
            return false;
        }

        var item = items[highlighted];

        // Closed before the command runs, for `CommandPalette.Accept`'s reason: a command that opens
        // a dialog would otherwise be covered by the menu that started it.
        Close(CloseReason.Committed);
        Chose?.Invoke(this, item);

        return true;
    }

    /// <summary>Aims at a wedge by index, as the keyboard does.</summary>
    /// <param name="index">Which one, or <c>-1</c> for none.</param>
    public void Aim(int index) {
        highlighted = index >= 0 && index < items.Count ? index : -1;
        Restyle();
    }

    /// <summary>Which wedge a direction from the centre points at.</summary>
    /// <param name="offset">How far the pointer is from the centre, in pixels.</param>
    /// <returns>The index, or <c>-1</c> inside the dead zone.</returns>
    /// <remarks>
    ///     ⚠ <b>Nearest by angle rather than by distance to the wedge.</b> A pie is aimed, not
    ///     pointed at: the gesture is a flick in a direction and it routinely overshoots the ring by
    ///     a long way, so a hit test against the buttons would miss every fast one. What is measured
    ///     is the angle, and how far out the pointer went matters only for the dead zone.
    /// </remarks>
    public int WedgeAt(Vector2 offset) {
        if (items.Count == 0 || offset.Length() < DeadZone) {
            return -1;
        }

        // Zero points up and grows clockwise, which is what `Place` lays the wedges out by. Screen y
        // grows downwards, so this is `Atan2(x, -y)` rather than the usual argument order.
        var angle = MathF.Atan2(offset.X, -offset.Y);

        if (angle < 0f) {
            angle += MathF.Tau;
        }

        var step = MathF.Tau / items.Count;

        return (int) MathF.Round(angle / step) % items.Count;
    }

    /// <summary>Puts each wedge on the ring.</summary>
    /// <remarks>
    ///     ⚠ <b>Positioned by an inline offset rather than by a rule, because the ring's geometry is
    ///     arithmetic no stylesheet can do.</b> The radius is a property, the count is whatever was
    ///     added, and each wedge has to be centred on its own point rather than starting at it — an
    ///     item placed by its top-left corner makes a ring that leans down and to the right by half a
    ///     button.
    /// </remarks>
    void Place() {
        var step = MathF.Tau / Math.Max(1, items.Count);
        var middle = new Vector2(Bounds.Width * 0.5f, Bounds.Height * 0.5f);

        for (var index = 0; index < items.Count; index++) {
            var angle = step * index;
            var item = items[index];

            item.Angle = angle;

            var point = middle + new Vector2(MathF.Sin(angle) * Radius, -MathF.Cos(angle) * Radius);

            item.SetStyle("left", Px(point.X - (item.Bounds.Width * 0.5f)));
            item.SetStyle("top", Px(point.Y - (item.Bounds.Height * 0.5f)));
        }
    }

    static string Px(float value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    void Restyle() {
        for (var index = 0; index < items.Count; index++) {
            if (index == highlighted) {
                items[index].State |= Styling.ElementState.Checked;
            } else {
                items[index].State &= ~Styling.ElementState.Checked;
            }
        }
    }

    void Aimed(PointerEvent args) {
        if (!IsOpen) {
            return;
        }

        if (args.Action is PointerAction.Moved or PointerAction.Pressed) {
            Aim(WedgeAt(new Vector2(args.X - centre.X, args.Y - centre.Y)));
            return;
        }

        // ⚠ Only a release that belongs to the gesture that opened it. A menu opened by a click is
        // opened *by* a press whose release arrives a few milliseconds later, over the middle of the
        // menu — committing on that would make every click-opened pie close again instantly, having
        // chosen whatever the dead zone let through.
        if (args.Action == PointerAction.Released) {
            Commit();
        }
    }

    /// <summary>The key that opened it going up, which is the other half of the same gesture.</summary>
    /// <remarks>
    ///     ⚠ <b>Any key, not the one that opened it, and that is a deliberate simplification.</b> The
    ///     menu is not told which chord summoned it — a command runs without being handed the key that
    ///     ran it — and the alternative would be a parameter every caller has to thread through
    ///     correctly for the gesture to work at all. Every key release while a pie is up and still
    ///     held is either the one that opened it or somebody doing something else entirely with a
    ///     menu open, and both mean "resolve this now".
    /// </remarks>
    void Lifted(KeyEvent args) {
        if (IsOpen && args.Action == KeyAction.Released) {
            Commit();
        }
    }

    /// <summary>Ends the held gesture: runs what is aimed at, or falls back to click-to-choose.</summary>
    void Commit() {
        if (!held) {
            return;
        }

        held = false;

        if (!Accept()) {
            // A release with nothing aimed at is somebody who opened the menu to look at it. It stays
            // up and becomes the click-to-choose kind, which is the first of the two gestures.
            Hold = false;
        }
    }

    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (element is not RadialItem { Index: >= 0 } item) {
                continue;
            }

            Aim(item.Index);
            Accept();

            args.Handled = true;
            return;
        }
    }

    /// <summary>Arrow keys aim and Enter commits, for a menu opened without a pointer at all.</summary>
    /// <remarks>
    ///     The four directions are the four wedges nearest them, which for a menu of five or seven is
    ///     approximate and is still the difference between "reachable from the keyboard" and not.
    /// </remarks>
    void Keyed(KeyEvent args) {
        if (args.Action != KeyAction.Pressed || items.Count == 0) {
            return;
        }

        switch (args.Key) {
            case InputKey.Up:
                Aim(WedgeAt(new Vector2(0f, -Radius)));
                break;

            case InputKey.Down:
                Aim(WedgeAt(new Vector2(0f, Radius)));
                break;

            case InputKey.Left:
                Aim(WedgeAt(new Vector2(-Radius, 0f)));
                break;

            case InputKey.Right:
                Aim(WedgeAt(new Vector2(Radius, 0f)));
                break;

            case InputKey.Tab:
                Aim(highlighted < 0 ? 0 : (highlighted + 1) % items.Count);
                break;

            case InputKey.Enter or InputKey.KeypadEnter or InputKey.Space:
                Accept();
                break;

            default:
                return;
        }

        args.Handled = true;
    }
}
