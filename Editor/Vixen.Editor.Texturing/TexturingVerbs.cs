// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Editor.Ui;

namespace Vixen.Editor.Texturing;

// The verbs, which are what `ITexturing` publishes and every Tools-menu command names — moved here out
// of TexturingModule.cs unchanged for #1306 (doc 50 § D2): the bodies are the ones the commands always
// ran, made public because a published service is a door a plugin comes through. `ITexturing` says why
// the service is the module and not a second object. ⚠ A plain comment and not a `///` block: the type's
// documentation is on the other half, and a second `<summary>` on a partial is concatenated into it.
public sealed partial class TexturingModule {
    /// <summary>Turns the graph on the canvas into a material in the project.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The route <a href="https://github.com/Rikarin/Vixen/issues/1009">#1009</a> says did
    ///         not exist.</b> Everything below the notification is
    ///         <see cref="MaterialBakeRoute.Bake(TextureGraphDocument, string, string, bool, bool)" />, which is the compile the pane runs, the
    ///         evaluator both panes share, and the <see cref="Vixen.Editor.Assets.Materials.ProjectMaterialBaker" /> the command
    ///         line calls. There is one baker and this is a second caller of it, which is the only
    ///         arrangement in which "the same code the CLI runs" is a fact.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The material is named after the graph's file and there is no dialog</b>, which is
    ///         a decision worth stating because it is what an artist will ask about first. A name is
    ///         the set's file name and not its identity — <c>MaterialBakeRecord.SourceAsset</c> is
    ///         that, so re-baking the same graph overwrites its own maps and keeps their GUIDs
    ///         however the file is called. A name control is a panel this verb does not have yet;
    ///         until it does, the graph's own name is the one answer that cannot silently adopt
    ///         another graph's set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Force is now offered, and it is a second verb rather than an argument</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1019">#1019</a>. A map somebody has
    ///         painted over stops the bake, which is § D4's whole point, and a command handler
    ///         carries no argument to force with. ⚠ <b>The sentence here used to send the artist to
    ///         <c>vixen texture bake --force</c> and that verb cannot re-bake a graph</b>: its
    ///         <c>--from</c> is required and reads a folder of maps
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1020">#1020</a>), so running it
    ///         would have written a <em>second</em> set beside the painted one rather than replacing
    ///         anything. <see cref="ForceBakeCommand" /> repeats <em>this</em> bake instead.
    ///     </para>
    /// </remarks>
    public void BakeMaterial() => BakeGraph(document, force: false);

    /// <summary>Turns the graph on the canvas into a material that marches its own height map.</summary>
    /// <remarks>
    ///     <b><a href="https://github.com/Rikarin/Vixen/issues/1103">#1103</a>.</b> The same bake as
    ///     <see cref="BakeMaterial" /> with the one ask <c>MaterialBake.Material</c> is deliberately
    ///     not allowed to make for itself. ⚠ A bake that wrote no height map says so rather than
    ///     composing a march with nothing to sample, which is <see cref="Vixen.Editor.Assets.Materials.MaterialBakeParallax" />'s
    ///     rule and not this verb's.
    /// </remarks>
    public void BakeParallaxMaterial() => BakeGraph(document, force: false, parallax: true);

