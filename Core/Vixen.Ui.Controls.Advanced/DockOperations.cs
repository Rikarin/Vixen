// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Controls.Advanced;

public sealed partial class DockLayout {
    /// <summary>Puts a panel somewhere, taking it out of wherever it was.</summary>
    /// <param name="id">The panel's id.</param>
    /// <param name="target">The group to dock it against.</param>
    /// <param name="zone">Which side of that group, or its middle.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Removed first, and the order is load-bearing.</b> Dropping a panel back into the
    ///         group it came from would otherwise add a second copy of it and then leave the first
    ///         one there — the commonest gesture in a docking host is the one that ends where it
    ///         started, and it must be a no-op rather than a duplication.
    ///     </para>
    ///     <para>
    ///         The removal prunes, which may take the target group out of the tree — a group holding
    ///         only the panel being moved. So the target is re-checked afterwards and a drop onto a
    ///         group that has just ceased to exist becomes a drop onto whatever survived.
    ///     </para>
    /// </remarks>
    public void Dock(string id, DockGroupNode target, DockZone zone) {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(target);

        RemovePanel(id);

        if (!Contains(target)) {
            // The target went with the removal. Whatever is left is where the panel goes; with
            // nothing left it becomes the whole arrangement.
            if (Groups() is [var survivor, ..]) {
                survivor.Add(id);
            } else {
                Root = new DockGroupNode(id);
            }

            return;
        }

        if (zone == DockZone.Center) {
            target.Add(id);
            return;
        }

        var group = new DockGroupNode(id);

        var orientation = zone is DockZone.Left or DockZone.Right
            ? Orientation.Horizontal
            : Orientation.Vertical;

        var split = zone is DockZone.Left or DockZone.Top
            ? new DockSplitNode(orientation, group, target)
            : new DockSplitNode(orientation, target, group);

        Replace(target, split);
    }

    /// <summary>Takes a panel out of the arrangement altogether.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it was in it.</returns>
    public bool RemovePanel(string id) {
        ArgumentNullException.ThrowIfNull(id);

        var removed = false;

        foreach (var group in Groups()) {
            removed |= group.Remove(id);
        }

        if (removed) {
            Prune();
        }

        return removed;
    }

    /// <summary>Takes a panel out of the docked tree and puts it in a window of its own.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="x">Where the window goes.</param>
    /// <param name="y">Ditto.</param>
    /// <param name="width">How big it is.</param>
    /// <param name="height">Ditto.</param>
    public void Float(string id, float x, float y, float width = 320f, float height = 240f) {
        ArgumentNullException.ThrowIfNull(id);

        RemovePanel(id);
        AddFloating(new DockFloat(new DockGroupNode(id), x, y, width, height));
    }

    /// <summary>Swaps one node of the tree for another.</summary>
    /// <param name="node">What to replace.</param>
    /// <param name="replacement">What to put there.</param>
    /// <returns>Whether the node was in the tree.</returns>
    public bool Replace(DockNode node, DockNode replacement) {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(replacement);

        if (ReferenceEquals(Root, node)) {
            Root = replacement;
            return true;
        }

        return Root is not null && Replace(Root, node, replacement);
    }

    /// <summary>Drops the empty groups and collapses the splits that lost a half.</summary>
    /// <remarks>
    ///     ⚠ <b>Run after every removal rather than left for the view to skip over.</b> A split with
    ///     one half is a splitter with nothing on one side of it, and a group with no panels is an
    ///     empty tab strip holding open a share of the window. Both are states the tree can be in
    ///     for a moment and neither is one anybody should ever see.
    /// </remarks>
    public void Prune() {
        Root = Prune(Root);

        for (var i = Floating.Count - 1; i >= 0; i--) {
            if (Floating[i].Group.Panels.Count == 0) {
                RemoveFloating(i);
            }
        }
    }

    /// <summary>Which floating entry holds a group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Its index in <see cref="Floating" />, or -1 if it is not floating.</returns>
    /// <remarks>
    ///     ⚠ <b>By reference, which is what makes a window survive a rebuild.</b> A host that opened
    ///     an operating-system window for a floating group has to find that group again after every
    ///     structural change, and the index it was at is not it: docking a panel elsewhere prunes the
    ///     list underneath it. The group object is the one thing that stays the same across
    ///     <see cref="SetFloating" />, which is how a window that has been dragged keeps being the
    ///     same window.
    /// </remarks>
    public int IndexOfFloating(DockGroupNode group) {
        ArgumentNullException.ThrowIfNull(group);

        for (var i = 0; i < Floating.Count; i++) {
            if (ReferenceEquals(Floating[i].Group, group)) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Every group in the docked tree, floating ones excluded.</summary>
    /// <returns>The groups, in order.</returns>
    public List<DockGroupNode> DockedGroups() {
        var groups = new List<DockGroupNode>();
        Root?.Collect(groups);

        return groups;
    }

    /// <summary>Whether a group is still part of the arrangement.</summary>
    /// <param name="group">The group.</param>
    /// <returns>Whether it is.</returns>
    public bool Contains(DockGroupNode group) {
        foreach (var candidate in Groups()) {
            if (ReferenceEquals(candidate, group)) {
                return true;
            }
        }

        return false;
    }

    static DockNode? Prune(DockNode? node) {
        switch (node) {
            case DockGroupNode group:
                return group.Panels.Count == 0 ? null : group;

            case DockSplitNode split: {
                var first = Prune(split.First);
                var second = Prune(split.Second);

                if (first is null) {
                    return second;
                }

                if (second is null) {
                    return first;
                }

                split.First = first;
                split.Second = second;

                return split;
            }

            default:
                return null;
        }
    }

    static bool Replace(DockNode node, DockNode target, DockNode replacement) {
        if (node is not DockSplitNode split) {
            return false;
        }

        if (ReferenceEquals(split.First, target)) {
            split.First = replacement;
            return true;
        }

        if (ReferenceEquals(split.Second, target)) {
            split.Second = replacement;
            return true;
        }

        return Replace(split.First, target, replacement) || Replace(split.Second, target, replacement);
    }
}
