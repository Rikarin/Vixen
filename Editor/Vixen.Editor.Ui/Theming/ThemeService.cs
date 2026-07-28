// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Yaml;
using Vixen.Ui;
using Vixen.Ui.Styling;

namespace Vixen.Editor.Ui;

/// <summary>Which of the two themes the editor is showing.</summary>
public enum ThemeMode : byte {
    /// <summary>Dark text on light chrome.</summary>
    Light,

    /// <summary>The other one.</summary>
    Dark
}

/// <summary>Switches between light and dark, and applies the user's own token overrides.</summary>
/// <remarks>
///     <para>
///         <b>Theming is nine custom properties on the root, and this class writes one class name.</b>
///         That is the whole of it, and it is what doc 11 means by "driven entirely by design
///         tokens": there is no theme object, no palette type and no per-control colour, so a theme
///         is a set of values and switching one is a restyle rather than a rebuild.
///     </para>
///     <para>
///         <b>A user-editable theme file is a natural consequence rather than a feature</b> — also
///         doc 11's words. <see cref="LoadTokens" /> takes a YAML mapping of token to value and
///         turns it into an author stylesheet, which beats the three user-agent sheets at equal
///         specificity by origin alone. So a user overriding <c>--accent</c> needs no <c>!important</c>
///         and no knowledge of what the control set's selectors look like.
///     </para>
///     <para>
///         ⚠ <b>One sheet, replaced rather than re-added.</b> A theme file edited and reloaded ten
///         times would otherwise leave ten sheets in the document, each overriding the last —
///         which works, costs a cascade pass per reload, and makes removing an override impossible.
///     </para>
/// </remarks>
public sealed class ThemeService {
    /// <summary>The class the dark theme is selected by, which is the control set's own.</summary>
    public const string DarkClass = "dark";

    readonly UiDocument document;

    int sheet = -1;
    ThemeMode mode;

    /// <summary>Takes charge of a document's theme.</summary>
    /// <param name="document">The document.</param>
    /// <param name="mode">Which theme to start in.</param>
    public ThemeService(UiDocument document, ThemeMode mode = ThemeMode.Dark) {
        ArgumentNullException.ThrowIfNull(document);

        this.document = document;
        Mode = mode;
    }

    /// <summary>Which theme is showing.</summary>
    public ThemeMode Mode {
        get => mode;
        set {
            if (mode == value && document.Root.HasClass(DarkClass) == (value == ThemeMode.Dark)) {
                return;
            }

            mode = value;

            if (value == ThemeMode.Dark) {
                document.Root.AddClass(DarkClass);
            } else {
                document.Root.RemoveClass(DarkClass);
            }

            Changed?.Invoke(this);
        }
    }

    /// <summary>The user's token overrides as they were last loaded, or <c>null</c>.</summary>
    public string? Tokens { get; private set; }

    /// <summary>Raised after the theme changes.</summary>
    public event Action<ThemeService>? Changed;

    /// <summary>Switches to the other theme.</summary>
    public void Toggle() => Mode = Mode == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark;

    /// <summary>Applies a user theme file.</summary>
    /// <param name="yaml">
    ///     A mapping with <c>light</c> and <c>dark</c> sections, each a mapping of token name to
    ///     value. A name may be written with or without its leading <c>--</c>.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Values are written into CSS, so they are refused if they contain anything that could
    ///     end a declaration.</b> A theme file is a file on the user's own machine rather than
    ///     something arriving over a network, so this is tidiness rather than a security boundary —
    ///     but a value with a <c>}</c> in it silently breaks every rule after it, which is a
    ///     miserable thing to debug in a file somebody hand-edited.
    /// </remarks>
    public void LoadTokens(string? yaml) {
        Tokens = yaml;

        var css = yaml is null ? string.Empty : Compile(yaml);

        if (sheet < 0) {
            sheet = document.Load(css);
        } else {
            document.ReloadStyles(sheet, css);
        }

        Changed?.Invoke(this);
    }

    /// <summary>Turns a theme file into the stylesheet it means.</summary>
    /// <param name="yaml">The file.</param>
    /// <returns>The CSS.</returns>
    /// <remarks>Public because it is what a "show me what my theme file does" button shows, and
    ///     because it is the half worth testing without a document.</remarks>
    public static string Compile(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        if (YamlReader.Read(yaml) is not YamlMapping document) {
            return string.Empty;
        }

        var css = new StringBuilder();

        Section(css, "root", document["light"] as YamlMapping);
        Section(css, "root." + DarkClass, document["dark"] as YamlMapping);

        // A file with neither section is read as light-only, so that the simplest useful theme file
        // is a flat list of tokens rather than a nesting nobody would guess.
        if (css.Length == 0) {
            Section(css, "root", document);
        }

        return css.ToString();
    }

    static void Section(StringBuilder css, string selector, YamlMapping? tokens) {
        if (tokens is null || tokens.Count == 0) {
            return;
        }

        var written = 0;

        foreach (var (name, node) in tokens) {
            if (node is not YamlScalar scalar || !Usable(scalar.Value) || name.Length == 0) {
                continue;
            }

            if (written++ == 0) {
                css.Append(selector).Append(" {\n");
            }

            css.Append("    --")
                .Append(name.StartsWith("--", StringComparison.Ordinal) ? name[2..] : name)
                .Append(": ")
                .Append(scalar.Value)
                .Append(";\n");
        }

        if (written > 0) {
            css.Append("}\n");
        }
    }

    static bool Usable(string value) =>
        value.Length > 0 && value.AsSpan().IndexOfAny(['{', '}', ';', '<', '>']) < 0;
}