    /// <summary>Turns the graph on the canvas into a material, forcing or not.</summary>
    /// <param name="subject">The document to bake.</param>
    /// <param name="force">Whether to overwrite outputs somebody has painted over.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The forced run is a closure this arms rather than a flag the verb reads</b>, so
    ///         that <see cref="ForceBakeCommand" /> can answer a refusal from either document with
    ///         one handler and cannot force a bake nobody asked about. See <see cref="Refused" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the document is a <em>parameter</em> rather than the field, which is the
    ///         whole of what makes that closure safe.</b> Reading <c>document</c> inside the forced
    ///         run would force-bake whatever happened to be open when the artist reached for the
    ///         verb — and the ordinary way to meet a painted-over refusal is to go and look at the
    ///         file that was painted, which means opening something else first. Capturing the
    ///         subject is what stops a verb whose whole purpose is overwriting somebody's work from
    ///         overwriting the <em>wrong</em> work.
    ///     </para>
    /// </remarks>
    void BakeGraph(TextureGraphDocument? subject, bool force, bool parallax = false) {
        if (subject is null) {
            shell.Notifications.Show(
                "No graph is open",
                NotificationSeverity.Warning,
                "Bake Material bakes the graph on the Texture Graph canvas. Select a .vxtexgraph and "
                + "run Open Texture Graph first."
            );

            return;
        }

        if (baker is null) {
            shell.Notifications.Show(
                "Nothing baked",
                NotificationSeverity.Warning,
                TexturePreview.Describe(TexturePreview.Blocking(graphics))
            );

            return;
        }

        var outcome = baker.Bake(
            subject,
            Path.GetFileNameWithoutExtension(subject.AssetPath),
            force: force,
            parallax: parallax
        );

        // ⚠ The forced repeat carries the ask, for the same reason it carries the document: an artist
        // who asked for a march and met a painted-over refusal would otherwise press Force and get a
        // material without one, with nothing saying the second bake answered a different question.
        Refused(outcome.Painted ? () => BakeGraph(subject, force: true, parallax: parallax) : null);

        shell.Notifications.Show(
            outcome.Set is null ? "Nothing baked" : "Baked '" + outcome.Set.Name + "'",
            outcome.Set is null ? NotificationSeverity.Warning : NotificationSeverity.Info,
            outcome.Painted ? outcome.Status + " " + ForceAdvice : outcome.Status
        );

        // ⚠ The pane too, and not because the picture changed — it did not. `Bake` compiles through
        // the document, which republishes the compound library; a pane left alone would go on showing
        // the picture it had while the material on disk was made from a different graph. It is also
        // what puts the compiler's warnings back under the pane after a bake reported them.
        Refresh();
    }

    /// <summary>Turns the open layer stack into one material per texture set.</summary>
    /// <remarks>
    ///     <b><a href="https://github.com/Rikarin/Vixen/issues/1029">#1029</a>'s editor half.</b>
    ///     Everything below the notification is <see cref="MaterialBakeRoute" />, which is the
    ///     compile the layers pane runs, the evaluator both panes share and the
    ///     <see cref="Vixen.Editor.Assets.Materials.ProjectMaterialBaker" /> the command line calls.
    /// </remarks>
    public void BakeStackMaterial() => BakeStack(stack, force: false);

    /// <summary>Turns the open layer stack into materials, forcing or not.</summary>
    /// <param name="subject">The stack to bake.</param>
    /// <param name="force">Whether to overwrite outputs somebody has painted over.</param>
    /// <remarks>
    ///     ⚠ <b>The stack is a parameter and not the field, for <see cref="BakeGraph" />'s
    ///     reason</b>: the closure <see cref="Refused" /> arms must repeat the bake that was
    ///     refused, not whatever is open when somebody presses the verb.
    ///     ⚠ <b>One notification for the whole stack rather than one per set.</b> A set that refused
    ///     and a set that wrote are both facts about the same gesture, and an artist reading two
    ///     notifications has to work out which of them was the answer to what they pressed. Every
    ///     set's own sentence is in the detail, named by its set.
    /// </remarks>
    void BakeStack(LayerStackDocument? subject, bool force) {
        if (subject is null) {
            shell.Notifications.Show(
                "No layer stack is open",
                NotificationSeverity.Warning,
                "Bake Material from Layers bakes the stack in the Layer Stack panel. Select a "
                + ".vxlayers and run Open Layer Stack first."
            );

            return;
        }

        // ⚠ Before the device, and it is the verb's answer to #1070 rather than a guard against a
        // crash. A shelf entry bakes *something* — it is a stack — and what it bakes is wrong twice
        // over: a smart material has no model, so every mesh-map mask in it refuses and the artist
        // reads a wall of diagnostics; and the materials it does write land in the project named
        // after a shelf entry nothing in a scene refers to. The gesture should not have been offered,
        // so the answer names the file that should have been baked instead.
        if (subject.IsSmartMaterial) {
            shell.Notifications.Show(
                "Nothing baked",
                NotificationSeverity.Warning,
                $"'{Path.GetFileName(subject.AssetPath)}' is a smart material, which is a stack "
                + "fragment with no model and no meshes — so its mesh-map masks have nothing to "
                + "measure and its materials would belong to no asset. Apply it to a .vxlayers and "
                + "bake that."
            );

            return;
        }

        if (baker is null) {
            shell.Notifications.Show(
                "Nothing baked",
                NotificationSeverity.Warning,
                TexturePreview.Describe(TexturePreview.Blocking(graphics))
            );

            return;
        }

        var outcomes = baker.Bake(subject, Path.GetFileNameWithoutExtension(subject.AssetPath), force: force);
        var written = outcomes.Count(one => one.Set is not null);
        var painted = outcomes.Any(one => one.Painted);

        Refused(painted ? () => BakeStack(subject, force: true) : null);

        var detail = string.Join(" · ", outcomes.Select(one => one.Status));

        shell.Notifications.Show(
            written == 0
                ? "Nothing baked"
                : $"Baked {written.ToString(CultureInfo.InvariantCulture)} of "
                + $"{outcomes.Length.ToString(CultureInfo.InvariantCulture)}",
            written == 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
            painted ? detail + " " + ForceAdvice : detail
        );

        RefreshStack();
    }

