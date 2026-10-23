// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.App;

/// <summary>The inspector panels that are open, and the preferences and curve presets every one of them shares.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D2's rule for a panel's state, applied to the inspector (#1294).</b> The
///         per-visit pair, the factory both inspector panels go through, the preferences restated
///         across them and the curve-preset lines on their menus were <c>EditorApplication</c>'s; they
///         are this model's, which the application builds once and the panels read on every visit.
///     </para>
///     <para>
///         ⚠ <b>Which selection is shown is not this model's to decide.</b> There is one selection per
///         open scene and one for the project, and arbitrating between them is the composition root's
///         job — doc 50 § D2 keeps it there — so <c>EditorApplication.FollowSelection</c> decides and
///         this model is told: <see cref="ShowAssets" /> or <see cref="ShowEntities" />.
///     </para>
///     <para>
///         ⚠ <b>Not behind <c>IWorkspace</c>, and that is the third slice's finding restated.</b>
///         <c>Vixen.Editor.Ui</c> refuses <c>Vixen.Editor.Core</c>, and <c>InspectorView</c>,
///         <c>ComponentsView</c> and <c>SceneDocument</c> are not words an interface there can say;
///         the model is the inspector context's own, in the assembly that builds the panel.
///     </para>
/// </remarks>
sealed class InspectorPanels {
    readonly EditorShell shell;
    readonly IEditorRegistry extensions;
    readonly IReadOnlyList<IComponentBridge> bridges;
    readonly Func<SceneDocument> scene;
    readonly Func<EditorPreferences> preferences;
    readonly Action writePreferences;
    readonly Func<CurvePresetLibrary> presets;
    readonly Action writePresets;
    readonly Action showSelection;

    /// <summary>Every inspector panel that is open, with the component section under each.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A list because doc 20 § B1 asks for more than one, and "a second instance" was
    ///         the hard half rather than "a second feature".</b> The panel already floats and already
    ///         has a lock; what it did not have was a second of it, because the application held one view
    ///         and one component section in two fields and <c>EditorApplication.ShowSelection</c> wrote to
    ///         both. Pinning one inspector to entity A while selecting entity B is how anybody copies
    ///         a value between two things, and it is the first thing somebody who has used either
    ///         reference editor tries.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The lock is what arbitrates, and it already existed.</b>
    ///         <c>InspectorView.Inspect</c> refuses while locked — so a locked panel needed nothing
    ///         adding — but the component section under it is not the inspector's rows and knew
    ///         nothing about the lock, so a locked panel's foldouts followed the selection anyway.
    ///         A pane is skipped whole.
    ///     </para>
    ///     <para>
    ///         Both entries go in and come out together: a panel's factory builds the section inside
    ///         the view's scroll region, and its <c>Closed</c> takes the pair out. Nothing here
    ///         assumes there is a first one.
    ///     </para>
    /// </remarks>
    readonly List<(InspectorView View, ComponentsView Components)> inspectors = [];

    /// <summary>Builds the model over what the application owns.</summary>
    /// <param name="shell">The shell, whose dialogs name a curve preset.</param>
    /// <param name="session">The project session, whose code build changes what a component section can show.</param>
    /// <param name="extensions">This editor's registry, which every inspector reads.</param>
    /// <param name="bridges">Which components and behaviours the sections can show.</param>
    /// <param name="scene">The active scene, asked for at each visit — it changes under the editor.</param>
    /// <param name="preferences">
    ///     The preferences, asked for at each read: ⚠ <c>LoadPreferences</c> and the settings window's
    ///     Revert both <i>replace</i> the object, so a captured one is the file as it was at start-up.
    /// </param>
    /// <param name="writePreferences">Writes the preferences file.</param>
    /// <param name="presets">The curve presets, asked for at each read, for the preferences' reason.</param>
    /// <param name="writePresets">Writes the presets file.</param>
    /// <param name="showSelection">
    ///     The application's arbitration, run when a panel opens or a lock comes off — both are moments a
    ///     panel is behind the selection without the selection having changed.
    /// </param>
    public InspectorPanels(
        EditorShell shell,
        IProjectSession session,
        IEditorRegistry extensions,
        IReadOnlyList<IComponentBridge> bridges,
        Func<SceneDocument> scene,
        Func<EditorPreferences> preferences,
        Action writePreferences,
        Func<CurvePresetLibrary> presets,
        Action writePresets,
        Action showSelection
    ) {
        this.shell = shell;
        this.extensions = extensions;
        this.bridges = bridges;
        this.scene = scene;
        this.preferences = preferences;
        this.writePreferences = writePreferences;
        this.presets = presets;
        this.writePresets = writePresets;
        this.showSelection = showSelection;

        // ⚠ Every section, because a rebuilt assembly is new types under the old names: rows built over
        // the previous build's binders show the members it had, so a member the build added stays
        // invisible until they are built again. Followed here rather than forwarded by the
        // application (#1293).
        session.CodeBuilt += () => Rebuild();
    }

