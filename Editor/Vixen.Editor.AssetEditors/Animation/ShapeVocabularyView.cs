// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>A project's shape vocabulary: the names, the tags, and the body plans.</summary>
/// <remarks>
///     <para>
///         <b>The first file in doc 34's workflow, and the one that had no panel.</b> Everything after
///         it is checked against it, so it being editable only outside Vixen made the first step of
///         the whole module the one step that left the editor.
///     </para>
///     <para>
///         ⚠ <b>One list holding all three kinds rather than three panels.</b> The mistake this file
///         exists to prevent — a class requiring a shape the vocabulary does not declare — is only
///         visible when the terms and the classes are in front of each other. Three tabs would hide
///         exactly the relationship being authored.
///     </para>
///     <para>
///         ⚠ <b>The problems are the vocabulary's own answer, not this panel's.</b>
///         <see cref="ShapeVocabularyContent.Problems" /> is what the importer reports from too — two
///         copies of the rules would be one copy that goes out of step, and the way that shows up is
///         a file the panel calls clean and the build refuses.
///     </para>
/// </remarks>
public sealed class ShapeVocabularyView : Control {
    readonly List<(UiElement Row, object Entry)> rows = [];

    ShapeVocabularyDocument? document;
    object? selected;

    /// <inheritdoc />
    protected override string TagName => "vocab-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The bar across the top.</summary>
    public UiElement Bar { get; private set; } = null!;

    /// <summary>Declares a name a shape may have.</summary>
    public Button AddTerm { get; private set; } = null!;

    /// <summary>Declares a tag a shape may carry.</summary>
    public Button AddTag { get; private set; } = null!;

    /// <summary>Declares a body plan. Named <c>AddPlan</c> because <c>UiElement.AddClass</c> exists.</summary>
    public Button AddPlan { get; private set; } = null!;

    /// <summary>Adds a shape the selected class requires.</summary>
    public Button AddMember { get; private set; } = null!;

    /// <summary>Removes whatever is selected. Named <c>Drop</c> because <c>UiElement.Remove</c> exists.</summary>
    public Button Drop { get; private set; } = null!;

    /// <summary>Everything the file declares, in one list.</summary>
    public UiElement List { get; private set; } = null!;

    /// <summary>The selected row's fields.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>What is wrong with the file, as the build would say it.</summary>
    public UiElement Report { get; private set; } = null!;

    /// <summary>What is selected, or <see langword="null" />.</summary>
    public object? Selected => selected;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Bar = Part("vocab-bar");

        AddTerm = Bar.Add<Button>();
        AddTerm.Label = "Add Name";

        AddTag = Bar.Add<Button>();
        AddTag.Label = "Add Tag";

        AddPlan = Bar.Add<Button>();
        AddPlan.Label = "Add Class";

        AddMember = Bar.Add<Button>();
        AddMember.Label = "Add Required Shape";

        Drop = Bar.Add<Button>();
        Drop.Label = "Remove";

        var body = Part("vocab-body");

        List = body.Add("vocab-list");

        var side = body.Add("vocab-side");

        Fields = side.Add("vocab-fields");
        Report = side.Add("vocab-report");