    /// <summary>Packs the open stack's layer weights into a splat map and binds it onto the material.</summary>
    /// <remarks>
    ///     <b><a href="https://github.com/Rikarin/Vixen/issues/1124">#1124</a>'s editor half, and the
    ///     verb <a href="https://github.com/Rikarin/Vixen/issues/1073">#1073</a> was missing.</b>
    ///     Everything below the notification is <see cref="MaterialBakeRoute" /> — the same compile
    ///     the layers pane runs, the same evaluator both panes share and the same
    ///     <see cref="Vixen.Editor.Assets.Materials.ProjectMaterialBaker" /> the command line calls, with its naming, its digest,
    ///     its painted-over guard and its provenance block.
    /// </remarks>
    public void BakeSplatMap() => BakeSplat(stack, force: false);

    /// <summary>Packs the weights, forcing or not.</summary>
    /// <param name="subject">The stack to weigh.</param>
    /// <param name="force">Whether to overwrite a splat map somebody has painted over.</param>
    /// <remarks>
    ///     ⚠ <b>A parameter and not the field, for <see cref="BakeStack" />'s reason</b>: the closure
    ///     <see cref="Refused" /> arms has to repeat the write that was refused rather than whatever
    ///     is open when the artist reaches the force verb.
    /// </remarks>
    void BakeSplat(LayerStackDocument? subject, bool force) {
        if (subject is null) {
            shell.Notifications.Show(
                "No layer stack is open",
                NotificationSeverity.Warning,
                "Bake Splat Map from Layers weighs the stack in the Layer Stack panel. Select a "
                + ".vxlayers and run Open Layer Stack first."
            );

            return;
        }

        // ⚠ The same refusal `BakeStack` makes and for the same reason one level along: a smart
        // material is a stack fragment with no model, so its mesh-map masks have nothing to measure —
        // and there is no material of its name to bind a splat map onto either, so the write would
        // refuse anyway with a sentence about a missing `.vxmat` rather than about the shelf entry
        // that should not have been weighed.
        if (subject.IsSmartMaterial) {
            shell.Notifications.Show(
                "Nothing baked",
                NotificationSeverity.Warning,
                $"'{Path.GetFileName(subject.AssetPath)}' is a smart material, which is a stack "
                + "fragment with no model and no material of its own. Apply it to a .vxlayers and "
                + "weigh that."
            );

            return;
        }

        if (baker is null) {
            shell.Notifications.Show(
                "Nothing baked",
                NotificationSeverity.Warning,
                TexturePreview.Describe(TexturePreview.Blocking(graphics))
            );

            return;
        }

        var outcomes = baker.BakeSplat(subject, Path.GetFileNameWithoutExtension(subject.AssetPath), force: force);
        var written = outcomes.Count(one => one.Set is not null);
        var painted = outcomes.Any(one => one.Painted);

        Refused(painted ? () => BakeSplat(subject, force: true) : null);

        var detail = string.Join(" · ", outcomes.Select(one => one.Status));

        shell.Notifications.Show(
            written == 0
                ? "Nothing baked"
                : $"Weighed {written.ToString(CultureInfo.InvariantCulture)} of "
                + $"{outcomes.Length.ToString(CultureInfo.InvariantCulture)}",
            written == 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
            painted ? detail + " " + ForceAdvice : detail
        );

        RefreshStack();
    }