    EditorPreferences Preferences => preferences();

    CurvePresetLibrary Presets => presets();

    /// <summary>Puts assets into every unlocked inspector, and empties its component section.</summary>
    /// <param name="assets">One view object per selected asset.</param>
    internal void ShowAssets(object[] assets) {
        foreach (var (view, section) in inspectors) {
            // ⚠ A locked panel is skipped whole rather than half. `InspectorView.Inspect` already
            // refuses while locked — that is what a lock is — but the component section under it
            // is not the inspector's rows and knew nothing about the lock, so a locked panel's
            // foldouts followed the selection anyway and the two halves of one panel showed two
            // different things.
            if (view.IsLocked) {
                continue;
            }

            // ⚠ Dropped rather than left pointing at the last entity. An asset did not come from a
            // prefab, and a stale pairing keyed by reference would answer for whatever boxed value
            // happened to land on the same object.
            view.Prefab = null;
            view.Inspect(assets);

            // An asset has no components, and leaving the last entity's foldouts under it would be
            // a panel showing two different things at once.
            section.Pair(null);
            section.Show(Entity.Null);
        }
    }

    /// <summary>Puts a scene's selected entities into every unlocked inspector.</summary>
    /// <param name="document">The scene whose selection won.</param>
    /// <param name="sources">What pairs each view object with the prefab it came from.</param>
    /// <param name="shown">One view object per selected entity.</param>
    /// <param name="own">Whether that scene is the editor's own, the one the component sections write to.</param>
    internal void ShowEntities(SceneDocument document, PrefabSource sources, List<object> shown, bool own) {
        foreach (var (view, section) in inspectors) {
            // ⚠ Skipped whole, both halves. See the asset branch above for what the second half of
            // that closes.
            if (view.IsLocked) {
                continue;
            }

            // ⚠ The document whose entities these are, not the editor's own scene. An inspector edit
            // is recorded on the stack of the document it changed, and a scene opened as an asset
            // has one of its own — so an edit made here with the wrong document set would be undone
            // by a Ctrl+Z aimed at something else entirely.
            view.EditedDocument = document;
            view.Prefab = sources;

            // ⚠ Assigned before `Inspect`, because building the rows is what asks the source whether
            // each member is overridden. Setting it afterwards would draw one unmarked panel and only
            // start telling the truth at the next selection.
            // ⚠ Spread rather than passed, because `Inspect` takes `params ReadOnlySpan<object>` and a
            // `List<object>` binds to it as one element — an inspector showing the list.
            view.Inspect([.. shown]);

            // ⚠ Only this editor's own scene. The foldouts write through `scene.Stack`, and a
            // document opened as an asset has a stack of its own — showing its entity's components
            // here would put the edit on the wrong one, which is the hazard the line above guards
            // for the rows.
            section.Pair(own ? sources : null);

            section.Show(
                own && document.Selection.Count > 0
                    ? document.Selection[0]
                    : Entity.Null
            );
        }
    }

