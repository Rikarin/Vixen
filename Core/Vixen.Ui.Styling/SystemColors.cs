// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Styling;

/// <summary>A CSS system colour: a name whose value the operating system owns rather than the sheet.</summary>
/// <remarks>
///     <para>
///         CSS Color 4 § 6.1's list, minus the three deprecated groups. Every one of these is a
///         <i>role</i> and not a colour — <c>Canvas</c> is "whatever this platform paints a page on"
///         — which is what makes them the only spelling in CSS that can follow a light/dark switch,
///         a high-contrast switch or an accent-colour change without a stylesheet saying anything.
///     </para>
///     <para>
///         ⚠ <b>The pairs travel together and reading one without the other is the mistake this
///         enum's ordering is arranged to make hard.</b> A background role is immediately followed by
///         its text role — <c>Canvas</c>/<c>CanvasText</c>, <c>ButtonFace</c>/<c>ButtonText</c>,
///         <c>Field</c>/<c>FieldText</c>, <c>Highlight</c>/<c>HighlightText</c>,
///         <c>Mark</c>/<c>MarkText</c>, <c>AccentColor</c>/<c>AccentColorText</c> — because a
///         forced palette guarantees contrast <i>within</i> a pair and guarantees nothing across
///         two. Substituting <c>Canvas</c> behind <c>ButtonText</c> is how a high-contrast theme
///         produces invisible text out of two colours the platform chose.
///     </para>
/// </remarks>
public enum SystemColor : byte {
    /// <summary>The background of application content.</summary>
    Canvas,

    /// <summary>Text on <see cref="Canvas" />.</summary>
    CanvasText,

    /// <summary>Text of a hyperlink that has not been followed.</summary>
    LinkText,

    /// <summary>The face of a push button.</summary>
    ButtonFace,

    /// <summary>Text on <see cref="ButtonFace" />.</summary>
    ButtonText,

    /// <summary>The border of a push button.</summary>
    ButtonBorder,

    /// <summary>The background of an input field.</summary>
    Field,

    /// <summary>Text in a <see cref="Field" />.</summary>
    FieldText,

    /// <summary>The background of selected text.</summary>
    Highlight,

    /// <summary>Text on <see cref="Highlight" />.</summary>
    HighlightText,

    /// <summary>Text of a control that is disabled.</summary>
    GrayText,

    /// <summary>The background of marked or found text.</summary>
    Mark,

    /// <summary>Text on <see cref="Mark" />.</summary>
    MarkText,

    /// <summary>The user's chosen accent colour.</summary>
    AccentColor,

    /// <summary>Text on <see cref="AccentColor" />.</summary>
    AccentColorText
}

/// <summary>What the platform fills the <see cref="SystemColor" /> roles with, right now.</summary>
/// <remarks>
///     <para>
///         <b>A mutable object rather than a value, and that is the whole point of it.</b> A sheet
///         that writes <c>color: CanvasText</c> is asking a question whose answer changes while the
///         application runs — the user switches to dark, turns high contrast on, picks a different
///         accent — and <c>StyleEngine.SetMedia</c> deliberately does not reload sheets when that
///         happens. So the substitution cannot be baked into the parsed value at load time; the
///         parsed value has to be able to come out different tomorrow from the same interned text.
///     </para>
///     <para>
///         ⚠ <b><see cref="Revision" /> is how a cache knows, and it is a counter rather than an
///         event for a reason.</b> <see cref="StyleValueParser" /> caches a parse per interned value
///         id and is created in eight places in this repository; wiring a change notification to each
///         would have meant eight subscriptions to unsubscribe. A counter compared on the way into
///         the cache costs one integer compare per parse and cannot leak.
///     </para>
///     <para>
///         ⚠ <b>Values are linear, like every other <see cref="StyleValue" /> colour</b>, and the
///         factories below therefore write sRGB bytes and convert. Handing this class an sRGB
///         <see cref="Color4" /> produces a palette that is visibly too bright and nothing will say
///         so — see <see cref="Color.ToLinear" />.
///     </para>
/// </remarks>
public sealed class SystemPalette {
    const int Count = (int)SystemColor.AccentColorText + 1;

    static readonly string[] Names = [
        "Canvas", "CanvasText", "LinkText", "ButtonFace", "ButtonText", "ButtonBorder",
        "Field", "FieldText", "Highlight", "HighlightText", "GrayText", "Mark", "MarkText",
        "AccentColor", "AccentColorText"
    ];