    /// <summary>Re-runs the last bake a painted-over map refused.</summary>
    /// <remarks>
    ///     ⚠ <b>It is armed by a refusal and disarmed by running, which is what makes one verb safe
    ///     enough to sit on the Tools menu</b> — <a href="https://github.com/Rikarin/Vixen/issues/1019">#1019</a>.
    ///     A plain "bake forced" verb is a control that overwrites an artist's paint whenever
    ///     somebody reaches for it out of order; this one does nothing at all until a bake has been
    ///     refused for the one reason force answers, and it repeats <em>that</em> bake — same
    ///     document, same name, same folder — rather than starting a new one from whatever happens to
    ///     be open now.
    /// </remarks>
    public void BakeForced() {
        if (forced is not { } repeat) {
            shell.Notifications.Show(
                "Nothing to force",
                NotificationSeverity.Warning,
                "Bake Material (Force) repeats a bake that was refused because one of its maps had "
                + "been painted over, and no bake has been refused for that reason. Run Bake Material "
                + "or Bake Material from Layers first."
            );

            return;
        }

        // ⚠ Cleared before the run and not after it. The repeat arms this again if it is refused
        // again — for a *second* painted file, say — and clearing afterwards would throw that away.
        forced = null;

        repeat();
    }

    /// <summary>Remembers, or forgets, the bake a force verb would repeat.</summary>
    /// <param name="repeat">How to re-run it forced, or null when nothing force answers was refused.</param>
    /// <remarks>
    ///     ⚠ <b>Called on every bake, including the ones that worked, and the null half is the load
    ///     bearing one.</b> An arming that were never cleared would leave the force verb pointing at
    ///     a refusal the artist has since resolved by hand — so pressing it would overwrite a file
    ///     that had stopped being the one they were told about.
    /// </remarks>
    void Refused(Action? repeat) => forced = repeat;

    /// <summary>Saves the open stack's chosen texture set onto the shelf as a <c>.vxsmartmat</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § M10's file, given the one thing a file format needs to stop being a
    ///         plan: somebody who makes one</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/575">#575</a>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Named after the stack's file with no dialog</b>, which is
    ///         <see cref="BakeMaterial" />'s decision and is taken here for its reason: a name control
    ///         is a panel this verb does not have, and the stack's own name is the one answer that
    ///         cannot silently adopt another stack's shelf entry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An existing shelf entry of that name is replaced, and the notification says so.</b>
    ///         The alternative — writing <c>Hull_2</c> beside it — makes the ordinary case, which is
    ///         re-saving after an edit, litter the shelf with every draft; and a shelf a third party
    ///         reads is exactly where a pile of near-identical names is expensive.
    ///     </para>
    /// </remarks>
    public void SaveSmartMaterial() {
        if (stack is null) {
            shell.Notifications.Show(
                "No layer stack is open",
                NotificationSeverity.Warning,
                "Save as Smart Material takes the layers out of the stack in the Layer Stack panel. "
                + "Select a .vxlayers and run Open Layer Stack first."
            );

            return;
        }

        // ⚠ The second half of #1070's verb list. Extracting a shelf entry from a shelf entry writes
        // it back onto the shelf under its own name — harmless, and confusing in the way that costs
        // an artist ten minutes: the file it replaced is the file they were editing, and the
        // notification would say it had been saved as something new. What they want is the ordinary
        // save, which the document already does.
        if (stack.IsSmartMaterial) {
            shell.Notifications.Show(
                "Nothing saved",
                NotificationSeverity.Warning,
                $"'{Path.GetFileName(stack.AssetPath)}' is already a smart material. Save the "
                + "document to write your edits back onto the shelf; Save as Smart Material is for "
                + "taking the layers out of a .vxlayers."
            );

            return;
        }

        var name = Path.GetFileNameWithoutExtension(stack.AssetPath);
        var extract = SmartMaterial.Extract(stack.Document, name, stack.PaintSet);

        if (extract.Material is not { } material) {
            shell.Notifications.Show("Nothing saved", NotificationSeverity.Warning, extract.Status);

            return;
        }

        if (SmartMaterial.FolderOf(project.Paths.Assets) is not { } shelf) {
            shell.Notifications.Show(
                "Nothing saved",
                NotificationSeverity.Warning,
                "This project publishes no assets folder, so there is no shelf to save onto."
            );

            return;
        }

        var file = Path.Combine(shelf, SmartMaterial.Safe(name) + SmartMaterial.Extension);
        var replaced = File.Exists(file);

        try {
            Directory.CreateDirectory(shelf);
            File.WriteAllText(file, LayerStackYaml.Write(material));
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            // ⚠ A sentence rather than an exception, which is this module's rule for every route that
            // runs from a command handler: a throw out of one takes the editor's frame with it.
            shell.Notifications.Show("Nothing saved", NotificationSeverity.Warning, failure.Message);

            return;
        }

        // The shelf is a folder under `Assets/`, so what lands in it is an asset — and a scan is what
        // puts it in the Project panel the artist will select it from to apply it.
        project.Assets.Scan();

        shell.Notifications.Show(
            "Saved '" + name + "'",
            NotificationSeverity.Info,
            extract.Status
            + $" It is on the shelf at Assets/{SmartMaterial.ShelfFolder}/, ready to apply to another "
            + "stack."
            + (replaced ? " ⚠ It replaced the entry of that name that was already there." : "")
        );
    }