    /// <summary>Rebuilds every component section, or only those showing one entity.</summary>
    /// <param name="changed">The entity whose components changed, or <see langword="null" /> for all — a code build.</param>
    internal void Rebuild(Entity? changed = null) {
        foreach (var (_, section) in inspectors) {
            if (changed is not { } entity || section.Entity == entity) {
                section.Rebuild();
            }
        }
    }

    /// <summary>Reads every inspector's rows back from what they are over, without rebuilding them.</summary>
    /// <param name="sections">Whether the component sections are read back too — an undo, not a drag.</param>
    internal void Reload(bool sections) {
        foreach (var (view, section) in inspectors) {
            view.Reload();

            if (sections) {
                section.Reload();
            }
        }
    }

    /// <summary>Describes one inspector panel, which there may be more than one of.</summary>
    /// <param name="id">The panel's id, which is what a saved arrangement carries it back by.</param>
    /// <param name="title">What the tab says.</param>
    /// <returns>The descriptor.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20 § B1's "multiple inspector windows", and what was missing was a second
    ///         <i>instance</i> rather than a second feature.</b> The panel already floats and already
    ///         locks; this application held one view and one component section in two fields, and
    ///         every place that fed them wrote to exactly one. <see cref="inspectors" /> is the list
    ///         now and this is the factory both entries go through, so the second inspector is the
    ///         first one — not a cut-down copy that quietly does less.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A panel's factory runs again every time it is reopened</b>, so everything here is
    ///         per visit: the pair is added on the way in and removed by <c>Closed</c> on the way
    ///         out, and the preferences are read then rather than captured.
    ///     </para>
    /// </remarks>
    internal PanelDescriptor Describe(string id, StringId title) {
        InspectorView? opened = null;

        return new PanelDescriptor(
            id,
            title,
            panel => {
                // ⚠ `InspectorView` owns a scroll region of its own and keeps its header out of it
                // on purpose, so that the search box and the lock cannot scroll away from somebody
                // who is using them to find the row they scrolled past. A panel that scrolled the
                // whole view would put the header back inside a scroller and give the wheel two bars
                // to choose between.
                panel.Scrolls = false;

                var view = panel.Add<InspectorView>();

                view.EditedDocument = scene();

                // This editor's registry rather than the process-wide default — see `Configure`.
                view.Extensions = extensions;

                // ⚠ Under the inspector's rows rather than inside its model. `InspectorView` draws
                // the members of one described type; which *types* are on an entity is a different
                // question, and one it deliberately cannot ask — see `ComponentsView`. What it does
                // share is the scroll region: an entity with six components is longer than any
                // panel, and two independent scroll regions would leave half the answer off screen
                // whichever one you moved.
                var section = view.Scroll.Content.Add<ComponentsView>();

                section.Attach(scene(), bridges, extensions);

                // ⚠ Restored before the subscription, so putting the foldouts back where the user
                // left them is not itself recorded as a rearrangement. The order is a preference
                // rather than anything about the entity — see `ComponentsView.Order` — which is why
                // it lives in the preferences file and not in the scene.
                section.Order = Preferences.ComponentOrder;

                section.Reordered += arranged => {
                    Preferences.ComponentOrder = [.. arranged];
                    writePreferences();
                    RestatePreferences(section);
                };

                // ⚠ Restored before the subscription, on the same terms as the order above and as
                // the browser's view toggle: writing `IsRaw` moves the button, which raises the
                // control's change — so a subscription made first would record the restore as the
                // user having pressed it. `Reveal` guards the round trip as well, and both halves
                // are cheap next to the class of bug they close.
                section.IsRaw = Preferences.InspectorRawMode;

                section.RawChanged += on => {
                    Preferences.InspectorRawMode = on;
                    writePreferences();
                    RestatePreferences(section);
                };

                // ⚠ Pinned members are a preference about how somebody works — per type and member
                // rather than per object — so they live in the same file as the foldout order and
                // for the same reason, and every open inspector is restated when one changes. Two
                // panels disagreeing about which member is pinned would be two answers to one
                // question.
                section.Pinned = Preferences.PinnedMembers;

                section.PinsChanged += pinned => {
                    Preferences.PinnedMembers = [.. pinned];
                    writePreferences();
                    RestatePreferences(section);
                };

                // ⚠ After it is in the tree, because the menu is a child of the document root and a
                // control has no document until it is added to one.
                view.Contextualise();

                // Doc 20 § B5's curve presets, on the row's own menu because that is where a curve
                // is — and on both menus, because a curve can be a member of the inspected object or
                // a member of a component on it, and those are two panels. See `CurvePresetLibrary`
                // for where the library lives and why.
                CurvePresetLines(view.Contextualise(), () => view.AimedRow);

                // ⚠ And the component section's own, which is a second menu rather than a second
                // caller of the first. `InspectorView`'s is attached to its `Body`, and this control
                // is a sibling of that body inside the same scroll region — so until now a secondary
                // click on a component row reached no menu at all.
                CurvePresetLines(section.Contextualise(), () => section.AimedRow);

                // ⚠ The panel refused every selection while it was locked, so it is showing
                // something stale the moment the lock comes off — and nothing else would tell it,
                // because the selection has not changed since.
                view.LockChanged += held => {
                    if (!held.IsLocked) {
                        showSelection();
                    }
                };

                opened = view;
                inspectors.Add((view, section));

                // The rows were built against the previous instance of this panel, so what is
                // selected has to be pushed into the new one rather than waited for — and from
                // whichever selection the inspector was already following, which is what the two
                // fields behind `ShowSelection` are for.
                showSelection();
            }
        ) {
            // ⚠ The other half of holding the pair. `FollowSelection` runs every frame and hands
            // whatever is selected to every open inspector — so a panel closed while the editor is
            // running leaves a dead entry pointing at a view whose elements have been removed, and
            // the next selection change throws from inside `Rebuild`. Applying a preset that does
            // not name the inspector is one of the ways it closes. See `PanelDescriptor.Closed`.
            Closed = () => {
                inspectors.RemoveAll(entry => ReferenceEquals(entry.View, opened));
                opened = null;
            }
        };
    }

