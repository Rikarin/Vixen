// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Engine.Scenes;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Scenes;

/// <summary>What a build makes of this scene: the blocks, the columns and the complaints.</summary>
/// <remarks>
///     <para>
///         <b>K1's last unopened door, and the question it answers is one nothing else could.</b> An
///         authored <c>.vxscene</c> nests its entities and spells its numbers out; a compiled
///         <c>SceneAsset</c> is flat, positional and archetype-major, and the two are shaped by
///         opposite forces. Everything an author can see today is the first of them. So "why does my
///         entity arrive in the player without its <c>Health</c>" has had no answer short of building
///         the project, shipping it, and noticing — which is the shape of defect this pane exists to
///         make visible at the moment somebody can still act on it.
///     </para>
///     <para>
///         ⚠ <b>It compiles the open document rather than reading the artefact the last import
///         wrote, and that is a choice with a cost.</b> What it shows is what this scene <i>would</i>
///         compile to, not what the build store currently holds — so it cannot show you a stale
///         artefact, and it cannot be wrong about an edit you have not saved. The alternative
///         answers a different and also useful question ("what is actually in the build") and needs
///         the artefact store, an import to have run, and a staleness story of its own. This is the
///         same trade the shader graph's *show generated code* makes, and for the same reason:
///         during authoring the actionable question is what the thing in front of you produces.
///     </para>
///     <para>
///         ⚠ <b>The diagnostics are the point, more than the tables are.</b>
///         <c>SceneCompiler.Compile</c> reports every problem and then fails once, so a hand-merged
///         scene with four duplicate ids says all four rather than making the author find them one
///         build at a time. A pane that showed only the happy result would throw that away.
///     </para>
///     <para>
///         <b>Compiled on demand and not on every keystroke.</b> A scene compile walks every entity
///         and serialises every component, which is the wrong thing to do behind a gizmo drag. The
///         button is the trigger, and the pane says when what it is showing was produced.
///     </para>
/// </remarks>
public sealed class CompiledSceneView : Control {
    SceneDocument? document;

    /// <inheritdoc />
    protected override string TagName => "compiled-scene";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Runs the compiler over the document as it stands.</summary>
    public Button Compile { get; private set; } = null!;

    /// <summary>Whether the entity names are kept, as a shipping build's would not be.</summary>
    /// <remarks>
    ///     ⚠ <b>Off changes the answer, which is why it is here rather than assumed.</b> Names are
    ///     one string per entity in the asset — a real fraction of a large level's compiled size —
    ///     and a build that strips them produces different bytes and a different total. An author
    ///     asking "how big is this scene" wants to ask it about the build they ship.
    /// </remarks>
    public CheckBox KeepNames { get; private set; } = null!;

    /// <summary>Shown before anything has been compiled, and when a compile failed.</summary>
    public Alert Status { get; private set; } = null!;

    /// <summary>Entity count, block count and bytes.</summary>
    public UiElement Facts { get; private set; } = null!;

    /// <summary>One row per block: its archetype, how many entities, and how many bytes.</summary>
    public UiElement Blocks { get; private set; } = null!;

    /// <summary>What the compiler said, in the order it said it.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <summary>The last compile's result, or <see langword="null" /> if it failed or has not run.</summary>
    public SceneContent? Content { get; private set; }

    /// <summary>What the last compile reported.</summary>
    public IReadOnlyList<(ImportSeverity Severity, string Message)> Reported => reported;

    readonly List<(ImportSeverity Severity, string Message)> reported = [];

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var bar = Part("compiled-scene-bar");

        Compile = bar.Add<Button>();
        Compile.Label = "Compile";
        Compile.Clicked += _ => Refresh();

        bar.Add("compiled-scene-label").Text = "Keep names";

        KeepNames = bar.Add<CheckBox>();
        KeepNames.IsChecked = true;
        KeepNames.CheckedChanged += (_, _) => {
            // Only when there is something to redo — flipping the switch before the first compile
            // should not run one, for the same reason `Show` does not.
            if (Content is not null || reported.Count > 0) {
                Refresh();
            }
        };

        Status = Part<Alert>();
        Status.Title = "Not compiled";
        Status.Message = "Press Compile to see what a build makes of this scene.";