    /// <summary>Puts the selected <c>.vxsmartmat</c> on top of the open stack's chosen texture set.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The open stack may itself be a shelf entry, and that is allowed rather than
    ///         overlooked</b> — <a href="https://github.com/Rikarin/Vixen/issues/1070">#1070</a>. Its
    ///         two neighbours refuse a <c>.vxsmartmat</c> in the panel and this one does not: a shelf
    ///         entry composed of two others — rust over panel wear — is a real thing to author, and
    ///         <see cref="SmartMaterial.Prepare" /> already drops everything a fragment must not
    ///         carry, so the result is a fragment either way. What it needs is no code: the
    ///         difference between the three verbs is only which of them should have been offered.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Through the document's command stack, as one undo entry.</b> A verb that mutated
    ///         <c>TextureSetAsset.Layers</c> directly would put ten layers into a stack the artist
    ///         could not take back out except by hand — and it would leave <c>CommandStack.Depth</c>
    ///         unmoved, which is the edge <c>LayerStackView</c> follows to notice that the document
    ///         changed (<a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>), so the rows
    ///         would not even redraw.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refusal names what the shelf holds.</b> "No smart material is selected"
    ///         leaves an artist unable to tell an empty shelf from a wrong selection, and those two
    ///         have different next steps.
    ///     </para>
    /// </remarks>
    public void ApplySmartMaterial() {
        if (stack is null) {
            shell.Notifications.Show(
                "No layer stack is open",
                NotificationSeverity.Warning,
                "Apply Smart Material puts a .vxsmartmat on top of the stack in the Layer Stack "
                + "panel. Select a .vxlayers and run Open Layer Stack first."
            );

            return;
        }

        var asset = project.Selection.Primary;

        // ⚠ `Primary` on an empty selection is `AssetId.Empty` rather than null — it is a struct — so
        // the emptiness is asked about by name, which is `OpenGraph`'s finding one verb over.
        if (asset.IsEmpty
            || !project.Assets.TryGetByGuid(asset, out var entry)
            || !entry.Path.EndsWith(SmartMaterial.Extension, StringComparison.OrdinalIgnoreCase)) {
            var shelf = SmartMaterial.Shelf(project.Paths.Assets);

            shell.Notifications.Show(
                "Select a .vxsmartmat first",
                NotificationSeverity.Warning,
                "Apply Smart Material applies whatever is selected in the Project panel. "
                + (shelf.Count > 0
                    ? $"This project's shelf holds {string.Join(", ", shelf)}."
                    : $"This project's shelf (Assets/{SmartMaterial.ShelfFolder}/) is empty — run Save "
                    + "as Smart Material on a stack to put one there.")
            );

            return;
        }

        LayerStackAsset material;

        try {
            material = LayerStackYaml.Read(File.ReadAllText(project.Paths.Absolute(entry.Path)));
        } catch (Exception failure) when (failure is YamlBindingException or YamlParseException
            or NotSupportedException or IOException) {
            shell.Notifications.Show("Nothing applied", NotificationSeverity.Warning, failure.Message);

            return;
        }

        // ⚠ The set the panel is showing and not `Sets[0]` — #927. `SetFor` falls back to the first
        // set for a name nothing matches, which is what an empty `PaintSet` means, and answers null
        // only for a stack with no set at all.
        if (LayerStackEdit.SetFor(stack.Document, stack.PaintSet) is not { } target) {
            shell.Notifications.Show(
                "Nothing applied",
                NotificationSeverity.Warning,
                "This stack has no texture set, so there is nothing to apply a smart material to."
            );

            return;
        }

        var applied = SmartMaterial.Prepare(target, material);

        if (applied.Layers.IsDefaultOrEmpty) {
            shell.Notifications.Show("Nothing applied", NotificationSeverity.Warning, applied.Status);

            return;
        }

        stack.Stack.Execute(
            new ApplySmartMaterialCommand(stack, target.Name, applied.Layers, "Apply Smart Material")
        );

        shell.Notifications.Show("Applied '" + material.Name + "'", NotificationSeverity.Info, applied.Status);

        RefreshStack();
    }

