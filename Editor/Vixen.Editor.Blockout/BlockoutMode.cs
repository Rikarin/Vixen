// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;

namespace Vixen.Editor.Blockout;

/// <summary>The viewport mode the grey-boxing tools live in.</summary>
/// <remarks>
///     <para>
///         <b>The second mode, and the one doc 20's <c>IEditorMode</c> was written for.</b> A1 asks
///         for the interface to ship with one mode so the seam is proven; doc 24's B2 is the argument
///         that a seam with one implementation is a hypothesis, and that blockout is what turns it
///         into a consumer — because it needs first refusal on viewport input, its own toolbar, and a
///         claim on keys that already mean something.
///     </para>
///     <para>
///         ⚠ <b>So far it owns its keys and nothing else, and that is doc 24's P0 exactly.</b> There
///         is no editable mesh in the engine yet — <c>Core/Vixen.Geometry</c> is P1 — so
///         <see cref="Element" /> is a statement about what a click <i>would</i> select rather than
///         something that selects it, and <see cref="Pointer" /> declines every event. What is real
///         is the arbitration: while this mode is active <c>1</c>, <c>2</c>, <c>3</c> and <c>4</c> in
///         the viewport are the element modes, and while it is not they are view-bookmark recall. That
///         is the thing that could not be retrofitted, and it is the thing this mode is here to prove.
///     </para>
///     <para>
///         ⚠ <b>The element commands are registered whether or not the mode is active, and scoped so
///         that they are only <i>reachable</i> while it is.</b> Registering them from
///         <see cref="Activated" /> would keep them out of the keybinding editor and out of the
///         palette until somebody had entered the mode once, which is how a rebindable shortcut
///         becomes undiscoverable.
///     </para>
/// </remarks>
public sealed class BlockoutMode : IEditorMode {
    /// <summary>What the mode is called, everywhere an id is wanted.</summary>
    public const string ModeId = "blockout";

    /// <summary>The command context the mode claims while it is active.</summary>
    /// <remarks>
    ///     ⚠ <b>The same string as <see cref="ModeId" /> and deliberately a separate constant.</b> One
    ///     is what the mode bar's button is called and the other is what a saved keymap files a
    ///     binding under; they coincide today and a rename of either must not silently move the other.
    /// </remarks>
    public const string BlockoutContext = "blockout";

    EditorShell? shell;

    /// <summary>The element mode <c>Tab</c> goes back into, which is the last one that was not Object.</summary>
    BlockoutElement inside = BlockoutElement.Face;

    /// <inheritdoc />
    public string Id => ModeId;

    /// <inheritdoc />
    public StringId Title { get; } = new("editor.mode.blockout", "Blockout");

    /// <inheritdoc />
    /// <remarks>
    ///     None, so the mode bar draws the word. Two glyphs on a strip that decides what every gesture
    ///     in the viewport means is two glyphs somebody has to learn — see <see cref="IEditorMode.Icon" />.
    /// </remarks>
    public PathBuilder? Icon => null;

    /// <inheritdoc />
    public string? Context => BlockoutContext;

    /// <inheritdoc />
    /// <remarks>None yet. The tool settings panel arrives with the tools, in doc 24's P3.</remarks>
    public string? Panel => null;

    /// <inheritdoc />
    /// <remarks>
    ///     The four element modes as one segmented control, which is what they are: a choice, not four
    ///     switches. The verbs join it a phase at a time.
    /// </remarks>
    public IReadOnlyList<ToolbarEntry> Toolbar { get; } = [
        new ToolbarGroup(ElementCommand(BlockoutElement.Object),
            ElementCommand(BlockoutElement.Vertex),
            ElementCommand(BlockoutElement.Edge),
            ElementCommand(BlockoutElement.Face))
    ];

    /// <summary>What a click in the viewport would select.</summary>
    public BlockoutElement Element {
        get;
        private set {
            if (field == value) {
                return;
            }

            field = value;

            // The one to come back to when `Tab` leaves the mesh and goes in again. Object is not a
            // place to come back to, so it is not remembered as one.
            if (value != BlockoutElement.Object) {
                inside = value;
            }

            ElementChanged?.Invoke(value);
        }
    } = BlockoutElement.Object;

    /// <summary>Raised when <see cref="Element" /> changes.</summary>
    public event Action<BlockoutElement>? ElementChanged;

    /// <summary>What the command that selects an element mode is called.</summary>
    /// <param name="element">The element mode.</param>
    /// <returns>The command id.</returns>
    public static string ElementCommand(BlockoutElement element) =>
        "blockout.element." + element switch {
            BlockoutElement.Vertex => "vertex",
            BlockoutElement.Edge => "edge",
            BlockoutElement.Face => "face",
            _ => "object"
        };

