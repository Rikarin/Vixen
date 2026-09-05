// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;

namespace Vixen.Ui.Styling.Utilities;

/// <summary>How a dark theme is selected.</summary>
public enum DarkModeStrategy : byte {
    /// <summary>By the surface's <c>prefers-color-scheme</c>.</summary>
    Media,

    /// <summary>By a <c>dark</c> class on an ancestor.</summary>
    Class
}

/// <summary>A font size and the line height that goes with it.</summary>
/// <param name="Size">The font size in pixels.</param>
/// <param name="LineHeight">The line height in pixels.</param>
public readonly record struct FontSizeToken(float Size, float LineHeight);

/// <summary>The design tokens every generated utility is measured in.</summary>
/// <remarks>
///     <para>
///         The whole argument for a utility system is here rather than in the generator: "accent is
///         now teal" is one line of one file, and the editor's two hundred components follow. That is
///         what makes this worth building instead of hand-writing CSS, and it applies more strongly
///         to one monolithic application than it ever did to a website.
///     </para>
///     <para>
///         ⚠ <b>A token is a custom property in an <c>@theme</c> block, and that is Tailwind v4's
///         model rather than a resemblance to it.</b> Declaring <c>--color-mint-500</c> does two
///         things at once: it makes the variable available to hand-written CSS, and it tells the
///         generator that <c>bg-mint-500</c>, <c>text-mint-500</c> and <c>border-mint-500</c> exist.
///         There is no configuration file, which is the point — the thing that declares the token and
///         the thing that uses it are the same language.
///     </para>
///     <para>
///         ⚠ <b>This replaces a <c>vixen.ui.yaml</c> reader, and the YAML is deleted rather than
///         supported.</b> The editor's copy of it declared no colours of its own: every entry was a
///         <c>var(--…)</c> pointing at a property <c>EditorTheme</c> put on the root, which is
///         <c>@theme</c> with an extra file in front of it. The two limitations that file carried in
///         its own comments — a radius that could not be a <c>var()</c>, and an opacity modifier that
///         dropped — were both the same bug: a token store that held <i>parsed values</i> where the
///         theme held <i>references</i>. Under <c>@theme</c> a token is text in a stylesheet and the
///         cascade resolves it, so the class of bug is gone rather than fixed.
///     </para>
///     <para>
///         ⚠ <b>Every namespace has a shipped default, and clearing one is how a project opts out.</b>
///         <see cref="CreateDefault" /> is Tailwind v4.3.3's own palette and scales, in oklch, so a
///         game writing <c>bg-blue-500</c> needs no theme file at all. A project's own
///         <c>@theme</c> layers over it; <c>--color-*: initial;</c> empties the colour namespace
///         before the project's own colours land, and <c>--*: initial;</c> empties every one.
///     </para>
/// </remarks>
public sealed class ThemeTokens {
    /// <summary>The key a namespace uses for its unqualified form.</summary>
    /// <remarks>
    ///     ⚠ <b>In a stylesheet it is spelled by leaving the suffix off: <c>--shadow</c> is the
    ///     <c>DEFAULT</c> shadow and <c>--shadow-lg</c> is the large one.</b> The constant survives
    ///     the move from YAML because the <i>resolver</i> uses it — a bare <c>shadow</c> class has an
    ///     empty value and looks the token up under this key — so the two spellings meet here rather
    ///     than in every family that has an unqualified form.
    /// </remarks>
    public const string DefaultKey = "DEFAULT";

    /// <summary>How many pixels one <c>rem</c> is worth when a token is resolved.</summary>
    /// <remarks>
    ///     ⚠ <b>Sixteen, fixed, and resolved at generate time rather than carried.</b> v4 writes every
    ///     length in this file's namespaces as a <c>rem</c> and lets the browser scale it against the
    ///     root font size. Vixen has no root font size to scale against — a tool window is not a page,
    ///     and the one thing a panel <i>is</i> sized by is the dock that holds it — so a token that
    ///     stayed relative would be a number nothing downstream could turn into a pixel. Resolving
    ///     here means <c>--spacing: 0.25rem</c> and <c>--spacing: 4px</c> are the same token, which is
    ///     what lets v4's file be transcribed rather than rewritten.
    /// </remarks>
    public const float PixelsPerRem = 16f;

    static readonly char[] Whitespace = [' ', '\t', '\r', '\n', '\f'];

    static string? defaultSheet;

    /// <summary>Colours, keyed by their utility suffix — <c>surface-1</c>, <c>blue-500</c>.</summary>
    public Dictionary<string, string> Colors { get; } = new(StringComparer.Ordinal);

