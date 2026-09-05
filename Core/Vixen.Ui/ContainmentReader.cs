// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Layout;
using Vixen.Ui.Styling;

namespace Vixen.Ui;

/// <summary>Reads <c>contain</c> off a computed style, as the flags the layout store carries.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One reader for three callers, which is <see cref="OverflowReader" />'s argument one
///         property over.</b> The layout store needs the <c>size</c> half, the draw list and the hit
///         test need the <c>paint</c> half, and a property parsed in three places is how a box ends up
///         visibly clipped and invisibly clickable. <see cref="OverflowReader" /> folds the paint half
///         in itself, so painting, hit testing and stickiness all get it from the same sentence.
///     </para>
///     <para>
///         ⚠ <b>The value is a LIST of keywords, and that is why this cannot be a
///         <c>Dictionary&lt;int, T&gt;</c> lookup like every other keyword property here.</b>
///         <c>contain: layout paint</c> interns as one string, so the answer is computed from the
///         value's text — once per distinct interned value, cached on the id, because the set of
///         spellings a document uses is tiny and fixed while the number of elements reading them is
///         not.
///     </para>
///     <para>
///         ⚠ <b><c>style</c> parses and contributes no flag, and that is deliberate rather than
///         unfinished.</b> CSS Containment § 3.4 scopes counters and quotes; this engine has neither,
///         so the keyword is understood and inert. Refusing it instead would drop the whole
///         declaration — <c>contain: layout style</c> would stop containing layout — which is a
///         worse answer than the one CSS asks for.
///     </para>
/// </remarks>
sealed class ContainmentReader {
    readonly int contain;
    readonly NameTable values;
    readonly Dictionary<int, Containment> cache = [];

    /// <summary>Interns the property name and keeps the table its values are interned in.</summary>
    /// <param name="properties">The table property names are interned in.</param>
    /// <param name="values">The table declaration values are interned in.</param>
    public ContainmentReader(NameTable properties, NameTable values) {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(values);

        contain = properties.Intern("contain");
        this.values = values;
    }

    /// <summary>What a style contains.</summary>
    /// <param name="style">The element's computed style.</param>
    /// <returns>The flags, or <see cref="Containment.None" /> when nothing is declared.</returns>
    public Containment Of(ComputedStyle style) {
        if (!style.TryGet(contain, out var id)) {
            return Containment.None;
        }

        if (cache.TryGetValue(id, out var cached)) {
            return cached;
        }

        var parsed = Parse(values.NameOf(id));
        cache[id] = parsed;

        return parsed;
    }

    /// <summary>The keywords of one declaration, folded together.</summary>
    /// <remarks>
    ///     ⚠ <b>An unrecognised word drops the whole declaration</b>, which is what CSS does with a
    ///     value it cannot parse and is the only reading that keeps a future keyword from being read
    ///     as a subset of itself. <c>strict</c> and <c>content</c> are the two shorthand spellings and
    ///     CSS forbids either beside anything else, so they are accepted alone.
    /// </remarks>
    internal static Containment Parse(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return Containment.None;
        }

        var result = Containment.None;
        var words = 0;

        foreach (var range in text.AsSpan().Split(' ')) {
            var word = text.AsSpan()[range].Trim();
            if (word.IsEmpty) {
                continue;
            }

            words++;

            // ⚠ `strict` is `size layout paint style` and `content` is `layout paint style`, both
            // minus the `style` this engine measures as inert — so `contain-content` is fully real
            // here and `contain-strict` needs nothing but the size half.
            if (word.Equals("strict", StringComparison.OrdinalIgnoreCase)) {
                return words == 1 ? Containment.Size | Containment.Layout | Containment.Paint : Containment.None;
            }

            if (word.Equals("content", StringComparison.OrdinalIgnoreCase)) {
                return words == 1 ? Containment.Layout | Containment.Paint : Containment.None;
            }

            if (word.Equals("none", StringComparison.OrdinalIgnoreCase)) {
                return Containment.None;
            }

            if (!TryFlag(word, out var flag)) {
                return Containment.None;
            }

            result |= flag;
        }

        return result;

        static bool TryFlag(ReadOnlySpan<char> word, out Containment flag) {
            if (word.Equals("size", StringComparison.OrdinalIgnoreCase)) {
                flag = Containment.Size;
                return true;
            }

            if (word.Equals("inline-size", StringComparison.OrdinalIgnoreCase)) {
                flag = Containment.InlineSize;
                return true;
            }

            if (word.Equals("layout", StringComparison.OrdinalIgnoreCase)) {
                flag = Containment.Layout;
                return true;
            }

            if (word.Equals("paint", StringComparison.OrdinalIgnoreCase)) {
                flag = Containment.Paint;
                return true;
            }

            // Understood, and it contributes nothing. See the remark on the type: refusing it would
            // drop the declaration it is written in, which is a worse answer than an inert keyword.
            flag = Containment.None;

            return word.Equals("style", StringComparison.OrdinalIgnoreCase);
        }
    }
}
