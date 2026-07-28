// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>Where a panel dropped on a group ends up.</summary>
public enum DockZone : byte {
    /// <summary>As another tab of the group it was dropped on.</summary>
    Center,

    /// <summary>In a new group to its left.</summary>
    Left,

    /// <summary>To its right.</summary>
    Right,

    /// <summary>Above it.</summary>
    Top,

    /// <summary>Below it.</summary>
    Bottom
}

/// <summary>One node of a docking arrangement.</summary>
/// <remarks>
///     <para>
///         <b>A model, entirely separate from the elements that show it.</b> The arrangement is what
///         gets saved, restored, reset to a preset and compared against one; the elements are what
///         happens to be on screen. Every docking implementation that skipped this step ended up
///         reconstructing the arrangement by walking its own widgets, which works until a panel is
///         hidden.
///     </para>
///     <para>
///         ⚠ <b>Splits are binary.</b> A three-way split is a split whose second half is a split,
///         which is what every docking system stores internally whatever it draws — and it is what
///         makes a splitter a thing between exactly two neighbours rather than a thing that has to
///         know which of N siblings it is between.
///     </para>
/// </remarks>
public abstract class DockNode {
    /// <summary>Writes it as a YAML node.</summary>
    /// <returns>The node.</returns>
    public abstract YamlNode ToYaml();

    /// <summary>Reads one back.</summary>
    /// <param name="node">The YAML.</param>
    /// <returns>The node, or <c>null</c> if it is not one.</returns>
    public static DockNode? FromYaml(YamlNode? node) {
        if (node is not YamlMapping mapping) {
            return null;
        }

        // The discriminator is which key is present rather than a `kind` field. A split has halves
        // and a group has panels; a mapping with both is a file somebody edited into nonsense, and
        // taking the split is a better answer than throwing at somebody's window layout.
        return mapping["first"] is not null || mapping["second"] is not null
            ? DockSplitNode.Read(mapping)
            : DockGroupNode.Read(mapping);
    }

    /// <summary>Every group in this subtree, in order.</summary>
    /// <param name="into">Where to put them.</param>
    public abstract void Collect(List<DockGroupNode> into);
}

/// <summary>Two arrangements side by side, with a ratio between them.</summary>
public sealed class DockSplitNode : DockNode {
    /// <summary>Creates a split.</summary>
    /// <param name="orientation">Which way the two halves are laid out.</param>
    /// <param name="first">The left or upper half.</param>
    /// <param name="second">The right or lower half.</param>
    /// <param name="ratio">How much of the space the first half takes, from zero to one.</param>
    public DockSplitNode(Orientation orientation, DockNode first, DockNode second, float ratio = 0.5f) {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        Orientation = orientation;
        First = first;
        Second = second;
        Ratio = Math.Clamp(ratio, MinimumRatio, 1f - MinimumRatio);
    }

    /// <summary>How small either half may be squeezed, as a fraction.</summary>
    /// <remarks>
    ///     ⚠ <b>Not zero.</b> A half dragged to nothing is a panel with no way back — there is no
    ///     splitter to grab, because the splitter is between two things and one of them has no
    ///     width. Every docking system that allowed it grew a "reset layout" command to undo it.
    /// </remarks>
    public const float MinimumRatio = 0.05f;

    /// <summary>Which way the two halves are laid out.</summary>
    public Orientation Orientation { get; }

    /// <summary>The left or upper half.</summary>
    public DockNode First { get; set; }

    /// <summary>The right or lower half.</summary>
    public DockNode Second { get; set; }

    /// <summary>How much of the space the first half takes.</summary>
    public float Ratio { get; set; }

    /// <inheritdoc />
    public override YamlNode ToYaml() =>
        new YamlMapping()
            .Set("orientation", new YamlScalar(Orientation == Orientation.Vertical ? "vertical" : "horizontal"))
            .Set("ratio", new YamlScalar(Ratio.ToString("0.####", CultureInfo.InvariantCulture)))
            .Set("first", First.ToYaml())
            .Set("second", Second.ToYaml());

    /// <inheritdoc />
    public override void Collect(List<DockGroupNode> into) {
        First.Collect(into);
        Second.Collect(into);
    }

