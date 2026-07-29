// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Prefabs;

/// <summary>A prefab, open in isolation: a banner saying so, and the subtree it holds.</summary>
/// <remarks>
///     <para>
///         <b>The banner is the feature, not decoration.</b> The failure an isolated prefab mode
///         exists to prevent is somebody editing a prefab while believing they are editing a level —
///         and every editor that has this mode marks it, because the two look identical otherwise.
///         The banner also carries the one thing that is true here and nowhere else: this document
///         must have exactly one root, and it says so the moment it does not.
///     </para>
///     <para>
///         ⚠ <b>The root count is checked as the structure changes and refused at the save.</b>
///         Refusing the edit that made a second root would mean an author cannot create an entity
///         before parenting it. So the banner turns into a complaint, and
///         <see cref="PrefabFileWriter" /> is what actually refuses — which is the moment work would
///         otherwise be lost.
///     </para>
/// </remarks>
public sealed class PrefabView : Control {
    SceneDocument? document;
    SceneHierarchyView? hierarchy;

    /// <inheritdoc />
    protected override string TagName => "prefab-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip that says this is a prefab and not a level.</summary>
    public Alert Banner { get; private set; } = null!;

    /// <summary>Where the tree goes.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>The tree over the prefab's entities.</summary>
    public SceneHierarchyView? Hierarchy => hierarchy;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Banner = Part<Alert>();
        Body = Part("prefab-body");
    }

    /// <summary>Shows a prefab.</summary>
    /// <param name="prefab">The document, opened into a world of its own.</param>
    public void Show(SceneDocument prefab) {
        ArgumentNullException.ThrowIfNull(prefab);

        if (document is { } previous) {
            previous.StructureChanged -= Restate;
            hierarchy?.Detach();
        }

        document = prefab;
        hierarchy = new(prefab, Body);

        prefab.StructureChanged += Restate;
        Restate(prefab);
    }

    /// <summary>Whether the prefab is in a state that can be saved.</summary>
    public bool IsSavable => document?.Roots.Count == 1;

    void Restate(SceneDocument prefab) {
        var roots = prefab.Roots.Count;

        Banner.Title = "Editing a prefab in isolation";

        Banner.Message = roots switch {
            1 => $"Changes here apply to every instance of '{prefab.Title.Peek()}' the next time it is imported.",
            0 => "A prefab needs one root entity. This one has none, so it cannot be saved yet.",
            _ => $"A prefab has one root and this has {roots}. Parent the loose entities under one before saving."
        };

        if (roots == 1) {
            Banner.RemoveClass("invalid");
        } else {
            Banner.AddClass("invalid");
        }
    }
}