        Facts = Part("compiled-scene-facts");
        Blocks = Part("compiled-scene-blocks");
        Diagnostics = Part("compiled-scene-diagnostics");
    }

    /// <summary>Points the pane at a document. Does not compile it.</summary>
    /// <param name="scene">The open scene or prefab.</param>
    /// <remarks>
    ///     ⚠ <b>Deliberately does not compile.</b> Opening a level would otherwise pay for a full
    ///     compile before the first frame is drawn, for a tab nobody may look at.
    /// </remarks>
    public void Show(SceneDocument scene) {
        ArgumentNullException.ThrowIfNull(scene);

        document = scene;

        Content = null;
        reported.Clear();

        Facts.Empty();
        Blocks.Empty();
        Diagnostics.Empty();

        Status.RemoveClass("hidden");
        Status.Title = "Not compiled";
        Status.Message = "Press Compile to see what a build makes of this scene.";
    }

    /// <summary>Compiles the document and redraws.</summary>
    /// <returns>Whether it compiled.</returns>
    /// <remarks>
    ///     ⚠ <b>A prefab is compiled as a prefab</b>, so the one-root rule is checked here as the
    ///     build would check it — <c>SceneCompiler.CompilePrefab</c> refuses two roots and says why.
    ///     Compiling every document as a scene would make this pane the one place a prefab with two
    ///     roots looked fine.
    /// </remarks>
    public bool Refresh() {
        if (document is not { } scene) {
            return false;
        }

        reported.Clear();
        Content = null;

        var file = SceneSerializer.ToFile(scene);
        var names = KeepNames.IsChecked;

        void Report(ImportSeverity severity, string message) => reported.Add((severity, message));

        Content = scene.Writer is PrefabFileWriter
            ? SceneCompiler.CompilePrefab(file, Report, names)?.Content
            : SceneCompiler.CompileScene(file, Report, names)?.Content;

        Draw();

        return Content is not null;
    }

    void Draw() {
        Facts.Empty();
        Blocks.Empty();
        Diagnostics.Empty();

        foreach (var (severity, message) in reported) {
            var row = Diagnostics.Add("compiled-scene-diagnostic");

            row.AddClass(severity switch {
                ImportSeverity.Error => "error",
                ImportSeverity.Warning => "warning",
                _ => "information"
            });

            row.Add("compiled-scene-severity").Text = severity.ToString();
            row.Add("compiled-scene-message").Text = message;
        }

        if (Content is not { } content) {
            Status.RemoveClass("hidden");
            Status.Title = "Would not compile";
            Status.Message = reported.Count == 0
                ? "The compiler refused it and said nothing, which is a bug in the compiler."
                : $"{reported.Count(entry => entry.Severity == ImportSeverity.Error)} error(s). A build would "
                + "fail on this scene for the same reasons.";

            return;
        }

        Status.AddClass("hidden");

        // ⚠ The bytes are the columns' and not the asset's. Positions, rotations, scales, names and
        // ids are tables of the content's own — a fixed cost per entity that no archetype decision
        // moves — so counting them into a block would make every block look alike.
        var bytes = content.Blocks.Sum(block => block.Columns.Sum(column => (long) column.Data.Length));

        Fact("Entities", content.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Fact("Blocks", content.Blocks.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Fact("Component bytes", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Fact("Names", content.Names.Length == 0 ? "stripped" : "kept");

        foreach (var block in content.Blocks) {
            var row = Blocks.Add("compiled-scene-block");

            // ⚠ An empty column set is a real archetype and not a missing one: an entity carrying
            // nothing but a transform is in a block whose columns are none, and showing it blank
            // reads as a rendering fault.
            row.Add("compiled-scene-archetype").Text = block.Columns.Length == 0
                ? "(transform only)"
                : string.Join(", ", block.Columns.Select(column => column.Component));

            row.Add("compiled-scene-count").Text =
                $"{block.Entities.Length} entit{(block.Entities.Length == 1 ? "y" : "ies")}";

            row.Add("compiled-scene-bytes").Text =
                $"{block.Columns.Sum(column => column.Data.Length)} B";
        }
    }

    /// <summary>A name and a number, in the shape every other editor's facts take.</summary>
    void Fact(string name, string value) {
        var row = Facts.Add("fact-row");

        row.Add("fact-name").Text = name;
        row.Add("fact-value").Text = value;
    }

}
