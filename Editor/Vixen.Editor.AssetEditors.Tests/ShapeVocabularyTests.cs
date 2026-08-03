// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The first file in doc 34's workflow, and the one that had no panel at all.</summary>
public class ShapeVocabularyTests {
    [Fact]
    public void EveryEditIsOneUndoEntry() {
        using var project = new EditorFixture();
        var document = Open(project);

        var belly = document.AddTerm("belly", "The front of the torso.");

        Assert.Single(document.Vocabulary.Shapes);

        var renamed = document.Edit(belly, term => term with { Name = "stomach" });

        Assert.Equal("stomach", Assert.Single(document.Vocabulary.Shapes).Name);
        Assert.NotSame(belly, renamed);

        document.Stack.Undo();
        Assert.Equal("belly", Assert.Single(document.Vocabulary.Shapes).Name);

        document.Stack.Undo();
        Assert.Empty(document.Vocabulary.Shapes);
    }

    /// <summary>A class holds members, and a member is only ever edited through its class.</summary>
    [Fact]
    public void AClassMemberIsAddedEditedAndRemovedThroughItsClass() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.AddTerm("belly", "The front of the torso.");

        var humanoid = document.AddClass("humanoid");
        var member = document.AddMember(humanoid);

        // ⚠ Defaults to a name this vocabulary declares. A member naming nothing is the one mistake
        // the file exists to catch, so the button that makes one does not make it on somebody's behalf.
        Assert.Equal("belly", member.Member.Name);
        Assert.Single(Assert.Single(document.Vocabulary.Classes).Members);

        var resized = document.Edit(member, entry => entry with { Extents = new(0.3f) });

        Assert.Equal(0.3f, Assert.Single(Assert.Single(document.Vocabulary.Classes).Members).Extents.X, 3);