    /// <summary>Adds the curve-preset lines to a panel's context menu.</summary>
    /// <param name="menu">The menu to add to.</param>
    /// <param name="aimed">What row a secondary click landed on, asked at the moment it is needed.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 20 § B5's last row, and the surface is the row's own menu because that is
    ///         where the curve is.</b> A preset is applied through <c>CurveEditor.Apply</c>, which
    ///         copies the keys into the curve the object already holds rather than swapping the
    ///         object — that is what makes applying one an undoable edit rather than an alias, and it
    ///         is why the drawer's <c>CurveChanged</c> is what writes it home.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Rebuilt every time the menu opens.</b> A submenu filled once holds the presets
    ///         that existed when the panel was built, and the first thing anybody does after saving
    ///         one is look for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both menus, because there are two panels drawing rows and a curve can be in
    ///         either.</b> <c>InspectorView</c> draws the members of one described type — a scene
    ///         entity, an asset — and <c>ComponentsView</c> draws what is on an entity; they are
    ///         siblings inside one scroll region with a context menu each. Lines added to one only
    ///         would be a feature that works on a curve in a custom inspector and silently does not
    ///         on a curve in a component, which is the shape of gap nobody reports.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Curves only, and the gradient half of the doc row is refused rather than
    ///         forgotten</b> — see <see cref="CurvePresetLibrary" /> for the sweep that settles it.
    ///     </para>
    /// </remarks>
    void CurvePresetLines(ContextMenu menu, Func<InspectorRow?> aimed) {
        menu.AddSeparator();

        var apply = menu.AddSubmenu("Curve Presets");
        var save = menu.AddItem("Save Curve as Preset…");
        var forget = menu.AddSubmenu("Forget Curve Preset");

        save.Clicked += _ => Keep(aimed);

        menu.OpenChanged += (_, isOpen) => {
            if (!isOpen) {
                return;
            }

            var editing = Curve(aimed) is not null;

            save.Disabled = !editing;
            if (apply.Opener is { } applyLine) {
                applyLine.Disabled = !editing;
            }
            if (forget.Opener is { } forgetLine) {
                forgetLine.Disabled = !editing || Presets.Curves.Count == 0;
            }

            Fill(apply, editing ? Presets.Offered() : [], name => Apply(aimed, name));

            Fill(
                forget,
                editing ? [.. Presets.Curves.Select(saved => (saved.Name, CurvePresetLibrary.ToCurve(saved)))] : [],
                name => {
                    if (Presets.Forget(name)) {
                        writePresets();
                    }
                }
            );
        };

        static void Fill(Menu submenu, IReadOnlyList<(string Name, AnimationCurve Curve)> offered, Action<string> run) {
            while (submenu.Items.Count > 0) {
                submenu.Items[^1].Remove();
            }

            foreach (var (name, _) in offered) {
                var line = submenu.AddItem(name);

                line.Clicked += _ => run(name);
            }
        }
    }