    /// <summary>Swaps the pointer between selecting and painting.</summary>
    /// <remarks>
    ///     ⚠ <b>It opens the pane the brush works in, and until <see cref="PaintUvView" /> existed
    ///     there was none.</b> This verb used to say, in the notification, that no viewport drove the
    ///     brush and that a drag would paint nothing. The 2D UV view is doc 48 § D13's second front
    ///     end and is that viewport; the 3D projection path is still owed
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/574">#574</a>), and a mode with no pane
    ///     open would still be a mode nothing can be done in — so switching it on shows the pane.
    /// </remarks>
    public void TogglePaint() {
        var mode = tool.Toggle();

        // ⚠ Pulled rather than pushed. The segmented control and this verb are two writers of one
        // model; without this the control keeps showing what was last clicked, which is the state
        // the panel is in exactly when somebody used the shortcut instead.
        stackView?.Brush?.Refresh();

        if (mode == PaintToolMode.Paint) {
            // Opened rather than toggled, for `OpenGraph`'s reason: the verb means "let me paint", and a
            // toggle would close the pane for anybody who ran it while it was already open.
            shell.Workspace.Open(PaintPanel);
            RefreshPaint();
        }

        shell.Notifications.Show(
            mode == PaintToolMode.Paint ? "Painting" : "Not painting",
            NotificationSeverity.Info,
            // ⚠ It names the layer the brush is really aimed at rather than "the first paint layer",
            // which is what it said and is no longer true — a row's Select button writes
            // `PaintTool.LayerId` (#910). It also names the *set*, because there is still no way to
            // choose one and every path here takes `Sets[0]` — #927 asks for exactly this sentence
            // where a selector is not built.
            mode == PaintToolMode.Paint
                ? "The brush is " + tool.Describe() + ". Drag in the Paint pane to lay a stroke into "
                + Aimed()
                + ". ⚠ The 3D projection path is still doc 48 § D13 (#574), so a drag in the scene "
                + "paints nothing."
                : "A drag selects rows and pans the preview."
        );
    }

    /// <summary>Which set and which layer a drag would reach, as a person reads it.</summary>
    /// <remarks>
    ///     ⚠ <b>The set is named, and it is now the set that was actually chosen.</b>
    ///     <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>: every path here used to
    ///     take <c>Sets[0]</c> while the messages read as though one had been picked, so a multi-set
    ///     stack painted into the first one and said nothing. The selector is built and
    ///     <c>LayerStackDocument.PaintSet</c> is the choice, so this sentence names <em>that</em>
    ///     set — ⚠ naming the first one after the selector landed would have been worse than the
    ///     original silence, because it would be a confident wrong answer.
    /// </remarks>
    string Aimed() {
        var chosen = stack is null ? null : LayerStackEdit.SetFor(stack.Document, stack.PaintSet);
        var set = chosen is not null ? $"set '{chosen.Name}'" : "this stack";

        return tool.LayerId.Length > 0
            ? $"the layer '{tool.LayerId}' of {set}"
            : $"the first paint layer of {set} — no layer is selected, so the brush takes the first one";
    }