        AddHandler<ClickEvent>(static (element, args) => ((ShapeVocabularyView) element).Chosen(args));
        AddHandler<PointerEvent>(static (element, args) => ((ShapeVocabularyView) element).Pressed(args));
    }

    /// <summary>Shows a vocabulary.</summary>
    /// <param name="vocabulary">The document.</param>
    public void Show(ShapeVocabularyDocument vocabulary) {
        ArgumentNullException.ThrowIfNull(vocabulary);

        if (document is { } previous) {
            previous.Changed -= Reload;
        }

        document = vocabulary;
        vocabulary.Changed += Reload;

        Reload(vocabulary);
    }

    /// <summary>Rebuilds from the document.</summary>
    /// <param name="vocabulary">The document.</param>
    public void Reload(ShapeVocabularyDocument vocabulary) {
        ArgumentNullException.ThrowIfNull(vocabulary);
        Reload();
    }

    /// <summary>Rebuilds the list.</summary>
    public void Reload() {
        List.Empty();
        rows.Clear();

        if (document is not { } vocabulary) {
            Restate();
            return;
        }

        // The names a class member may refer to, so a member that refers to something else can be
        // marked as it is drawn rather than only when the check is run.
        var declared = new HashSet<string>(
            vocabulary.Vocabulary.Shapes.Select(static term => term.Name),
            StringComparer.Ordinal
        );

        Heading("Names");

        foreach (var term in vocabulary.Vocabulary.Shapes) {
            Row(term, term.Name, term.Meaning, false, 0);
        }

        Heading("Tags");

        foreach (var tag in vocabulary.Vocabulary.Tags) {
            Row(tag, tag.Tag, tag.Meaning, false, 0);
        }

        Heading("Classes");

        foreach (var declaredClass in vocabulary.Vocabulary.Classes) {
            Row(declaredClass, declaredClass.Name, $"{declaredClass.Members.Length} required shape(s)", false, 0);

            foreach (var member in declaredClass.Members) {
                var missing = declared.Count > 0 && !declared.Contains(member.Name);

                Row(
                    new VocabularyMember(declaredClass, member),
                    member.Name,
                    missing ? "not declared above" : $"{member.Kind}{(member.Required ? string.Empty : ", optional")}",
                    missing,
                    1
                );
            }
        }

        Restate();
    }

    void Heading(string text) {
        var row = List.Add("vocab-row");

        row.AddClass("header");
        row.Add("vocab-name").Text = text;
    }

    void Row(object entry, string name, string detail, bool missing, int depth) {
        var row = List.Add("vocab-row");

        row.SetClass("member", depth > 0);
        row.SetClass("missing", missing);

        row.Add("vocab-name").Text = name;
        row.Add("vocab-detail").Text = detail;

        rows.Add((row, entry));
    }

    void Restate() {
        Fields.Empty();
        Report.Empty();

        foreach (var (element, entry) in rows) {
            element.SetClass("selected", Same(entry, selected));
        }

        if (document is not { } vocabulary) {
            return;
        }

        Fields.Add("vocab-title").Text = vocabulary.Vocabulary.Name;

        if (vocabulary.LoadError is { } error) {
            Fields.Add("vocab-error").Text = error;
        }

        Fields.Add("fact-row").Add("fact-name").Text =
            $"{vocabulary.Vocabulary.Shapes.Length} name(s), {vocabulary.Vocabulary.Tags.Length} tag(s), "
            + $"{vocabulary.Vocabulary.Classes.Length} class(es)";

        switch (selected) {
            case ShapeTermRecord term:
                Term(vocabulary, term);
                break;

            case ShapeTagRecord tag:
                Afford(vocabulary, tag);
                break;

            case ShapeClassRecord declared:
                Class(vocabulary, declared);
                break;

            case VocabularyMember member:
                Member(vocabulary, member);
                break;

            default:
                Fields.Add("text").Text = "Select a name, a tag or a class.";
                break;
        }

        // ⚠ Always shown, not behind a button. The one mistake this file exists to prevent is one an
        // author makes while typing a class, and a check they have to remember to press is one they
        // press after they have finished.
        var problems = vocabulary.Problems();

        if (problems.Count == 0) {
            return;
        }

        Report.Add("vocab-title").Text = "Problems";

        foreach (var problem in problems) {
            var line = Report.Add("vocab-note");

            line.SetClass("fatal", problem.Fatal);
            line.Text = problem.Message;
        }
    }

    void Term(ShapeVocabularyDocument vocabulary, ShapeTermRecord term) {
        Fields.Add("vocab-title").Text = "Name";

        Line("Name", term.Name, value => selected = vocabulary.Edit(term, entry => entry with { Name = value }));

        // ⚠ The meaning is the useful half and the panel says so by giving it the same standing as
        // the name. A vocabulary of thirty bare words is a list nobody can use to settle an argument.
        Line("Meaning", term.Meaning, value => selected = vocabulary.Edit(term, entry => entry with { Meaning = value }));
    }

    void Afford(ShapeVocabularyDocument vocabulary, ShapeTagRecord tag) {
        Fields.Add("vocab-title").Text = "Tag";

        Line("Tag", tag.Tag, value => selected = vocabulary.Edit(tag, entry => entry with { Tag = value }));
        Line("Meaning", tag.Meaning, value => selected = vocabulary.Edit(tag, entry => entry with { Meaning = value }));
    }

    void Class(ShapeVocabularyDocument vocabulary, ShapeClassRecord declared) {
        Fields.Add("vocab-title").Text = "Class";

        Line("Name", declared.Name, value => selected = vocabulary.Edit(declared, entry => entry with { Name = value }));

        Fields.Add("text").Text =
            "A body that claims this class has to carry every shape below it. That is what makes one authored "
            + "contact portable: a clip may assume the names exist.";
    }

    void Member(ShapeVocabularyDocument vocabulary, VocabularyMember member) {
        Fields.Add("vocab-title").Text = "Required shape";

        Line(
            "Name",
            member.Member.Name,
            value => selected = vocabulary.Edit(member, entry => entry with { Name = value })
        );

        var kind = Fields.Add("fact-row");
        kind.Add("fact-name").Text = "Kind";

        var select = kind.Add("fact-value").Add<Select>();

        foreach (var option in Enum.GetValues<ShapeKind>()) {
            select.AddOption(option.ToString());
        }

        select.Value = member.Member.Kind.ToString();

        select.SelectionChanged += (_, value) => {
            if (Enum.TryParse<ShapeKind>(value, out var picked)) {
                selected = vocabulary.Edit(member, entry => entry with { Kind = picked });
            }
        };

        Vector(
            "Extents",
            member.Member.Extents,
            value => selected = vocabulary.Edit(member, entry => entry with { Extents = value })
        );

        var required = Fields.Add("fact-row");
        required.Add("fact-name").Text = "Required";

        var toggle = required.Add("fact-value").Add<ToggleButton>();

        toggle.Label = member.Member.Required ? "a set without it is invalid" : "optional";
        toggle.IsChecked = member.Member.Required;

        toggle.CheckedChanged += (_, on) => selected = vocabulary.Edit(member, entry => entry with { Required = on });
    }

    void Line(string label, string value, Action<string> write) {
        var row = Fields.Add("fact-row");

        row.Add("fact-name").Text = label;

        var box = row.Add("fact-value").Add<TextBox>();

        box.Value = value;
        box.ValueChanged += (_, text) => write(text ?? string.Empty);
    }

    void Vector(string label, Vector3 value, Action<Vector3> write) {
        var row = Fields.Add("fact-row");

        row.Add("fact-name").Text = label;

        var holder = row.Add("fact-value");
        var current = value;

        Component(holder, current.X, number => write(new(number, current.Y, current.Z)));
        Component(holder, current.Y, number => write(new(current.X, number, current.Z)));
        Component(holder, current.Z, number => write(new(current.X, current.Y, number)));
    }

    static void Component(UiElement holder, float value, Action<float> write) {
        var box = holder.Add<NumericInput>();

        box.Decimals = 3;
        box.Number = value;
        box.NumberChanged += (_, number) => write((float) number);
    }

    /// <summary>
    ///     ⚠ <b>A member is compared by its class and its record, not by reference to the wrapper.</b>
    ///     Every rebuild makes a fresh <see cref="VocabularyMember" /> around the same two objects, so
    ///     reference equality on the wrapper would lose the selection on every keystroke.
    /// </summary>
    static bool Same(object? left, object? right) =>
        left is VocabularyMember first && right is VocabularyMember second
            ? ReferenceEquals(first.Member, second.Member)
            : ReferenceEquals(left, right);

    void Chosen(ClickEvent args) {
        if (document is not { } vocabulary) {
            return;
        }

        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, AddTerm)) {
                selected = vocabulary.AddTerm();
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddTag)) {
                selected = vocabulary.AddTag();
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddPlan)) {
                selected = vocabulary.AddClass();
                args.Handled = true;

                return;
            }

            if (ReferenceEquals(element, AddMember)) {
                // A member belongs to a class, so the button follows the selection into one rather
                // than asking which — and does nothing when the selection is not in a class at all.
                if (Owner() is { } owner) {
                    selected = vocabulary.AddMember(owner);
                }

                args.Handled = true;
                return;
            }

            if (ReferenceEquals(element, Drop)) {
                Delete(vocabulary);
                args.Handled = true;

                return;
            }
        }
    }

    /// <summary>Which class the Add Required Shape button would add to, or <see langword="null" />.</summary>
    public ShapeClassRecord? Owner() =>
        selected switch {
            ShapeClassRecord declared => declared,
            VocabularyMember member => member.Class,
            _ => null
        };

    void Delete(ShapeVocabularyDocument vocabulary) {
        var removed = selected switch {
            ShapeTermRecord term => vocabulary.Remove(term),
            ShapeTagRecord tag => vocabulary.Remove(tag),
            ShapeClassRecord declared => vocabulary.Remove(declared),
            VocabularyMember member => vocabulary.Remove(member),
            _ => false
        };

        if (removed) {
            selected = null;
        }
    }

    void Pressed(PointerEvent args) {
        if (args.Action != PointerAction.Pressed || args.Button != PointerButton.Primary) {
            return;
        }

        foreach (var (element, entry) in rows) {
            if (element.Bounds.Contains(new Vector2(args.X, args.Y))) {
                selected = entry;
                Restate();

                args.Handled = true;
                return;
            }
        }
    }
}

/// <summary>Opens a project's shape vocabulary.</summary>
public sealed class ShapeVocabularyEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Shape Vocabulary";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [ShapeVocabularyDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new ShapeVocabularyDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<ShapeVocabularyView>();
        view.Show((ShapeVocabularyDocument) document);

        return view;
    }
}