    /// <summary>Corner radii, keyed by suffix, as the CSS length they stand for.</summary>
    /// <remarks>
    ///     ⚠ <b>A string, and it used to be a <c>float</c>.</b> That one type was the whole of the
    ///     "one place the token model does not reach" the editor's theme file complained about:
    ///     <c>EditorTheme</c> declares <c>--radius-panel</c>, <c>--radius-control</c> and
    ///     <c>--radius-row</c>, and a dictionary of numbers rejected <c>var(--radius-row)</c> with a
    ///     diagnostic rather than storing it — so the choice was a radius written in two files or no
    ///     radius tokens at all, and the editor chose the second. Text stores a reference, a
    ///     <c>calc()</c> and a percentage as readily as a number, which is the property every other
    ///     namespace here already had.
    /// </remarks>
    public Dictionary<string, string> Radius { get; } = new(StringComparer.Ordinal);

    /// <summary>Shadows, as the <c>box-shadow</c> text they stand for, keyed by suffix.</summary>
    /// <remarks>
    ///     Whole declarations rather than a set of numbers to assemble, because a shadow is a
    ///     designed thing: the offset, the blur and the alpha are chosen together to read as one
    ///     height above the surface, and a scale that let them be picked apart would invite exactly
    ///     the combinations that do not.
    /// </remarks>
    public Dictionary<string, string> Shadow { get; } = new(StringComparer.Ordinal);

    /// <summary>Drop shadows, as the <c>drop-shadow()</c> arguments they stand for, keyed by suffix.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The function's <i>arguments</i> and not the function, which is where this differs
    ///         from <see cref="Shadow" /> beside it.</b> A <c>box-shadow</c> token is a whole
    ///         declaration because that property takes one; a <c>drop-shadow</c> is one item of
    ///         <c>filter</c>'s ordered list, and the list is assembled by
    ///         <c>UtilityComposition.Filter</c>. So the token holds <c>0 4px 4px rgb(0 0 0 / 0.15)</c>
    ///         and the assembler wraps it — the same division <c>--tw-blur</c> already makes, and for
    ///         the same reason: a fragment holding <c>drop-shadow(…)</c> whole could have no initial
    ///         value that composes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A namespace of its own and not a member of <see cref="Shadow" />, even though a
    ///         browser would accept either text in either place.</b> They are different scales in v4
    ///         and they have to be: a <c>box-shadow</c> may be a comma-separated list and several of
    ///         the shipped ones are, while <c>filter</c>'s items are separated by spaces and a comma
    ///         in one would invalidate the whole declaration. <c>--drop-shadow-*</c> read as a
    ///         <c>--shadow-*</c> would take the whole element's filter down with it.
    ///     </para>
    /// </remarks>
    public Dictionary<string, string> DropShadow { get; } = new(StringComparer.Ordinal);

    /// <summary>Font sizes, keyed by suffix.</summary>
    public Dictionary<string, FontSizeToken> FontSize { get; } = new(StringComparer.Ordinal);

    /// <summary>Font weights, keyed by suffix.</summary>
    public Dictionary<string, float> FontWeight { get; } = new(StringComparer.Ordinal);

    /// <summary>Font stacks, keyed by suffix — <c>sans</c>, <c>mono</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Carried and not yet consumed by a family.</b> <c>font-*</c> in this engine resolves a
    ///     <i>weight</i>; a stack needs a <c>font-family</c> that can pick the first name the face
    ///     registry knows, which is an engine task rather than a token one. Until then these reach a
    ///     hand-written rule through <c>var(--font-mono)</c> and reach no utility, which is why the
    ///     namespace is here rather than left out — a token nothing declares cannot be referenced at
    ///     all, where a token no utility reads can.
    /// </remarks>
    public Dictionary<string, string> FontFamily { get; } = new(StringComparer.Ordinal);

    /// <summary>Breakpoint widths in pixels, keyed by variant name.</summary>
    public Dictionary<string, float> Screens { get; } = new(StringComparer.Ordinal);

    /// <summary>Blur radii in pixels, keyed by suffix — <c>sm</c>, <c>2xl</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>A named scale where this engine had only an arithmetic one, and the difference is
    ///     which classes a Tailwind user can write.</b> <c>blur-*</c> resolved through the spacing
    ///     base, so <c>blur-8</c> worked and <c>blur-md</c> — the spelling v4 has and the only one
    ///     v4 has — resolved to nothing at all. That is not the v3-to-v4 step shift doc 43 § C2
    ///     went looking for; it is a namespace that was never shipped, and the shift is what makes
    ///     the numbers here <i>look</i> like an off-by-one against v3's.
    ///     <para>
    ///         The arithmetic form is kept beside it rather than replaced: <c>blur-2</c> is not a
    ///         class v4 has, but it is one this engine's own documentation writes and it costs
    ///         nothing to answer. The named step wins where both could.
    ///     </para>
    /// </remarks>
    public Dictionary<string, float> Blur { get; } = new(StringComparer.Ordinal);

