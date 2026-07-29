// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App.Tests;

/// <summary>A whole editor, in a project of its own, driven the way the host drives it.</summary>
/// <remarks>
///     <para>
///         <b>The real <see cref="EditorApplication" />, not a rehearsal of one.</b> Everything these
///         tests are about — which panel a click landed in, what the inspector was handed, which of
///         the two selections won — is wiring that exists only in that class, so a fixture that built
///         its own shell and its own panels would be testing a copy of the thing that breaks.
///     </para>
///     <para>
///         ⚠ <b>The frame is <c>EditorHost</c>'s four steps in <c>EditorHost</c>'s order</b>: tick,
///         lay out, update, draw. The application's own remarks say the update has to sit between the
///         layout and the draw, and a harness that ran it anywhere else would be one whose passing
///         tests said nothing about the loop that actually runs.
///     </para>
///     <para>
///         ⚠ <b>No GPU, no window, no platform.</b> The host's first three steps are all above the
///         RHI — which is the same reason <c>--frames N</c> is a smoke test on a machine with no
///         Vulkan — so everything up to and including the draw list runs here.
///     </para>
/// </remarks>
sealed class EditorFixture : IDisposable {
    readonly string directory;

    TimeSpan clock;

    public EditorFixture(float width = 1600f, float height = 1000f) {
        // A directory per fixture, because opening a project writes to it: sidecars, a GUID index,
        // the seeded scene, and on the way down a layout and a keymap. Two tests sharing one would
        // be two editors scanning the same tree.
        directory = Path.Combine(Path.GetTempPath(), "vixen-editor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        Editor = new EditorApplication(width, height, directory);

        // The same borrowed face the application's own loader finds. Nothing here asserts on text,
        // but a document with no font measures every label at zero — and a row whose label is zero
        // wide is one whose hit test lands somewhere a person's click would not.
        Fonts.Install(Editor.Shell.Document);

        Frames(3);
    }

    /// <summary>The editor under test.</summary>
    public EditorApplication Editor { get; }

    /// <summary>Its document, which is the shell's.</summary>
    public UiDocument Document => Editor.Shell.Document;

    /// <summary>The hierarchy's tree.</summary>
    public TreeView Hierarchy => TreeIn("hierarchy");

    /// <summary>The project browser's tree.</summary>
    public TreeView Project => TreeIn("project");

    /// <summary>The inspector.</summary>
    public InspectorView Inspector => Find<InspectorView>(Document.Root)
        ?? throw new InvalidOperationException("the inspector panel is not open");

    /// <summary>Runs one frame, the way <c>EditorHost</c> does.</summary>
    public void Frame() {
        clock += TimeSpan.FromMilliseconds(16);

        Editor.Shell.Tick(clock, TimeSpan.FromMilliseconds(16));
        Document.Update();
        Editor.Update(TimeSpan.FromMilliseconds(16));
        Document.Draw();
    }

    /// <summary>Runs several.</summary>
    /// <param name="count">How many.</param>
    public void Frames(int count) {
        for (var index = 0; index < count; index++) {
            Frame();
        }
    }

    /// <summary>Brings a panel forward, so that a click can reach it.</summary>
    /// <param name="id">Which panel.</param>
    /// <remarks>
    ///     The hierarchy and the project browser are tabs of one group in the default arrangement, so
    ///     the one that is not in front has no size at all — and a click aimed at the middle of a
    ///     zero-sized row lands on whatever is behind it.
    /// </remarks>
    public void Open(string id) {
        Editor.Shell.Workspace.Open(id);
        Frames(2);
    }

    /// <summary>Clicks the middle of an element.</summary>
    /// <param name="element">What to click.</param>
    public void Click(UiElement element) {
        var x = element.Bounds.X + (element.Bounds.Width * 0.5f);
        var y = element.Bounds.Y + (element.Bounds.Height * 0.5f);

        // A move first, because a pointer that arrives already pressed is not a gesture anything
        // outside a test ever sees.
        Send(x, y, PointerAction.Moved, PointerButton.None);
        Send(x, y, PointerAction.Pressed, PointerButton.Primary);
        Send(x, y, PointerAction.Released, PointerButton.Primary);

        Frames(2);
    }

    /// <summary>Double-clicks the row showing a node, which is what opens an asset.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="text">What the row says.</param>
    /// <remarks>
    ///     ⚠ <b>Two presses in the same place with no rest between them.</b> The gesture recogniser
    ///     counts taps in a row and a control acting on the second one sees nothing from two clicks a
    ///     second apart — which every user gets right by being fast and every test gets wrong by
    ///     being slow.
    /// </remarks>
    public void DoubleClickRow(TreeView tree, string text) {
        var row = Row(tree, text);
        var x = row.Bounds.X + (row.Bounds.Width * 0.5f);
        var y = row.Bounds.Y + (row.Bounds.Height * 0.5f);

        Send(x, y, PointerAction.Moved, PointerButton.None);
        Send(x, y, PointerAction.Pressed, PointerButton.Primary);
        Send(x, y, PointerAction.Released, PointerButton.Primary);
        Send(x, y, PointerAction.Pressed, PointerButton.Primary);
        Send(x, y, PointerAction.Released, PointerButton.Primary);

        Frames(3);
    }

    /// <summary>The tree inside the panel an opened asset was given, whichever GUID it was named after.</summary>
    public TreeView OpenedAssetTree =>
        Panels(Document.Root)
            .Where(panel => panel.Id.StartsWith("asset.", StringComparison.Ordinal))
            .Select(panel => Find<TreeView>(panel))
            .FirstOrDefault(tree => tree is not null)
        ?? throw new InvalidOperationException("no asset panel with a tree in it is open");

    /// <summary>Opens every node in a tree, so that a deep row is on screen to be clicked.</summary>
    /// <param name="tree">Which tree.</param>
    public void ExpandAll(TreeView tree) {
        foreach (var node in Descendants(tree.Root).ToList()) {
            if (node.HasChildren) {
                tree.Expand(node);
            }
        }

        Frames(2);

        static IEnumerable<TreeNode> Descendants(TreeNode node) {
            foreach (var child in node.Children) {
                yield return child;

                foreach (var descendant in Descendants(child)) {
                    yield return descendant;
                }
            }
        }
    }

    /// <summary>Clicks the row showing a node.</summary>
    /// <param name="tree">Which tree.</param>
    /// <param name="text">What the row says.</param>
    public void ClickRow(TreeView tree, string text) => Click(Row(tree, text));

    /// <summary>The realised row showing a node.</summary>
    static TreeRow Row(TreeView tree, string text) =>
        tree.Rows.FirstOrDefault(candidate => !candidate.HasClass("parked") && candidate.Node?.Text == text)
        ?? throw new InvalidOperationException($"no row saying '{text}' is on screen");

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The editor and nothing else.</b> It disposes the shell, which owns the document — the
    ///     arrangement <c>UiTest.Adopt</c> warns about, from the other side.
    /// </remarks>
    public void Dispose() {
        Editor.Dispose();

        try {
            Directory.Delete(directory, recursive: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A temp directory that would not go is not a failed test.
        }
    }

    void Send(float x, float y, PointerAction action, PointerButton button) {
        clock += TimeSpan.FromMilliseconds(16);

        Document.Dispatch(
            new PointerEvent {
                X = x,
                Y = y,
                Action = action,
                Button = button,
                Modifiers = ModifierKeys.None,
                Timestamp = clock
            }
        );

        Frame();
    }

    /// <summary>The tree inside a panel, found by the id the arrangement knows the panel by.</summary>
    /// <remarks>
    ///     By panel rather than by type, because both of the trees this fixture reaches for are
    ///     <c>TreeView</c>s in the same document — and "the first one" is an answer that depends on
    ///     which side of the window the layout put them on.
    /// </remarks>
    TreeView TreeIn(string panel) {
        var found = Panels(Document.Root).FirstOrDefault(candidate => candidate.Id == panel)
            ?? throw new InvalidOperationException($"the '{panel}' panel is not open");

        return Find<TreeView>(found) ?? throw new InvalidOperationException($"the '{panel}' panel has no tree");
    }

    static IEnumerable<DockPanel> Panels(UiElement element) {
        if (element is DockPanel panel) {
            yield return panel;
        }

        foreach (var child in element.Children) {
            foreach (var found in Panels(child)) {
                yield return found;
            }
        }
    }

    static T? Find<T>(UiElement element) where T : UiElement {
        if (element is T match) {
            return match;
        }

        foreach (var child in element.Children) {
            if (Find<T>(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