    /// <summary>Opens the selected <c>.vxtexgraph</c> on the canvas.</summary>
    /// <remarks>
    ///     ⚠ <b>Reuses the document the project already has for that asset.</b> Two documents over one
    ///     file are two undo histories and two dirty flags, and the second save silently discards the
    ///     first — which is <c>IDocumentHost.TryOpen</c>'s rule, restated because this module
    ///     cannot use that registry.
    /// </remarks>
    public void OpenGraph() {
        // ⚠ `Primary` on an empty selection is `AssetId.Empty` rather than null — it is a struct — so
        // the emptiness is asked about by name. A pattern match here compiles to "always true" and
        // would send `Empty` to the database, which answers no and produces the right message for the
        // wrong reason.
        var asset = project.Selection.Primary;

        if (asset.IsEmpty
            || !project.Assets.TryGetByGuid(asset, out var entry)
            || !entry.Path.EndsWith(TextureGraphDocument.Extension, StringComparison.OrdinalIgnoreCase)) {
            shell.Notifications.Show(
                "Select a .vxtexgraph first",
                NotificationSeverity.Warning,
                "Open Texture Graph opens whatever is selected in the Project panel."
            );

            return;
        }

        if (project.TryGetDocument(asset, out var existing) && existing is TextureGraphDocument opened) {
            document = opened;
        } else {
            document = new TextureGraphDocument(project, asset, project.Paths.Absolute(entry.Path));
        }

        project.Activate(document);

        // ⚠ Opened rather than toggled. The command means "show me this graph"; a toggle would close
        // the panel for anybody who ran it while it was already open, which is every second use.
        shell.Workspace.Open(GraphPanel);
        Refresh();
    }

    /// <summary>Opens the selected <c>.vxlayers</c> in the layers panel.</summary>
    /// <remarks>
    ///     <b><see cref="OpenGraph" />'s six decisions, unchanged and for its reasons:</b> the project's
    ///     own document is reused so that one file is not two undo histories, <c>Primary</c>'s
    ///     emptiness is asked about by name because <c>AssetId</c> is a struct and a pattern match
    ///     compiles to "always true", and the panel is opened rather than toggled.
    /// </remarks>
    public void OpenStack() {
        var asset = project.Selection.Primary;

        // ⚠ Either extension — #1070. A `.vxsmartmat` is a `.vxlayers` byte for byte and this is the
        // verb half of the claim `LayerStackEditorFactory` makes for the double-click; a verb that
        // took only one of the two would leave the shelf openable by mouse and not by command.
        if (asset.IsEmpty
            || !project.Assets.TryGetByGuid(asset, out var entry)
            || !(entry.Path.EndsWith(LayerStackDocument.Extension, StringComparison.OrdinalIgnoreCase)
                || entry.Path.EndsWith(SmartMaterial.Extension, StringComparison.OrdinalIgnoreCase))) {
            shell.Notifications.Show(
                "Select a .vxlayers or a .vxsmartmat first",
                NotificationSeverity.Warning,
                "Open Layer Stack opens whatever is selected in the Project panel. A shelf entry "
                + "opens in the same rows: it is a stack with no model and nothing painted."
            );

            return;
        }

        var previous = stack;

        if (project.TryGetDocument(asset, out var existing) && existing is LayerStackDocument opened) {
            stack = opened;
        } else {
            stack = new LayerStackDocument(project, asset, project.Paths.Absolute(entry.Path));
        }

        // ⚠ A different stack's canvases are not this stack's, and the store is keyed by absolute
        // path rather than by document — so nothing would evict them except the budget, which is a
        // ceiling and not a schedule. The paint pane is refreshed below and re-pins whatever it
        // opens, so this costs one read of one canvas and gives back the whole of the last stack's.
        if (!ReferenceEquals(previous, stack)) {
            canvases.Clear();
        }

        project.Activate(stack);

        shell.Workspace.Open(StackPanel);
        RefreshStack();

        // ⚠ The paint pane too, and it is the same document. Opening a second stack while the pane
        // was showing the first one's canvas would leave the brush aiming at a layer of a file
        // nobody has open — and the pixels under the pointer would be the old stack's.
        RefreshPaint();
    }
}