    /// <summary>Container-query widths in pixels, keyed by variant name.</summary>
    /// <remarks>
    ///     ⚠ <b>A different set of numbers under the same names as <see cref="Screens" />, and that
    ///     is the whole reason the namespace has to exist rather than <c>@sm:</c> reading the
    ///     breakpoints.</b> <c>sm</c> is a 40 rem <i>window</i> and <c>@sm</c> is a 24 rem
    ///     <i>box</i>; a dockable panel is a few hundred pixels wide, so every container variant
    ///     driven off the breakpoint scale would be correct CSS with a threshold nothing in an
    ///     editor ever reaches — a query that never matches, which nothing warns about.
    ///     <para>
    ///         The scale runs the other way round from the breakpoints too: it starts at
    ///         <c>3xs</c> and there is no <c>2xl</c> window to anchor it, because the sizes a
    ///         <i>card</i> comes in are not the sizes a screen comes in.
    ///     </para>
    /// </remarks>
    public Dictionary<string, float> Containers { get; } = new(StringComparer.Ordinal);

    /// <summary>Every custom property the theme declares, by full name, as it should be emitted.</summary>
    /// <remarks>
    ///     What <see cref="RootRuleFor" /> writes out, and the half of <c>@theme</c> the typed
    ///     dictionaries above cannot represent: a namespace no family reads is still a variable a
    ///     hand-written rule can say <c>var(…)</c> against.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Variables => variables;

    readonly Dictionary<string, string> variables = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> sizes = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> heights = new(StringComparer.Ordinal);
    readonly Dictionary<string, float> ratios = new(StringComparer.Ordinal);

    /// <summary>The spacing unit. <c>p-4</c> is four of these.</summary>
    /// <remarks>
    ///     One number rather than a table of named steps, which is the choice that makes the spacing
    ///     scale <i>arithmetic</i>: <c>p-4</c> is twice <c>p-2</c> and everyone can see it. A named
    ///     scale reads better in a design tool and worse in a stylesheet, because nothing about
    ///     <c>p-md</c> says whether it is bigger than <c>p-sm</c> by a little or a lot.
    /// </remarks>
    public float SpacingBase { get; set; } = 4f;

    /// <summary>How a dark theme is selected.</summary>
    /// <remarks>
    ///     <c>--dark-mode: class;</c> in an <c>@theme</c> block. Vixen's own namespace rather than
    ///     v4's — v4 spells this as a <c>@custom-variant</c>, which this engine has no equivalent of,
    ///     and a token is the nearest true thing rather than a half-built variant mechanism.
    /// </remarks>
    public DarkModeStrategy DarkMode { get; set; } = DarkModeStrategy.Media;

    /// <summary>Everything that could not be read, and why.</summary>
    public List<string> Diagnostics { get; } = [];

    /// <summary>The engine's shipped defaults: Tailwind v4.3.3's palette and scales, in oklch.</summary>
    /// <returns>A fresh set of tokens, owned by the caller.</returns>
    /// <remarks>
    ///     ⚠ <b>A new instance each call, deliberately.</b> The dictionaries are mutable and the whole
    ///     mechanism is "layer your own over the default", so a shared singleton would let one
    ///     project's <c>@theme</c> reach into the next one's — which inside a build step running
    ///     several projects in one process is not hypothetical.
    /// </remarks>
    public static ThemeTokens CreateDefault() {
        var tokens = new ThemeTokens();
        tokens.Apply(DefaultSheet());
        return tokens;
    }

    /// <summary>Reads a project's <c>@theme</c> over the shipped defaults.</summary>
    /// <param name="css">A stylesheet containing zero or more <c>@theme</c> blocks.</param>
    /// <returns>The tokens.</returns>
    public static ThemeTokens Parse(string css) {
        ArgumentNullException.ThrowIfNull(css);

        var tokens = CreateDefault();
        tokens.Apply(css);
        return tokens;
    }