    internal static DockNode? Read(YamlMapping mapping) {
        var first = FromYaml(mapping["first"]);
        var second = FromYaml(mapping["second"]);

        // ⚠ A split with one half is not an error to report, it is a split that is not one. Saved
        // layouts outlive the panels in them — a plugin that is no longer installed takes its group
        // with it — so collapsing to whichever half survived is the behaviour that keeps somebody's
        // window arrangement usable instead of resetting it.
        if (first is null) {
            return second;
        }

        if (second is null) {
            return first;
        }

        var orientation = (mapping["orientation"] as YamlScalar)?.Value == "vertical"
            ? Orientation.Vertical
            : Orientation.Horizontal;

        var ratio = float.TryParse(
            (mapping["ratio"] as YamlScalar)?.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : 0.5f;

        return new DockSplitNode(orientation, first, second, ratio);
    }
}

/// <summary>A stack of panels showing one at a time.</summary>
public sealed class DockGroupNode : DockNode {
    readonly List<string> panels = [];

    /// <summary>Creates a group.</summary>
    /// <param name="panels">The panels in it, by id.</param>
    public DockGroupNode(params ReadOnlySpan<string> panels) {
        foreach (var panel in panels) {
            this.panels.Add(panel);
        }
    }

    /// <summary>The panels in it, by id, in tab order.</summary>
    public IReadOnlyList<string> Panels => panels;

    /// <summary>Which of them is showing.</summary>
    public int Selected { get; set; }

    /// <summary>Where a panel sits in the tab order, or -1 if it is not in this group.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>The index.</returns>
    public int IndexOf(string id) => panels.IndexOf(id);

    /// <summary>Adds a panel.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="index">Where in the tab order, or -1 for the end.</param>
    public void Add(string id, int index = -1) {
        ArgumentNullException.ThrowIfNull(id);

        panels.Insert(index < 0 || index > panels.Count ? panels.Count : index, id);
        Selected = IndexOf(id);
    }

    /// <summary>Takes a panel out.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(string id) {
        var index = IndexOf(id);
        if (index < 0) {
            return false;
        }

        panels.RemoveAt(index);
        Selected = Math.Clamp(Selected > index ? Selected - 1 : Selected, 0, Math.Max(0, panels.Count - 1));

        return true;
    }

    /// <inheritdoc />
    public override YamlNode ToYaml() {
        var list = new YamlSequence { Style = YamlCollectionStyle.Flow };

        foreach (var panel in panels) {
            list.Add(new YamlScalar(panel));
        }

        return new YamlMapping()
            .Set("panels", list)
            .Set("selected", new YamlScalar(Selected.ToString(CultureInfo.InvariantCulture)));
    }

    /// <inheritdoc />
    public override void Collect(List<DockGroupNode> into) => into.Add(this);

    internal static DockNode? Read(YamlMapping mapping) {
        var group = new DockGroupNode();

        if (mapping["panels"] is YamlSequence list) {
            foreach (var item in list) {
                if (item is YamlScalar panel && !string.IsNullOrEmpty(panel.Value)) {
                    group.panels.Add(panel.Value);
                }
            }
        }

        // An empty group is not a group. It happens whenever a saved layout names panels that no
        // longer exist, and keeping it would leave an empty tab strip taking up half the window.
        if (group.panels.Count == 0) {
            return null;
        }

        group.Selected = int.TryParse(
            (mapping["selected"] as YamlScalar)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var selected
        )
            ? Math.Clamp(selected, 0, group.panels.Count - 1)
            : 0;

        return group;
    }
}

