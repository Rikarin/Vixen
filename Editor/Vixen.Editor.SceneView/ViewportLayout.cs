// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Controls;
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.SceneView;

/// <summary>How many panes the scene view is split into.</summary>
public enum ViewportArrangement {
    /// <summary>One.</summary>
    Single,

    /// <summary>Two, side by side.</summary>
    SideBySide,

    /// <summary>Two, one above the other.</summary>
    Stacked,

    /// <summary>Four, the classic top/front/side/perspective.</summary>
    Quad
}

/// <summary>Several scene panes, each with its own camera and its own way of drawing.</summary>
/// <remarks>
///     <para>
///         <b>A pane is a whole <see cref="SceneViewport" />, not a camera.</b> Doc 11 asks for
///         "multiple simultaneous viewports with independent cameras and render modes", and the
///         second half of that is what forces it: a render mode is stage state and a
///         <c>RenderStage</c> belongs to the render system, so a pane that wanted its own mode needs
///         its own stage as well as its own view. Sharing anything less than the whole pane makes the
///         second pane silently change the first.
///     </para>
///     <para>
///         <b>The quad layout's three orthographic panes are set up once, on creation.</b> Top,
///         front and side with an orthographic projection is what everybody means by a four-pane
///         layout, and a layout that came up as four identical perspective views is one that every
///         user then configures by hand.
///     </para>
///     <para>
///         ⚠ <b>Only the focused pane is driven by the keyboard.</b> Two panes both taking WASD would
///         fly both cameras, and a viewport is focusable precisely so that "which one" has an answer.
///     </para>
/// </remarks>
public sealed class ViewportLayout : IDisposable {
    readonly List<SceneViewport> panes = [];
    readonly Selection<Entity> selection;
    readonly UiElement root;

    ViewportArrangement arrangement = ViewportArrangement.Single;
    bool disposed;

    /// <summary>The panes, in reading order.</summary>
    public IReadOnlyList<SceneViewport> Panes => panes;

    /// <summary>Which pane the keyboard drives, or <see langword="null" /> when none has focus.</summary>
    public SceneViewport? Focused { get; private set; }

    /// <summary>How the panes are arranged.</summary>
    public ViewportArrangement Arrangement {
        get => arrangement;

        set {
            if (arrangement == value) {
                return;
            }

            arrangement = value;
            Rebuild();
        }
    }

    /// <summary>Where a gizmo drag in any pane is recorded.</summary>
    public EditorDocument? Document {
        get;

        set {
            field = value;

            foreach (var pane in panes) {
                pane.Document = value;
            }
        }
    }

    /// <summary>Turns the shared selection into things a gizmo can move.</summary>
    public Func<IReadOnlyList<IGizmoTarget>>? TargetsFactory {
        get;

        set {
            field = value;

            foreach (var pane in panes) {
                pane.TargetsFactory = value;
            }
        }
    }

    /// <summary>Raised whenever the number of panes changes.</summary>
    public event Action<ViewportLayout>? Rearranged;

    /// <summary>Builds a layout inside an element.</summary>
    /// <param name="root">Where the panes go. Emptied and refilled on every rearrangement.</param>
    /// <param name="selection">What is selected, shared with the rest of the editor.</param>
    public ViewportLayout(UiElement root, Selection<Entity> selection) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(selection);

        this.root = root;
        this.selection = selection;

        Rebuild();
    }

    /// <summary>Advances every pane and brings its render view up to date.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <returns>The views to render this frame, in pane order.</returns>
    /// <remarks>
    ///     Every pane is advanced, not only the focused one — but only one of them is <i>flying</i>,
    ///     because the keys reach the pane the focus is in and the others have no keys held. That is
    ///     the same answer this type's remarks give for the keyboard generally, arrived at by the
    ///     focus rather than by a check here.
    /// </remarks>
    public IReadOnlyList<RenderView> Update(TimeSpan delta) {
        List<RenderView> views = new(panes.Count);

        foreach (var pane in panes) {
            pane.Update(delta);
            views.Add(pane.View);
        }

        return views;
    }

    /// <summary>Says which pane the keyboard drives.</summary>
    /// <param name="pane">The pane, or <see langword="null" /> for none.</param>
    public void Focus(SceneViewport? pane) => Focused = pane is null || panes.Contains(pane) ? pane : Focused;

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Discard();
    }

    void Rebuild() {
        Discard();

        var count = arrangement switch {
            ViewportArrangement.Single => 1,
            ViewportArrangement.SideBySide or ViewportArrangement.Stacked => 2,
            _ => 4
        };

        root.AddClass("viewport-layout");
        root.RemoveClass("single");
        root.RemoveClass("side-by-side");
        root.RemoveClass("stacked");
        root.RemoveClass("quad");

        // The arrangement is a class rather than inline geometry, so the split is a stylesheet's
        // business — which is also what lets a user's own theme change the proportions.
        root.AddClass(arrangement switch {
            ViewportArrangement.Single => "single",
            ViewportArrangement.SideBySide => "side-by-side",
            ViewportArrangement.Stacked => "stacked",
            _ => "quad"
        });

        for (var index = 0; index < count; index++) {
            var control = root.Add<ViewportControl>();
            var pane = new SceneViewport(control, selection, $"SceneView{index}") {
                Document = Document,
                TargetsFactory = TargetsFactory
            };

            if (arrangement == ViewportArrangement.Quad) {
                Preset(pane, index);
            }

            panes.Add(pane);
        }

        Focused = panes.Count > 0 ? panes[0] : null;
        Rearranged?.Invoke(this);
    }

    /// <summary>The classic four: top, front, side, and a perspective view.</summary>
    static void Preset(SceneViewport pane, int index) {
        switch (index) {
            case 0:
                pane.Camera.LookFrom(ViewDirection.Top);
                pane.Camera.IsOrthographic = true;

                break;

            case 1:
                pane.Camera.LookFrom(ViewDirection.Front);
                pane.Camera.IsOrthographic = true;

                break;

            case 2:
                pane.Camera.LookFrom(ViewDirection.Right);
                pane.Camera.IsOrthographic = true;

                break;

            default:
                // The perspective pane keeps the default angle rather than an axis view, which is
                // what makes it the one you actually work in.
                //
                // ⚠ In degrees through `Turn` rather than as a pointer drag through `Orbit`. A
                // preset expressed as pixels is one that moves when the orbit speed is tuned and
                // reverses when somebody sets `InvertOrbitY` — and this preset was already the
                // casualty of the second of those, coming up underneath the grid looking at the sky.
                pane.Camera.Turn(MathUtil.DegreesToRadians(45f), MathUtil.DegreesToRadians(-30f));
                break;
        }
    }

    void Discard() {
        foreach (var pane in panes) {
            pane.Dispose();
        }

        panes.Clear();
        Focused = null;

        while (root.Children.Count > 0) {
            root.Children[^1].Remove();
        }
    }
}