    /// <summary>Layers one stylesheet's <c>@theme</c> blocks over what is already here.</summary>
    /// <param name="css">The stylesheet. Anything outside an <c>@theme</c> block is ignored.</param>
    /// <remarks>
    ///     ⚠ <b>The block's modifiers are accepted and ignored.</b> v4 writes <c>@theme default</c>,
    ///     <c>@theme inline</c>, <c>@theme static</c> and <c>@theme reference</c>, and all four are
    ///     about <i>emission</i> — whether the variable reaches the output, whether a utility inlines
    ///     its value instead of referencing it. This generator resolves tokens at build time and emits
    ///     the variables a sheet actually references, so every one of those questions is already
    ///     answered differently and by construction. Refusing the modifier would make v4's own file
    ///     unreadable for a distinction that does not exist here.
    /// </remarks>
    public void Apply(string css) {
        ArgumentNullException.ThrowIfNull(css);

        foreach (var block in Blocks(css)) {
            foreach (var (name, value) in Declarations(block)) {
                Set(name, value);
            }
        }
    }

    /// <summary>Removes every <c>@theme</c> block from a stylesheet.</summary>
    /// <param name="css">The stylesheet.</param>
    /// <returns>The stylesheet without them.</returns>
    /// <remarks>
    ///     ⚠ <b>Because <c>@theme</c> is a build-time construct and the cascade has never heard of
    ///     it.</b> ExCSS hands an unknown at-rule back unparsed and <see cref="StyleSheetLoader" />
    ///     drops it with a diagnostic, so a sheet that reached the engine with its <c>@theme</c> still
    ///     in it would work — and would report a diagnostic on every load, for a block whose entire
    ///     content has already been turned into the <c>root</c> rule beside it.
    /// </remarks>
    public static string Strip(string css) {
        ArgumentNullException.ThrowIfNull(css);

        if (css.IndexOf("@theme", StringComparison.Ordinal) < 0) {
            return css;
        }

        var text = new StringBuilder(css.Length);
        var index = 0;

        while (index < css.Length) {
            var start = css.IndexOf("@theme", index, StringComparison.Ordinal);

            if (start < 0) {
                text.Append(css, index, css.Length - index);
                break;
            }

            var open = css.IndexOf('{', start);

            if (open < 0 || !TryClose(css, open, out var close)) {
                text.Append(css, index, css.Length - index);
                break;
            }

            text.Append(css, index, start - index);
            index = close + 1;
        }

        return text.ToString();
    }