    static readonly Dictionary<string, SystemColor> Lookup = Build();

    readonly Color4[] entries = new Color4[Count];
    readonly Color4[] supplied = new Color4[Count];
    readonly bool[] fromPlatform = new bool[Count];

    /// <summary>Creates a palette holding the light defaults.</summary>
    public SystemPalette() {
        Reset(Light);
    }

    /// <summary>How many times any entry has changed.</summary>
    /// <remarks>
    ///     Starts at zero and is bumped by every <see cref="Set" /> that actually changes a colour. A
    ///     cache that stores this alongside a parsed value can tell in one compare whether the value
    ///     is still the one this palette would produce.
    /// </remarks>
    public int Revision { get; private set; }

    /// <summary>Reads one role.</summary>
    /// <param name="colour">The role.</param>
    /// <returns>Its colour, linear.</returns>
    public Color4 this[SystemColor colour] => entries[(int)colour];

    /// <summary>The light defaults, as sRGB bytes.</summary>
    /// <remarks>
    ///     Chromium's, which are the ones a stylesheet author has most likely seen. They are
    ///     deliberately not this engine's theme tokens: a system colour that answered the sheet's own
    ///     palette back would be an elaborate way of writing the token.
    /// </remarks>
    public static ReadOnlySpan<uint> Light =>
    [
        0xFFFFFF, 0x000000, 0x0000EE, 0xEFEFEF, 0x000000, 0x767676,
        0xFFFFFF, 0x000000, 0xB4D5FE, 0x000000, 0x808080, 0xFFFF00, 0x000000,
        0x0075FF, 0xFFFFFF
    ];

    /// <summary>The dark defaults, as sRGB bytes.</summary>
    public static ReadOnlySpan<uint> Dark =>
    [
        0x121212, 0xFFFFFF, 0x9E9EFF, 0x6B6B6B, 0xFFFFFF, 0x6B6B6B,
        0x3B3B3B, 0xFFFFFF, 0x3367D1, 0xFFFFFF, 0xAAAAAA, 0xFFFF00, 0x000000,
        0x4E9BFF, 0x000000
    ];

    /// <summary>The forced-colours defaults, as sRGB bytes.</summary>
    /// <remarks>
    ///     ⚠ <b>Windows' High Contrast Black, and it is a <i>fallback</i> rather than a description of
    ///     any user's machine.</b> A host that can read the platform's high-contrast palette should
    ///     write those colours instead; what this is for is the case the platform read fails or does
    ///     not exist, where a forced-colours mode with no palette to force to would be worse than
    ///     none — every colour in the frame would keep the value the sheet chose, which is exactly
    ///     what the user asked not to happen.
    /// </remarks>
    public static ReadOnlySpan<uint> HighContrast =>
    [
        0x000000, 0xFFFFFF, 0xFFFF00, 0x000000, 0xFFFFFF, 0xFFFFFF,
        0x000000, 0xFFFFFF, 0x1AEBFF, 0x000000, 0x3FF23F, 0xFFFF00, 0x000000,
        0x1AEBFF, 0x000000
    ];