        Assert.True(document.Remove(resized));
        Assert.Empty(Assert.Single(document.Vocabulary.Classes).Members);
    }

    /// <summary>
    ///     ⚠ <b>The one mistake the file exists to prevent</b>: a class demanding a shape and the
    ///     vocabulary forbidding it, in one file. Every set that honoured the class would fail the
    ///     name check.
    /// </summary>
    [Fact]
    public void AClassRequiringAnUndeclaredNameIsAProblemAndItIsFatal() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.AddTerm("belly", "The front of the torso.");

        var humanoid = document.AddClass("humanoid");

        Assert.Empty(document.Problems());

        document.AddMember(humanoid, "left-palm");

        var problem = Assert.Single(document.Problems());

        Assert.True(problem.Fatal);
        Assert.Contains("does not declare", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>A name declared twice is worth saying and is not fatal.</summary>
    [Fact]
    public void ANameDeclaredTwiceIsAWarning() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.AddTerm("belly", "The front of the torso.");
        document.AddTerm("belly", "Something else entirely.");

        var problem = Assert.Single(document.Problems());

        Assert.False(problem.Fatal);
        Assert.Contains("more than once", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ <b>The panel and the build read the same rules.</b> Two copies would be one copy that
    ///     goes out of step, and the way that shows up is a file the panel calls clean and the build
    ///     refuses.
    /// </summary>
    [Fact]
    public void ThePanelsProblemsAreTheContentsOwnAnswer() {
        var content = new ShapeVocabularyContent {
            Name = "humanoid",
            Shapes = [new("belly", "")],
            Classes = [new("humanoid", [new("left-palm", ShapeKind.Box, [], new(0.1f), Vector3.Zero, true)])]
        };

        var problem = Assert.Single(content.Problems());

        Assert.True(problem.Fatal);
        Assert.Equal("left-palm", problem.Name);
    }

    // ── The panel ────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>Names and classes are in one list, because the mistake is only visible when both are
    ///     in front of each other.</b> Three tabs would hide exactly the relationship being authored.
    /// </summary>
    [Fact]
    public void TheListShowsNamesTagsAndClassesTogetherWithMembersUnderTheirClass() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        document.AddTerm("belly", "The front of the torso.");
        document.AddTag("affords=grip-surface", "Something a hand may hold.");

        var humanoid = document.AddClass("humanoid");
        document.AddMember(humanoid);

        var view = harness.Ui.Document.Root.Add<ShapeVocabularyView>();

        view.Show(document);
        harness.Ui.Frame();

        // Three headings, one name, one tag, one class and its member.
        Assert.Equal(7, view.List.Children.Count);
        Assert.True(view.List.Children[6].HasClass("member"));
    }

    /// <summary>A member naming something undeclared is marked as it is drawn, not only when checked.</summary>
    [Fact]
    public void AMemberNamingAnUndeclaredShapeIsMarkedInTheList() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        document.AddTerm("belly", "The front of the torso.");

        var humanoid = document.AddClass("humanoid");

        document.AddMember(humanoid, "left-palm");

        var view = harness.Ui.Document.Root.Add<ShapeVocabularyView>();

        view.Show(document);
        harness.Ui.Frame();

        var member = view.List.Children[^1];

        Assert.True(member.HasClass("missing"));
        Assert.Equal("not declared above", member.Children[1].Text);

        // ⚠ And the report is always shown rather than behind a button: the mistake is one somebody
        // makes while typing a class, and a check they have to remember to press is one they press
        // after they have finished.
        Assert.NotEmpty(view.Report.Children);
        Assert.Contains(view.Report.Children, child => child.HasClass("fatal"));
    }

    /// <summary>The buttons add the right kind of thing, and Add Required Shape follows the selection.</summary>
    [Fact]
    public void TheBarAddsEachKindAndAMemberFollowsTheSelectedClass() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        var view = harness.Ui.Document.Root.Add<ShapeVocabularyView>();

        view.Show(document);
        harness.Ui.Frame();

        Click(harness, view.AddTerm);
        Assert.Single(document.Vocabulary.Shapes);

        Click(harness, view.AddTag);
        Assert.Single(document.Vocabulary.Tags);

        // ⚠ Nothing selected is not a class, so the button does nothing rather than guessing which
        // class was meant — there is no "the first one" that is ever the right answer.
        Click(harness, view.AddMember);
        Assert.Empty(document.Vocabulary.Classes);

        Click(harness, view.AddPlan);
        Assert.Single(document.Vocabulary.Classes);
        Assert.NotNull(view.Owner());

        Click(harness, view.AddMember);
        Assert.Single(Assert.Single(document.Vocabulary.Classes).Members);

        // The selection followed the new member, so Remove takes that and not the class.
        Click(harness, view.Drop);
        Assert.Empty(Assert.Single(document.Vocabulary.Classes).Members);
        Assert.Single(document.Vocabulary.Classes);
    }

    /// <summary>
    ///     ⚠ <b>A member is compared by its record, not by the wrapper round it.</b> Every rebuild
    ///     makes a fresh one, so reference equality on the wrapper would lose the selection on every
    ///     keystroke.
    /// </summary>
    [Fact]
    public void SelectingAMemberSurvivesARebuild() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        document.AddTerm("belly", "The front of the torso.");

        var humanoid = document.AddClass("humanoid");
        document.AddMember(humanoid);

        var view = harness.Ui.Document.Root.Add<ShapeVocabularyView>();

        view.Show(document);
        harness.Ui.Frame();

        Click(harness, view.List.Children[^1]);

        var chosen = Assert.IsType<VocabularyMember>(view.Selected);

        view.Reload();
        harness.Ui.Frame();

        Assert.True(view.List.Children[^1].HasClass("selected"));
        Assert.Same(chosen.Member, Assert.IsType<VocabularyMember>(view.Selected).Member);
    }

    [Fact]
    public void ItSavesAndReopens() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.AddTerm("belly", "The front of the torso.");
        document.AddTag("affords=lean-on", "Something a body may rest against.");
        document.AddMember(document.AddClass("humanoid"));
        document.Save();

        var reopened = new ShapeVocabularyDocument(project.Project, AssetId.New(), document.AssetPath);

        Assert.Null(reopened.LoadError);
        Assert.Equal("belly", Assert.Single(reopened.Vocabulary.Shapes).Name);
        Assert.Equal("affords=lean-on", Assert.Single(reopened.Vocabulary.Tags).Tag);
        Assert.Equal("belly", Assert.Single(Assert.Single(reopened.Vocabulary.Classes).Members).Name);
    }

    static void Click(ViewHarness harness, UiElement element) {
        var x = element.AbsoluteLeft + (element.Width / 2f);
        var y = element.AbsoluteTop + (element.Height / 2f);

        harness.Ui.Document.Dispatch(new PointerEvent { X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary });
        harness.Ui.Document.Dispatch(new PointerEvent { X = x, Y = y, Action = PointerAction.Released, Button = PointerButton.Primary });

        harness.Ui.Frame();
    }

    static ShapeVocabularyDocument Open(EditorFixture project) {
        var path = project.WriteAsset("Assets/humanoid.vxshapevocab", string.Empty);

        return new(project.Project, AssetId.New(), path);
    }
}