    /// <summary>The <c>root</c> rule declaring every theme variable a stylesheet references.</summary>
    /// <param name="css">The stylesheet the variables have to serve.</param>
    /// <returns>A <c>root { … }</c> rule, or the empty string when nothing is referenced.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Referenced rather than all of them, and the closure is transitive.</b> The shipped
    ///         default is 347 declarations; emitting it whole would put three hundred custom
    ///         properties on the root of every document to serve the handful a sheet actually says
    ///         <c>var()</c> against, and every one of them is a string the cascade interns and a
    ///         substitution the resolver walks past. A theme variable whose own value references
    ///         another — <c>--color-accent: var(--accent)</c>, which is exactly the shape the editor's
    ///         palette is made of — pulls its target in with it, which is why this is a closure and
    ///         not a filter.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>root</c> and not <c>:root</c>.</b> The root of a Vixen document is an element
    ///         whose tag is literally <c>root</c>, which is what <c>EditorTheme</c>'s own token block
    ///         selects, and the pseudo-class spelling would compile to a selector matching nothing.
    ///     </para>
    /// </remarks>
    public string RootRuleFor(string css) {
        ArgumentNullException.ThrowIfNull(css);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();

        foreach (var name in References(css)) {
            Want(name);
        }

        while (pending.Count > 0) {
            foreach (var name in References(variables[pending.Dequeue()])) {
                Want(name);
            }
        }

        if (wanted.Count == 0) {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.AppendLine("/* The theme variables this sheet references, and the ones those reference. */");
        text.AppendLine("root {");

        foreach (var name in wanted.Order(StringComparer.Ordinal)) {
            text.Append("    ").Append(name).Append(": ").Append(variables[name]).AppendLine(";");
        }

        text.AppendLine("}");

        return text.ToString();

        // ⚠ **A token whose value references its own name is an alias, and emitting it would erase
        // what it aliases.** `--radius-row: var(--radius-row)` is how a theme says "this token is
        // whatever the document already declares under that name" — the editor's whole palette is
        // written that way, so that `bg-surface` and a hand-written `background: var(--surface)` are
        // one declaration and not two agreeing copies. Written back out into `root` it becomes a
        // self-reference that lands *after* the sheet declaring the real value, wins on source
        // order, and resolves to nothing: every radius in the editor would go square, from a rule
        // that looks like a tautology and reads like an accident.
        void Want(string name) {
            if (variables.TryGetValue(name, out var value)
                && !References(value).Contains(name, StringComparer.Ordinal)
                && wanted.Add(name)) {
                pending.Enqueue(name);
            }
        }
    }

    /// <summary>Looks a colour up.</summary>
    /// <param name="name">The utility suffix, such as <c>accent</c> or <c>blue-500</c>.</param>
    /// <param name="value">Receives the colour as written in the theme.</param>
    /// <returns>Whether it is a known colour.</returns>
    public bool TryGetColor(string name, out string value) {
        ArgumentNullException.ThrowIfNull(name);
        return Colors.TryGetValue(name, out value!);
    }

    /// <summary>Files one declaration into the namespace it belongs to.</summary>
    /// <remarks>
    ///     ⚠ <b>Order matters twice, and both are real collisions rather than tidiness.</b>
    ///     <c>--text-xs--line-height</c> has to be recognised before <c>--text-*</c> or it becomes a
    ///     font size called <c>xs--line-height</c>; <c>--font-weight-bold</c> has to be recognised
    ///     before <c>--font-*</c> or it becomes a font stack called <c>weight-bold</c>. Both would
    ///     have parsed cleanly and produced a utility that resolved to nonsense.
    /// </remarks>
    void Set(string name, string value) {
        var initial = value.Equals("initial", StringComparison.OrdinalIgnoreCase);

        if (name.EndsWith("-*", StringComparison.Ordinal) || name.Equals("--*", StringComparison.Ordinal)) {
            if (!initial) {
                Diagnostics.Add($"'{name}' can only be set to 'initial'; a namespace has no value of its own");
                return;
            }

            Clear(name.Equals("--*", StringComparison.Ordinal) ? "--" : name[..^1], exact: false);
            return;
        }

        if (initial) {
            Clear(name, exact: true);
            return;
        }

        variables[name] = value;

        if (Suffix(name, "--text-", "--line-height") is { } lineHeightOf) {
            SetLineHeight(lineHeightOf, value);
            return;
        }

        if (Suffix(name, "--color-") is { } colour) {
            Colors[colour] = value;
            return;
        }

        if (Suffix(name, "--font-weight-") is { } weight) {
            Number(name, value, FontWeight, weight);
            return;
        }

        if (Suffix(name, "--font-") is { } family) {
            FontFamily[family] = value;
            return;
        }

        // ⚠ `--text-shadow-*` is v4's own namespace and only *looks* like a member of `--text-*`.
        // Left to fall through it becomes a font size called `shadow-sm` whose value is a box-shadow,
        // which is a diagnostic on a file that is correct. No family reads it yet, so it stays in
        // `Variables` and stops here.
        if (!name.StartsWith("--text-shadow-", StringComparison.Ordinal) && Suffix(name, "--text-") is { } size) {
            SetFontSize(name, size, value);
            return;
        }

        if (Suffix(name, "--radius-") is { } radius) {
            // The one namespace kept as text. See the remarks on `Radius`: a length, a `var()` and a
            // `calc()` are all radii somebody writes, and only the first is a number.
            Radius[radius] = Length(value, out var pixels) ? Px(pixels) : value;
            return;
        }

        // ⚠ Before `--shadow-` and not after it, and the order is defensive rather than load-bearing:
        // `Suffix` matches a prefix, so `--drop-shadow-lg` does not begin with `--shadow-` and the two
        // cannot collide today. What it guards against is somebody widening `Suffix` to match
        // anywhere, which is the change that turned `--text-shadow-sm` into a font size — see the
        // exclusion two branches up, which is the same mistake already made once.
        if (Suffix(name, "--drop-shadow-") is { } cast) {
            DropShadow[cast] = value;
            return;
        }

        if (Suffix(name, "--shadow-") is { } shadow) {
            Shadow[shadow] = value;
            return;
        }

        if (Suffix(name, "--breakpoint-") is { } screen) {
            Length(name, value, Screens, screen);
            return;
        }

        if (Suffix(name, "--container-") is { } container) {
            Length(name, value, Containers, container);
            return;
        }

        if (Suffix(name, "--blur-") is { } blur) {
            Length(name, value, Blur, blur);
            return;
        }

        if (name.Equals("--spacing", StringComparison.Ordinal)) {
            if (Length(value, out var spacing)) {
                SpacingBase = spacing;
            } else {
                Diagnostics.Add($"'{name}' is not a length: {value}");
            }

            return;
        }

        if (name.Equals("--dark-mode", StringComparison.Ordinal)) {
            DarkMode = value.Equals("class", StringComparison.OrdinalIgnoreCase)
                ? DarkModeStrategy.Class
                : DarkModeStrategy.Media;
        }

        // Anything else is a namespace no family reads. It stays in `Variables`, so a hand-written
        // rule can reference it, and it produces no diagnostic — an author adding `--tracking-tight`
        // before the family exists is early rather than wrong.
    }

    void SetFontSize(string name, string key, string value) {
        if (!Length(value, out var size)) {
            Diagnostics.Add($"'{name}' is not a length: {value}");
            return;
        }

        sizes[key] = size;
        Recompute(key);
    }

    /// <summary>Files a <c>--text-*--line-height</c>, which is a ratio or a length and not both.</summary>
    /// <remarks>
    ///     ⚠ <b>The unit decides, which is CSS's own rule and not a heuristic.</b> <c>line-height: 1.5</c>
    ///     is one and a half times the font size; <c>line-height: 16px</c> is sixteen pixels. v4 writes
    ///     every one of its own as a ratio in a <c>calc()</c> — <c>calc(1.25 / 0.875)</c> — and a theme
    ///     transcribed from the editor's old pair syntax writes them as pixels. Both have to land.
    ///     <para>
    ///         ⚠ And the two are kept apart from the size rather than folded into
    ///         <see cref="FontSize" /> as they arrive, because <b>the order the two declarations are
    ///         written in is the author's business</b>. Folding early means a ratio filed before its
    ///         size has nowhere to sit but the line-height slot, where the size — arriving second —
    ///         cannot tell it from a length. Two side tables and one recomputation make both orders
    ///         the same.
    ///     </para>
    /// </remarks>
    void SetLineHeight(string key, string value) {
        if (Length(value, out var pixels) && !Unitless(value)) {
            heights[key] = pixels;
            ratios.Remove(key);
            Recompute(key);
            return;
        }

        if (!Ratio(value, out var ratio)) {
            Diagnostics.Add($"'--text-{key}--line-height' is neither a ratio nor a length: {value}");
            return;
        }

        ratios[key] = ratio;
        heights.Remove(key);
        Recompute(key);
    }

    void Recompute(string key) {
        var size = sizes.TryGetValue(key, out var found) ? found : 0f;

        // `1.4` is what "no opinion about line height" was worth under the YAML shape — `base: 14`
        // was allowed and meant exactly this — and it stays the answer for a size declared without a
        // partner, which v4 never does and a hand-written theme often will.
        var height = heights.TryGetValue(key, out var absolute) ? absolute
            : ratios.TryGetValue(key, out var ratio) ? MathF.Round(size * ratio)
            : MathF.Round(size * 1.4f);

        FontSize[key] = new FontSizeToken(size, height);
    }

    /// <summary>Empties a namespace, or one token, and keeps both representations in step.</summary>
    /// <remarks>
    ///     ⚠ <b>The typed dictionaries are pruned against <see cref="variables" /> rather than by
    ///     prefix of their own.</b> A key in <see cref="FontSize" /> is <c>xs</c> and the variable
    ///     that made it is <c>--text-xs</c>; re-deriving the full name in two places is how the two
    ///     halves drift, and the drift shows up as a token that clears from one and not the other —
    ///     a colour still generating a utility whose variable no longer exists.
    /// </remarks>
    void Clear(string prefix, bool exact) {
        if (exact) {
            variables.Remove(prefix);
        } else {
            foreach (var name in variables.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToList()) {
                variables.Remove(name);
            }
        }

        Drop(Colors, "--color-");
        Drop(Radius, "--radius-");
        Drop(Shadow, "--shadow-");
        Drop(DropShadow, "--drop-shadow-");
        Drop(FontWeight, "--font-weight-");
        Drop(FontFamily, "--font-");
        Drop(Screens, "--breakpoint-");
        Drop(Containers, "--container-");
        Drop(Blur, "--blur-");
        Drop(FontSize, "--text-");
        Drop(sizes, "--text-");
        Drop(heights, "--text-");
        Drop(ratios, "--text-");

        if (!variables.ContainsKey("--spacing")) {
            SpacingBase = 4f;
        }

        return;

        void Drop<T>(Dictionary<string, T> into, string space) {
            foreach (var key in into.Keys.Where(key => !variables.ContainsKey(Full(space, key))).ToList()) {
                into.Remove(key);
            }
        }
    }

    void Number(string name, string value, Dictionary<string, float> into, string key) {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            into[key] = number;
            return;
        }

        Diagnostics.Add($"'{name}' is not a number: {value}");
    }

