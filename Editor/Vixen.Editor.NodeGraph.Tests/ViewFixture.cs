// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Text;
using PortDirection = Vixen.Editor.NodeGraph.PortDirection;

namespace Tests;

/// <summary>A document that holds nothing, so an undo stack can be had without a project on disk.</summary>
sealed class ScratchDocument(EditorProject project) : EditorDocument(project, AssetId.Empty, "Scratch") {
    protected override void SaveCore() { }
}

/// <summary>
///     A whole graph editor, headless: three stylesheets, a font, a project, and a way to drive it.
/// </summary>
/// <remarks>
///     All three sheets, in order. The view's own is written against the advanced theme's tokens,
///     which are written against the base theme's, and a custom property nothing declared substitutes
///     to nothing — which is a search popup with no size, so every test about what is on screen would
///     quietly measure zero.
/// </remarks>
sealed class ViewFixture : IDisposable {
    static readonly FontFace Font = LoadFont();

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-nodegraph-" + Guid.NewGuid().ToString("N"));

    TimeSpan clock;

    public ViewFixture(float width = 900f, float height = 700f) {
        Directory.CreateDirectory(root);

        Project = new(new ProjectPaths(root));
        Document = new ScratchDocument(Project);

        Ui = new UiDocument(width, height);
        Ui.Fonts.Register("Test", Font);

        ControlTheme.Install(Ui);
        AdvancedTheme.Install(Ui);
        NodeGraphTheme.Install(Ui);

        Ui.Load($"root {{ width: {width}px; height: {height}px; }}");

        View = Ui.Root.Add<NodeGraphView>();
        View.EditedDocument = Document;

        Update();
    }

    public EditorProject Project { get; }

    public ScratchDocument Document { get; }

    public UiDocument Ui { get; }

    public NodeGraphView View { get; }

    public NodeCanvas Canvas => View.Canvas;

    public CommandStack Stack => Document.Stack;

    /// <summary>Shows a graph, and lets the layout settle so the canvas knows how big it is.</summary>
    public void Show(NodeGraphModel graph, NodeTypeRegistry registry) {
        View.Registry = registry;
        View.Graph = graph;

        Update();

        // ⚠ Twice, and the second is not superstition: NodeCanvas realises against the size it had
        // when it last realised, and on the first pass through a fresh document that size is zero.
        // The same gap TreeView and ScrollView have, and the same workaround.
        Canvas.Refresh();
        Update();
    }

    public void Update() {
        Ui.Update();
        Ui.Draw();
    }

    public void Press(
        float x,
        float y,
        PointerButton button = PointerButton.Primary,
        ModifierKeys modifiers = ModifierKeys.None
    ) => Send(x, y, PointerAction.Pressed, button, modifiers);

    public void Move(float x, float y, ModifierKeys modifiers = ModifierKeys.None) =>
        Send(x, y, PointerAction.Moved, PointerButton.None, modifiers);

    public void Release(
        float x,
        float y,
        PointerButton button = PointerButton.Primary,
        ModifierKeys modifiers = ModifierKeys.None
    ) => Send(x, y, PointerAction.Released, button, modifiers);

    /// <summary>Presses in the middle of an element, drags to a point and releases there.</summary>
    public void DragFrom(UiElement from, float x, float y, ModifierKeys modifiers = ModifierKeys.None) {
        var bounds = from.Bounds;

        Press(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f), modifiers: modifiers);
        Move(x, y, modifiers);
        Release(x, y, modifiers: modifiers);
    }

    public void Click(UiElement element, ModifierKeys modifiers = ModifierKeys.None) {
        var bounds = element.Bounds;
        var x = bounds.X + (bounds.Width * 0.5f);
        var y = bounds.Y + (bounds.Height * 0.5f);

        Press(x, y, modifiers: modifiers);
        Release(x, y, modifiers: modifiers);
    }

    public void Type(InputKey key, ModifierKeys modifiers = ModifierKeys.None) {
        SendKey(key, KeyAction.Pressed, modifiers);
        SendKey(key, KeyAction.Released, modifiers);
    }

    /// <summary>The element showing a port of a node, once the canvas has realised it.</summary>
    public NodePortView Port(NodeId node, string port, PortDirection direction) {
        var item = Canvas.Items.FirstOrDefault(candidate => candidate.Node?.Tag is NodeId id && id == node)
            ?? throw new InvalidOperationException($"{node} has no element on the canvas.");

        var pool = direction == PortDirection.Input ? item.Inputs : item.Outputs;

        return pool.FirstOrDefault(view => view.Port?.Name == port)
            ?? throw new InvalidOperationException($"{node} has no '{port}' element.");
    }

    /// <summary>The element showing a node.</summary>
    public NodeItem Item(NodeId node) =>
        Canvas.Items.FirstOrDefault(candidate => candidate.Node?.Tag is NodeId id && id == node)
        ?? throw new InvalidOperationException($"{node} has no element on the canvas.");

    void Send(float x, float y, PointerAction action, PointerButton button, ModifierKeys modifiers) {
        clock += TimeSpan.FromMilliseconds(16);

        Ui.Dispatch(
            new PointerEvent {
                X = x,
                Y = y,
                Action = action,
                Button = button,
                Modifiers = modifiers,
                Timestamp = clock
            }
        );

        Update();
    }

    void SendKey(InputKey key, KeyAction action, ModifierKeys modifiers) {
        clock += TimeSpan.FromMilliseconds(16);
        Ui.Dispatch(new KeyEvent { Key = key, Action = action, Modifiers = modifiers, Timestamp = clock });

        Update();
    }

    public void Dispose() {
        Ui.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }
    }

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Editor.NodeGraph.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray());
    }
}