    /// <summary>Looks a CSS system colour keyword up.</summary>
    /// <param name="name">The keyword, in any casing — CSS keywords are ASCII case-insensitive.</param>
    /// <param name="colour">Receives the role.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryParse(ReadOnlySpan<char> name, out SystemColor colour) =>
        Lookup.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out colour);

    /// <summary>The keyword a role is spelt with.</summary>
    /// <param name="colour">The role.</param>
    /// <returns>Its CSS spelling.</returns>
    public static string NameOf(SystemColor colour) => Names[(int)colour];

    /// <summary>Sets one role, until the next <see cref="Reset" />.</summary>
    /// <param name="colour">The role.</param>
    /// <param name="value">Its colour, linear.</param>
    /// <returns>Whether that changed anything.</returns>
    /// <remarks>
    ///     ⚠ <b>A one-off write and not a claim about the platform</b>: the next
    ///     <see cref="Reset" /> — an appearance change, a contrast change — puts the default table
    ///     back over it. A host that has read a colour <i>from the operating system</i> wants
    ///     <see cref="SetPlatform" /> instead, which survives.
    /// </remarks>
    public bool Set(SystemColor colour, Color4 value) {
        if (entries[(int)colour].Equals(value)) {
            return false;
        }

        entries[(int)colour] = value;
        Revision++;
        return true;
    }

    /// <summary>Fills one role from what the operating system says it is.</summary>
    /// <param name="colour">The role.</param>
    /// <param name="value">Its colour, linear.</param>
    /// <returns>Whether that changed anything.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The difference from <see cref="Set" /> is that this one outlives a
    ///         <see cref="Reset" />, and without that the platform read this class exists for could
    ///         not work at all.</b> A host reads its platform's palette once and on each change; the
    ///         default tables are re-applied on every appearance <i>and</i> every contrast change,
    ///         which arrive from a different place and on a different cadence. A host writing over
    ///         the top of <see cref="Reset" /> therefore holds its colours only until the user next
    ///         toggles dark mode — the same two-writers failure <c>PlatformInput.Repalette</c> is
    ///         arranged to avoid one level up, and it is not a failure a picture announces: the
    ///         window simply goes back to Chromium's blue.
    ///     </para>
    ///     <para>
    ///         So the platform's answers are held apart from the table and re-applied after it. A
    ///         role nobody has supplied is untouched by this and keeps following the tables, which is
    ///         what makes a partial read — an accent and a highlight and nothing else, which is all
    ///         <c>NSGlobalDomain</c> can give without AppKit — the normal case rather than a special
    ///         one.
    ///     </para>
    /// </remarks>
    public bool SetPlatform(SystemColor colour, Color4 value) {
        var index = (int)colour;

        supplied[index] = value;
        fromPlatform[index] = true;

        if (entries[index].Equals(value)) {
            return false;
        }

        entries[index] = value;
        Revision++;
        return true;
    }

    /// <summary>Gives one role back to the default tables.</summary>
    /// <param name="colour">The role.</param>
    /// <returns>Whether that changed anything.</returns>
    /// <remarks>
    ///     ⚠ <b>The role does not revert here; it reverts at the next <see cref="Reset" />.</b> This
    ///     class holds no memory of which table it was last filled from — light, dark or forced is
    ///     a question about the document, not about the palette — so inventing a value to fall back
    ///     to would be guessing at one of three. Forgetting the platform's answer is the whole of
    ///     what a host that has stopped being able to read one can honestly say.
    /// </remarks>
    public bool ClearPlatform(SystemColor colour) {
        if (!fromPlatform[(int)colour]) {
            return false;
        }

        fromPlatform[(int)colour] = false;
        return true;
    }

    /// <summary>Gives every role back to the default tables.</summary>
    /// <returns>Whether any role was being supplied.</returns>
    public bool ClearPlatform() {
        var any = false;

        for (var i = 0; i < Count; i++) {
            any |= fromPlatform[i];
            fromPlatform[i] = false;
        }

        return any;
    }

    /// <summary>Whether one role is being filled by the platform rather than by a default table.</summary>
    /// <param name="colour">The role.</param>
    /// <returns>Whether a host has supplied it.</returns>
    public bool IsFromPlatform(SystemColor colour) => fromPlatform[(int)colour];

    /// <summary>Replaces every role at once, from a table of sRGB bytes.</summary>
    /// <param name="srgb">Fifteen packed <c>0xRRGGBB</c> values, in <see cref="SystemColor" /> order.</param>
    /// <returns>Whether that changed anything.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One <see cref="Revision" /> bump for the whole table and not fifteen</b>, which
    ///         is what makes an appearance switch a single cache clear rather than fifteen of them
    ///         mid-frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A role the platform has supplied is <i>not</i> replaced</b> — see
    ///         <see cref="SetPlatform" />. The table is the fallback for the roles nobody has read
    ///         from the operating system, and a reset that overwrote the read ones would make an
    ///         appearance change the moment a real palette is lost.
    ///     </para>
    /// </remarks>
    public bool Reset(ReadOnlySpan<uint> srgb) {
        ArgumentOutOfRangeException.ThrowIfNotEqual(srgb.Length, Count, nameof(srgb));

        var changed = false;

        for (var i = 0; i < Count; i++) {
            var packed = srgb[i];

            var colour = fromPlatform[i]
                ? supplied[i]
                : new Color((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed).ToLinear();

            if (!entries[i].Equals(colour)) {
                entries[i] = colour;
                changed = true;
            }
        }

        if (changed) {
            Revision++;
        }

        return changed;
    }

    static Dictionary<string, SystemColor> Build() {
        var table = new Dictionary<string, SystemColor>(Count, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Count; i++) {
            table[Names[i]] = (SystemColor)i;
        }

        return table;
    }
}