    void Length(string name, string value, Dictionary<string, float> into, string key) {
        if (Length(value, out var pixels)) {
            into[key] = pixels;
            return;
        }

        Diagnostics.Add($"'{name}' is not a length: {value}");
    }

    /// <summary>The key a name carries inside a namespace, or null if it is not in it.</summary>
    /// <remarks>
    ///     ⚠ <b>The namespace's own name is <see cref="DefaultKey" />, which is what v4 spells
    ///     <c>DEFAULT</c>.</b> <c>--shadow: …</c> is what makes the bare class <c>shadow</c> mean
    ///     something, next to <c>--shadow-lg</c> making <c>shadow-lg</c> mean something, and the
    ///     resolver already looks the bare form up under that key. Without this the two spellings
    ///     would be unrelated tokens and the bare class would resolve to nothing, silently.
    /// </remarks>
    static string? Suffix(string name, string prefix, string? tail = null) {
        if (tail is null && name.Length == prefix.Length - 1 && prefix.StartsWith(name, StringComparison.Ordinal)) {
            return DefaultKey;
        }

        if (!name.StartsWith(prefix, StringComparison.Ordinal)) {
            return null;
        }

        var rest = name[prefix.Length..];

        if (tail is null) {
            return rest.Length == 0 ? null : rest;
        }

        return rest.EndsWith(tail, StringComparison.Ordinal) && rest.Length > tail.Length
            ? rest[..^tail.Length]
            : null;
    }