    /// <summary>The command that enters and leaves the mesh.</summary>
    public const string ToggleMeshCommand = "blockout.toggle-mesh";

    /// <inheritdoc />
    public void Register(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);
        this.shell = shell;

        Declare(BlockoutElement.Object, "Object Mode", InputKey.Number1);
        Declare(BlockoutElement.Vertex, "Vertex Mode", InputKey.Number2);
        Declare(BlockoutElement.Edge, "Edge Mode", InputKey.Number3);
        Declare(BlockoutElement.Face, "Face Mode", InputKey.Number4);

        shell.Commands.Add(
            new EditorCommand(ToggleMeshCommand, new StringId("editor.command.blockout.toggle-mesh", "Enter / Leave Mesh"), Toggle) {
                Category = CategoryBlockout,
                Context = BlockoutContext,
                Enablement = IsActive
            }
        );

        // ⚠ Tab, and it beats the interface's own focus traversal rather than fighting it.
        // `Keyboard.Dispatch` moves the focus only when the route left the event unhandled, and the
        // command dispatcher is on that route — so the binding wins while the blockout context has
        // the focus and Tab is ordinary focus movement everywhere else.
        shell.Keys.SetDefault(ToggleMeshCommand, new KeyChord(InputKey.Tab, ModifierKeys.None));

        void Declare(BlockoutElement element, string label, InputKey key) {
            var id = ElementCommand(element);

            shell.Commands.Add(
                new EditorCommand(id, new StringId("editor.command." + id, label), () => this.Element = element) {
                    Category = CategoryBlockout,

                    // ⚠ This is the whole of doc 24's B2 in one line. The command belongs to the
                    // blockout context, so `KeyMap` files its chord under that context rather than
                    // globally — and `scene.bookmark-go-1`, which is bound to the same key with no
                    // context at all, keeps it everywhere the blockout context does not have the
                    // focus. Neither had to give up the key and neither had to move.
                    Context = BlockoutContext,

                    RadioGroup = ElementGroup,
                    Checked = () => this.Element == element,
                    Enablement = IsActive
                }
            );

            shell.Keys.SetDefault(id, new KeyChord(key, ModifierKeys.None));
        }
    }

    /// <inheritdoc />
    public void Unregister(EditorShell shell) {
        ArgumentNullException.ThrowIfNull(shell);

        shell.Commands.Remove(ElementCommand(BlockoutElement.Object));
        shell.Commands.Remove(ElementCommand(BlockoutElement.Vertex));
        shell.Commands.Remove(ElementCommand(BlockoutElement.Edge));
        shell.Commands.Remove(ElementCommand(BlockoutElement.Face));
        shell.Commands.Remove(ToggleMeshCommand);

        this.shell = null;
    }

    /// <inheritdoc />
    public void Activated() {
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Back to Object on the way out, and it is not tidiness.</b> A sub-object element mode
    ///     is a claim about a mesh being edited; leaving it set while the mode is inactive would mean
    ///     re-entering blockout put the viewport straight back into face selection on whatever
    ///     happens to be selected now, which is rarely what was being edited a moment ago.
    /// </remarks>
    public void Deactivated() => Element = BlockoutElement.Object;

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing yet, and it is a real <see langword="false" /> rather than a stub: there is no
    ///     editable mesh to pick a face of until doc 24's P1, so every press still means what it means
    ///     in Select mode. The seam is what this phase ships; the gestures are P2's.
    /// </remarks>
    public bool Pointer(PointerEvent args) => false;

    /// <inheritdoc />
    /// <remarks>Ditto: everything the mode owns today is a command, and a command's key comes
    ///     through the keymap rather than through here.</remarks>
    public bool Key(KeyEvent args) => false;

    /// <summary>Enters the mesh, or comes back out of it.</summary>
    void Toggle() =>
        Element = Element == BlockoutElement.Object ? inside : BlockoutElement.Object;

    /// <summary>Whether this mode is the shell's active one.</summary>
    /// <remarks>
    ///     ⚠ <b>Enablement as well as context, because the palette does not go through the keymap.</b>
    ///     Scoping keeps the chord from firing outside the mode; it does nothing about somebody
    ///     choosing "Face Mode" out of the command palette while they are in Select, which would set a
    ///     state nothing is reading and tick a button nothing is drawing.
    /// </remarks>
    bool IsActive() => shell?.Modes.IsActive(ModeId) == true;

    /// <summary>Where the palette files the mode's verbs.</summary>
    static readonly StringId CategoryBlockout = new("editor.category.blockout", "Blockout");

    /// <summary>The radio group the four element modes are in.</summary>
    const string ElementGroup = "blockout.element";
}