    /// <summary>The curve editor the aimed row is drawing, if it is drawing one.</summary>
    /// <remarks>
    ///     ⚠ <b>The control rather than the member's value.</b> <c>CurveEditor.Apply</c> is what
    ///     keeps the object the caller is holding, and it is also what raises the change the drawer
    ///     writes home; reaching past it to the boxed value would edit a copy nothing is bound to.
    /// </remarks>
    static CurveEditor? Curve(Func<InspectorRow?> aimed) => aimed()?.Editor as CurveEditor;

    /// <summary>Puts a named shape into the aimed row's curve.</summary>
    /// <param name="aimed">What row the click landed on.</param>
    /// <param name="name">Which shape.</param>
    void Apply(Func<InspectorRow?> aimed, string name) {
        if (Curve(aimed) is not { } editor) {
            return;
        }

        foreach (var (offered, curve) in Presets.Offered()) {
            if (string.Equals(offered, name, StringComparison.Ordinal)) {
                editor.Apply(curve);
                return;
            }
        }
    }

    /// <summary>Asks for a name and keeps the aimed row's curve under it.</summary>
    /// <param name="aimed">What row the click landed on.</param>
    /// <remarks>
    ///     ⚠ <b>The keys are copied out of the control's curve.</b> A library holding the object an
    ///     inspector row is editing would be a preset that changed every time somebody dragged the
    ///     key it was made from.
    /// </remarks>
    void Keep(Func<InspectorRow?> aimed) {
        if (Curve(aimed) is not { } editor) {
            return;
        }

        var shape = editor.Curve;

        _ = Ask();

        async Task Ask() {
            var typed = await shell.Dialogs.PromptAsync(
                "Save Curve as Preset",
                "It is kept beside your layouts and keymap rather than in the project.",
                confirm: "Save"
            ).ConfigureAwait(true);

            if (typed is not { Length: > 0 } name || string.IsNullOrWhiteSpace(name)) {
                return;
            }

            Presets.Save(name.Trim(), shape);
            writePresets();
        }
    }

    /// <summary>Brings every open inspector's component section into line with the preferences.</summary>
    /// <remarks>
    ///     ⚠ <b>What a second inspector needs and one did not.</b> The order, the raw toggle and the
    ///     pinned members are all preferences rather than facts about a panel, so a change made in
    ///     one panel is a change to all of them — and two panels showing the same entity with
    ///     different foldout orders is a picture that says the preference is per panel, which it is
    ///     not.
    /// </remarks>
    /// <param name="origin">
    ///     The section that raised the change, which is skipped. ⚠ It already holds the new value —
    ///     it is where the value came from — and writing it back re-runs its own <c>Rebuild</c> in
    ///     the middle of the drop handler that raised it, which tears down the rows a drag has just
    ///     finished with.
    /// </param>
    void RestatePreferences(ComponentsView? origin = null) {
        foreach (var (_, section) in inspectors) {
            if (ReferenceEquals(section, origin)) {
                continue;
            }

            section.Order = Preferences.ComponentOrder;
            section.IsRaw = Preferences.InspectorRawMode;
            section.Pinned = Preferences.PinnedMembers;
        }
    }
}
