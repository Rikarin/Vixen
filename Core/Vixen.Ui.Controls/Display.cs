// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
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
}

/// <summary>A person or an entity, as a circle with their initials in it.</summary>
/// <remarks>
///     ⚠ <s><b>Initials only, for now, and it is the draw list rather than this control that says
///     so.</b> There is no texture command — <c>DrawCommandKind</c> has rectangles, borders, text,
///     paths and clips — so an avatar cannot show a picture until one exists.</s>
///     <para>
///         <b>There is one now.</b> <see cref="SurfaceView" /> draws whatever a renderer's surface
///         drawer recognises, so an avatar with a picture is this control with one of those inside
///         it, and the picture is no longer owed by the draw list. <see cref="Initials" /> stays as
///         what it always was: the fallback every avatar needs anyway, for the person whose portrait
///         has not loaded or was never set.
///     </para>
/// </remarks>
public sealed partial class Avatar : Control {
    /// <inheritdoc />
    protected override string TagName => "avatar";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

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
///     ⚠ <b>It reserves the space and draws nothing, and that is the honest state of it.</b> The draw
///     list has no texture command — see <c>DrawCommandKind</c> — so there is nowhere for pixels to
///     go. What this does do is the half that does not need one: <see cref="Source" /> names the
///     asset, and <c>aspect-ratio</c> in the stylesheet keeps the box the right shape so a layout
///     built against images does not reflow when they finally appear. A background colour on it is a
///     perfectly good placeholder in the meantime.
/// </remarks>
public sealed partial class Image : Control {
    /// <inheritdoc />
    protected override string TagName => "image";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>What to show, named the way the application's asset system names things.</summary>
    [UiProperty]
    public partial string? Source { get; set; }

    /// <summary>What it is a picture of, for a tooltip and for the accessibility bridge.</summary>
    [UiProperty]
    public partial string? Description { get; set; }
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

    /// <summary>Writes a combination the way a menu would.</summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">What is held with it.</param>
    /// <returns>Something like <c>Ctrl+Shift+S</c>.</returns>
    /// <remarks>
    ///     <para>
    ///         The modifier order is Ctrl, Alt, Shift, Meta, which is the order Windows, GTK and Qt
    ///         all write them in. It is not alphabetical and it is not the flag order; it is a
    ///         convention, and a menu that used a different one would look wrong beside every other
    ///         application on the machine.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not localised and not platform-adapted.</b> A Mac writes <c>⌘⇧S</c> with no
    ///         separators and a different modifier order, and getting that right needs to know what
    ///         it is running on — which this assembly deliberately does not, because
    ///         <c>Vixen.Ui</c> sits below <c>Vixen.Platform</c>. An application that ships on macOS
    ///         should set the text itself; this is the default, not the answer.
    ///     </para>
    /// </remarks>
    public static string Describe(InputKey key, ModifierKeys modifiers) {
        var text = new StringBuilder();

        if (modifiers.HasFlag(ModifierKeys.Control)) {
            text.Append("Ctrl+");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt)) {
            text.Append("Alt+");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift)) {
            text.Append("Shift+");
        }

        if (modifiers.HasFlag(ModifierKeys.Meta)) {
            text.Append("Meta+");
        }

        return text.Append(Name(key)).ToString();
    }

    /// <summary>What a key is called on a menu.</summary>
    /// <remarks>
    ///     The enum's own name for everything except the handful whose member name is a description
    ///     rather than a legend — <c>Number1</c> is the <c>1</c> key and no menu has ever written it
    ///     that way.
    /// </remarks>
    static string Name(InputKey key) =>
        key switch {
            >= InputKey.Number1 and <= InputKey.Number9 => ((int) (key - InputKey.Number1) + 1).ToString(),
            InputKey.Number0 => "0",
            InputKey.Space => "Space",
            InputKey.Grave => "`",
            InputKey.Minus => "-",
            InputKey.Equals => "=",
            InputKey.LeftBracket => "[",
            InputKey.RightBracket => "]",
            InputKey.Backslash => "\\",
            InputKey.Semicolon => ";",
            InputKey.Apostrophe => "'",
            InputKey.Comma => ",",
            InputKey.Period => ".",
            InputKey.Slash => "/",
            _ => key.ToString()
        };

    void OnKeyChanged(InputKey previous, InputKey current) => Text = Describe(current, Modifiers);

    void OnModifiersChanged(ModifierKeys previous, ModifierKeys current) => Text = Describe(Key, current);
}
