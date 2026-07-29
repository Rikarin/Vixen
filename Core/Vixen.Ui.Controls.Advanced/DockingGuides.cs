// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Ui.Controls.Advanced;

public sealed partial class DockingHost {
    /// <summary>The zones a guide is offered for, in the order the handles are built.</summary>
    /// <remarks>
    ///     The order is the tree order of <see cref="Guides" />' children, which is what lets the
    ///     highlight walk both together. The <i>class</i> on each handle is what a stylesheet and a
    ///     test address it by, so neither depends on this array staying as it is.
    /// </remarks>
    static readonly DockZone[] GuideZones =
        [DockZone.Center, DockZone.Left, DockZone.Right, DockZone.Top, DockZone.Bottom];

    /// <summary>How big one handle is.</summary>
    /// <remarks>
    ///     ⚠ <b>Declared here rather than in the stylesheet, and the stylesheet is told.</b> The
    ///     handle a drop lands on is decided by arithmetic — the pointer is captured by the tab for
    ///     the whole drag, so nothing is ever hit-tested against these elements — and a theme that
    ///     could resize them would move the drawn handle away from the one that answers.
    /// </remarks>
    internal const float GuideSize = 28f;

    /// <summary>The gap between two handles.</summary>
    internal const float GuideGap = 4f;

    /// <summary>From one handle's edge to the next one's.</summary>
    const float GuideStep = GuideSize + GuideGap;

    /// <summary>How wide and tall the whole cluster is.</summary>
    internal const float GuideSpan = (GuideStep * 2f) + GuideSize;

    /// <summary>The five handles shown over the group a drag is currently over.</summary>
    /// <remarks>
    ///     One per window, for the reason <see cref="Preview" /> is: an element is in exactly one
    ///     surface, and a single shared cluster would draw the guides for a torn-off inspector in
    ///     the main window's middle.
    /// </remarks>
    public UiElement Guides { get; private set; } = null!;

    /// <summary>Builds a cluster of guide handles into a window's root.</summary>
    /// <remarks>
    ///     Built once and moved thereafter, not built per drag: a cluster created when the pointer
    ///     first entered a group would have no box to measure on the frame it appeared, and
    ///     <see cref="Place" /> works from where layout last put the element.
    /// </remarks>
    static UiElement BuildGuides(UiElement parent) {
        var guides = parent.Add("dock-guides");
        guides.AddClass("hidden");

        foreach (var zone in GuideZones) {
            var guide = guides.Add("dock-guide", null, ClassOf(zone));
            var offset = Offset(zone);

            guide.OffsetX = offset.X;
            guide.OffsetY = offset.Y;

            // What the handle says it would do: the miniature of the rectangle the preview draws
            // full size. An arrow would have to be read; half a box shaded in is the answer itself.
            guide.Add("dock-hint");
        }

        return guides;
    }

    /// <summary>What a stylesheet and a test call the handle for a zone.</summary>
    static string ClassOf(DockZone zone) =>
        zone switch {
            DockZone.Left => "left",
            DockZone.Right => "right",
            DockZone.Top => "top",
            DockZone.Bottom => "bottom",
            _ => "center"
        };

    /// <summary>Where a handle sits inside the cluster.</summary>
    static Vector2 Offset(DockZone zone) =>
        zone switch {
            DockZone.Left => new Vector2(0f, GuideStep),
            DockZone.Right => new Vector2(GuideStep * 2f, GuideStep),
            DockZone.Top => new Vector2(GuideStep, 0f),
            DockZone.Bottom => new Vector2(GuideStep, GuideStep * 2f),
            _ => new Vector2(GuideStep, GuideStep)
        };

    /// <summary>Where the cluster goes over a group: the middle of it.</summary>
    internal static Rectangle Cluster(Rectangle bounds) =>
        new(
            bounds.X + ((bounds.Width - GuideSpan) * 0.5f),
            bounds.Y + ((bounds.Height - GuideSpan) * 0.5f),
            GuideSpan,
            GuideSpan
        );

    /// <summary>Where one handle lands over a group, in whatever space the group's rectangle is in.</summary>
    /// <param name="bounds">The group.</param>
    /// <param name="zone">Which handle.</param>
    /// <returns>The handle's rectangle.</returns>
    internal static Rectangle GuideBounds(Rectangle bounds, DockZone zone) {
        var cluster = Cluster(bounds);
        var offset = Offset(zone);

        return new Rectangle(cluster.X + offset.X, cluster.Y + offset.Y, GuideSize, GuideSize);
    }

    /// <summary>Whether a group has room for the cluster.</summary>
    /// <remarks>
    ///     ⚠ <b>A pane smaller than the cluster gets no guides at all rather than guides hanging
    ///     over its neighbours.</b> Handles drawn outside the group they belong to would offer a drop
    ///     on top of the panel next door and dock it somewhere else — and the edge proximity that
    ///     <see cref="ZoneOf" /> reads is still there, so a narrow pane is docked into exactly as it
    ///     was before any of this existed.
    /// </remarks>
    internal static bool Guided(Rectangle bounds) => bounds.Width >= GuideSpan && bounds.Height >= GuideSpan;

    /// <summary>Which handle a point is on, if any.</summary>
    /// <param name="bounds">The group the cluster is over.</param>
    /// <param name="x">The point.</param>
    /// <param name="y">Ditto.</param>
    /// <returns>The zone its handle stands for, or <see langword="null" /> if it is on none of them.</returns>
    internal static DockZone? GuideAt(Rectangle bounds, float x, float y) {
        if (!Guided(bounds)) {
            return null;
        }

        foreach (var zone in GuideZones) {
            if (Inside(GuideBounds(bounds, zone), x, y)) {
                return zone;
            }
        }

        return null;
    }

    /// <summary>Where a drop on a group would put the panel.</summary>
    /// <remarks>
    ///     ⚠ <b>A handle wins over the edge it is nowhere near, and that is the point of having
    ///     them.</b> The proximity rule <see cref="ZoneOf" /> applies is a quarter of the pane deep,
    ///     so the whole middle of a group means "stack it here" — which is right until the user wants
    ///     a split and has to guess how close to the edge is close enough. The handles are that
    ///     answer written down, and they sit in the middle precisely so that aiming at one is never
    ///     the same gesture as aiming at an edge.
    /// </remarks>
    internal static DockZone ZoneAt(Rectangle bounds, float x, float y) =>
        GuideAt(bounds, x, y) ?? ZoneOf(bounds, x, y);

    /// <summary>Shows the cluster over a group with the handle for a zone lit.</summary>
    static void Guide(UiElement guides, Rectangle bounds, DockZone side) {
        if (!Guided(bounds)) {
            return;
        }

        guides.RemoveClass("hidden");
        Place(guides, Cluster(bounds));

        // ⚠ Lit from the zone the drop would *actually* use rather than from what the pointer is
        // over, which are different things whenever the pointer is over no handle at all: the
        // proximity rule still answers, and a cluster with nothing lit while a preview covers half
        // the pane reads as the guides having stopped working.
        for (var i = 0; i < guides.Children.Count && i < GuideZones.Length; i++) {
            var guide = guides.Children[i];

            if (GuideZones[i] == side) {
                guide.AddClass("active");
            } else {
                guide.RemoveClass("active");
            }
        }
    }
}