/// <summary>A group floating over the docked arrangement.</summary>
/// <param name="Group">What is in it.</param>
/// <param name="X">Where its left edge is, in document space.</param>
/// <param name="Y">Its top edge.</param>
/// <param name="Width">How wide it is.</param>
/// <param name="Height">How tall.</param>
/// <remarks>
///     ⚠ <b>Floating within the document, not in an OS window of its own.</b> Doc 11 asks the editor
///     for "undock to a separate OS window", and a second window is a second surface, a second
///     swapchain and a second input queue — all of which belong to <c>Vixen.Platform</c> and the
///     application head. What this assembly can do is the half above that line: a group that is not
///     in the docked tree, positioned and sized in its own coordinates. Promoting one to a real
///     window is then the app's business and this record is what it would be handed.
/// </remarks>
public readonly record struct DockFloat(DockGroupNode Group, float X, float Y, float Width, float Height) {
    /// <summary>Writes it as a YAML node.</summary>
    /// <returns>The node.</returns>
    public YamlNode ToYaml() =>
        new YamlMapping()
            .Set("x", Number(X))
            .Set("y", Number(Y))
            .Set("width", Number(Width))
            .Set("height", Number(Height))
            .Set("group", Group.ToYaml());

    internal static DockFloat? Read(YamlNode node) {
        if (node is not YamlMapping mapping || DockGroupNode.Read(mapping["group"] as YamlMapping ?? new YamlMapping()) is not DockGroupNode group) {
            return null;
        }

        return new DockFloat(
            group,
            Number(mapping["x"]),
            Number(mapping["y"]),
            Number(mapping["width"], 320f),
            Number(mapping["height"], 240f)
        );
    }

    static YamlScalar Number(float value) => new(value.ToString("0.###", CultureInfo.InvariantCulture));

    static float Number(YamlNode? node, float fallback = 0f) =>
        float.TryParse(
            (node as YamlScalar)?.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : fallback;
}

/// <summary>A whole docking arrangement: what is docked, and what is floating over it.</summary>
/// <remarks>
///     ⚠ <b>YAML rather than the binary serializer, and rather than reflection.</b> A window layout
///     is something a person diffs, checks in and occasionally repairs by hand — the same argument
///     doc 11 makes for project settings — and it is built here node by node rather than through
///     <c>YamlSerializer</c> so that nothing has to be registered, nothing is reflected over, and
///     the format is whatever this file says it is rather than whatever a converter decided.
/// </remarks>
public sealed partial class DockLayout {
    readonly List<DockFloat> floating = [];

    /// <summary>The docked arrangement, or <c>null</c> if nothing is docked.</summary>
    public DockNode? Root { get; set; }

    /// <summary>The groups floating over it.</summary>
    public IReadOnlyList<DockFloat> Floating => floating;

    /// <summary>Adds a floating group.</summary>
    /// <param name="window">The group and where it is.</param>
    public void AddFloating(DockFloat window) => floating.Add(window);

    /// <summary>Takes a floating group out.</summary>
    /// <param name="index">Which one.</param>
    public void RemoveFloating(int index) => floating.RemoveAt(index);

    /// <summary>Replaces a floating group, which is how one is moved or resized.</summary>
    /// <param name="index">Which one.</param>
    /// <param name="window">Its new position and size.</param>
    public void SetFloating(int index, DockFloat window) => floating[index] = window;

    /// <summary>Every group in the arrangement, docked ones first.</summary>
    public List<DockGroupNode> Groups() {
        var groups = new List<DockGroupNode>();
        Root?.Collect(groups);

        foreach (var window in floating) {
            groups.Add(window.Group);
        }

        return groups;
    }

    /// <summary>Which group holds a panel, and where in it.</summary>
    /// <param name="id">The panel's id.</param>
    /// <returns>The group and the index, or <c>null</c> if nothing holds it.</returns>
    public (DockGroupNode Group, int Index)? Find(string id) {
        ArgumentNullException.ThrowIfNull(id);

        foreach (var group in Groups()) {
            var index = group.IndexOf(id);

            if (index >= 0) {
                return (group, index);
            }
        }

        return null;
    }

    /// <summary>Writes the arrangement as YAML.</summary>
    /// <returns>The text.</returns>
    public string Save() {
        var document = new YamlMapping();

        if (Root is { } root) {
            document.Set("root", root.ToYaml());
        }

        if (floating.Count > 0) {
            var windows = new YamlSequence();

            foreach (var window in floating) {
                windows.Add(window.ToYaml());
            }

            document.Set("floating", windows);
        }

        return YamlWriter.Write(document);
    }

    /// <summary>Reads an arrangement back.</summary>
    /// <param name="yaml">The text.</param>
    /// <returns>The arrangement.</returns>
    /// <remarks>
    ///     ⚠ <b>Never throws on a layout that has gone stale.</b> Panels disappear between versions,
    ///     plugins are uninstalled, and a file gets hand-edited — and the answer to all three is a
    ///     usable arrangement missing whatever is missing, not an exception at start-up in front of
    ///     somebody who wanted to open a project. Empty groups vanish and half-empty splits collapse
    ///     into whichever half survived.
    /// </remarks>
    public static DockLayout Load(string yaml) {
        ArgumentNullException.ThrowIfNull(yaml);

        var layout = new DockLayout();

        if (YamlReader.Read(yaml) is not YamlMapping document) {
            return layout;
        }

        layout.Root = DockNode.FromYaml(document["root"]);

        if (document["floating"] is YamlSequence windows) {
            foreach (var window in windows) {
                if (DockFloat.Read(window) is { } floated) {
                    layout.floating.Add(floated);
                }
            }
        }

        return layout;
    }
}