    /// <summary>The variable name a namespace and a key came from. The inverse of <see cref="Suffix" />.</summary>
    static string Full(string space, string key) =>
        key.Equals(DefaultKey, StringComparison.Ordinal) ? space[..^1] : space + key;

    static bool Unitless(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>Reads a token's length in pixels, resolving <c>rem</c> and bare numbers.</summary>
    static bool Length(string value, out float pixels) {
        pixels = 0f;

        var text = value.AsSpan().Trim();

        if (text.EndsWith("rem", StringComparison.OrdinalIgnoreCase)) {
            return Scalar(text[..^3], PixelsPerRem, out pixels);
        }

        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) {
            return Scalar(text[..^2], 1f, out pixels);
        }

        return Scalar(text, 1f, out pixels);

        static bool Scalar(ReadOnlySpan<char> number, float scale, out float pixels) {
            var parsed = float.TryParse(number.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out pixels);
            pixels *= scale;
            return parsed;
        }
    }

    /// <summary>Reads a unitless ratio, including v4's <c>calc(a / b)</c> spelling of one.</summary>
    /// <remarks>
    ///     ⚠ <b>A deliberate two-operand evaluator and not a <c>calc()</c> implementation.</b> The
    ///     only <c>calc()</c> v4's theme file contains is one division of two literals, written that
    ///     way so the ratio's derivation stays visible — <c>calc(1.25 / 0.875)</c> is "20px over
    ///     14px". Anything else is refused with a diagnostic rather than approximated, which is the
    ///     rule the selector compiler and the sheet loader both follow.
    /// </remarks>
    static bool Ratio(string value, out float ratio) {
        var text = value.Trim();

        if (text.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) && text.EndsWith(')')) {
            text = text[5..^1].Trim();

            foreach (var op in new[] { '/', '*' }) {
                var at = text.IndexOf(op);

                if (at < 0) {
                    continue;
                }

                if (float.TryParse(text[..at].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                    && float.TryParse(text[(at + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var right)
                    && (op == '*' || right != 0f)) {
                    ratio = op == '/' ? left / right : left * right;
                    return true;
                }

                ratio = 0f;
                return false;
            }
        }

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio);
    }

    static string Px(float value) => value.ToString("0.####", CultureInfo.InvariantCulture) + "px";

    /// <summary>Every <c>var(--name)</c> a piece of text references.</summary>
    static IEnumerable<string> References(string css) {
        var index = 0;

        while (index < css.Length) {
            var start = css.IndexOf("var(", index, StringComparison.Ordinal);

            if (start < 0) {
                yield break;
            }

            var open = start + 4;

            while (open < css.Length && char.IsWhiteSpace(css[open])) {
                open++;
            }

            var end = open;

            while (end < css.Length && (css[end] == '-' || css[end] == '_' || char.IsLetterOrDigit(css[end]))) {
                end++;
            }

            if (end > open + 2 && css[open] == '-' && css[open + 1] == '-') {
                yield return css[open..end];
            }

            index = start + 4;
        }
    }

    /// <summary>The bodies of every <c>@theme</c> block in a stylesheet, in source order.</summary>
    static IEnumerable<string> Blocks(string css) {
        var index = 0;

        while (index < css.Length) {
            var start = css.IndexOf("@theme", index, StringComparison.Ordinal);

            if (start < 0) {
                yield break;
            }

            var open = css.IndexOf('{', start);

            if (open < 0 || !TryClose(css, open, out var close)) {
                yield break;
            }

            yield return css[(open + 1)..close];
            index = close + 1;
        }
    }

    /// <summary>The brace matching <paramref name="open" />, comments and strings respected.</summary>
    static bool TryClose(string css, int open, out int close) {
        var depth = 0;

        for (var index = open; index < css.Length; index++) {
            var c = css[index];

            switch (c) {
                case '/' when index + 1 < css.Length && css[index + 1] == '*': {
                    var end = css.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? css.Length : end + 1;
                    break;
                }

                case '"':
                case '\'': {
                    index++;

                    while (index < css.Length && css[index] != c) {
                        index += css[index] == '\\' ? 2 : 1;
                    }

                    break;
                }

                case '{':
                    depth++;
                    break;

                case '}' when --depth == 0:
                    close = index;
                    return true;
            }
        }

        close = -1;
        return false;
    }

    /// <summary>The <c>name: value</c> pairs in a block's body, comments stripped.</summary>
    /// <remarks>
    ///     ⚠ <b>Split at the top level only.</b> <c>--font-sans</c> is a comma-separated stack and
    ///     <c>--shadow-md</c> is two shadows separated by a comma, so a naive split on <c>;</c> is
    ///     fine but a split on <c>,</c> would not be — and <c>calc(1 / 0.75)</c> means the depth has
    ///     to be tracked through parentheses rather than braces alone.
    /// </remarks>
    static IEnumerable<(string Name, string Value)> Declarations(string block) {
        var text = new StringBuilder(block.Length);

        for (var index = 0; index < block.Length; index++) {
            var c = block[index];

            if (c == '/' && index + 1 < block.Length && block[index + 1] == '*') {
                var end = block.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? block.Length : end + 1;
                continue;
            }

            text.Append(c);
        }

        var body = text.ToString();
        var depth = 0;
        var start = 0;

        for (var index = 0; index <= body.Length; index++) {
            if (index < body.Length) {
                switch (body[index]) {
                    case '(':
                    case '{':
                        depth++;
                        continue;
                    case ')':
                    case '}':
                        depth--;
                        continue;
                    case ';' when depth == 0:
                        break;
                    default:
                        continue;
                }
            }

            var declaration = body[start..index];
            start = index + 1;

            var colon = declaration.IndexOf(':');

            if (colon < 0) {
                continue;
            }

            var name = declaration[..colon].Trim(Whitespace);
            var value = declaration[(colon + 1)..].Trim(Whitespace);

            if (name.StartsWith("--", StringComparison.Ordinal) && value.Length > 0) {
                yield return (Collapse(name), Collapse(value));
            }
        }
    }

    /// <summary>Runs of whitespace to one space, because v4's file wraps long values over lines.</summary>
    static string Collapse(string value) {
        if (value.AsSpan().IndexOfAny('\n', '\r', '\t') < 0 && !value.Contains("  ", StringComparison.Ordinal)) {
            return value;
        }

        var text = new StringBuilder(value.Length);
        var space = false;

        foreach (var c in value) {
            if (char.IsWhiteSpace(c)) {
                space = true;
                continue;
            }

            if (space && text.Length > 0) {
                text.Append(' ');
            }

            space = false;
            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>The shipped default sheet, read once out of this assembly.</summary>
    /// <remarks>
    ///     ⚠ <b>A real <c>.vcss</c> embedded as a resource, not a <c>const string</c>.</b> Three
    ///     hundred and forty-seven declarations edited inside a C# string literal is the shape this
    ///     whole change exists to get away from — and the file is the thing a hot-reload watcher, a
    ///     diff and a colour picker can all read.
    /// </remarks>
    static string DefaultSheet() {
        if (defaultSheet is not null) {
            return defaultSheet;
        }

        var assembly = typeof(ThemeTokens).Assembly;
        const string name = "Vixen.Ui.Styling.Utilities.Theme.vixen.default.vcss";

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"the shipped theme '{name}' is not embedded in {assembly.GetName().Name}. "
                + "It is added by the .vcss glob in Vixen.Ui.targets.");

        using var reader = new StreamReader(stream);
        return defaultSheet = reader.ReadToEnd();
    }
}
