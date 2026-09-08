// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Vixen.Core;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's <c>Planned</c> mechanism, and the one way it goes wrong.</summary>
/// <remarks>
///     <para>
///         <b>A verb that is not implemented is greyed with a written reason, which is right — and
///         nothing re-reads the reason.</b> A blocker that closed months ago leaves a verb
///         indistinguishable from one that is genuinely blocked, with a tooltip actively telling the
///         reader not to try. Six of the twenty were in that state and a seventh, the clipboard's,
///         was not even on the list: <c>SceneClone</c>'s own remarks call themselves "the thing doc
///         20's clipboard was blocked on".
///     </para>
///     <para>
///         ⚠ <b>"Is this reason still true" is not a test that can be written, and that is the whole
///         difficulty.</b> What <i>can</i> be written is the rule that separated the good reasons
///         from the expired ones: a reason naming a <i>mechanism</i> is falsifiable by looking at the
///         tree, and a reason naming a <i>milestone</i> is not — it is a claim about a schedule, and
///         a schedule that has since closed leaves the sentence reading as true. So this suite
///         asserts the ids that were re-derived are verbs, and that no refusal anywhere in the
///         editor names a milestone.
///     </para>
/// </remarks>
public class ExpiredRefusalTests {
    /// <summary>The ids whose stated blocker had shipped, now implemented.</summary>
    public static TheoryData<string> Rederived => [
        "file.export-package",
        "assets.create",
        "entity.set-parent",
        "tools.reload-shaders",
        "edit.duplicate"
    ];

    [Theory]
    [MemberData(nameof(Rederived))]
    public void A_verb_whose_blocker_shipped_is_no_longer_greyed(string id) {
        using var fixture = EditorSession.Start();

        var command = fixture.Shell.Commands[id];

        Assert.NotNull(command);

        Assert.False(
            command.IsUnavailable,
            $"{id} is still registered as Planned, and the thing it said it was waiting for exists: "
            + command.Unavailable.Text
        );
    }

    /// <summary>Every refusal names a missing piece, so it can be checked against the tree.</summary>
    /// <remarks>
    ///     ⚠ <b>The count is asserted because the loop is the assertion.</b> A registry that came up
    ///     with no planned commands at all would satisfy a bare <c>foreach</c> silently, and this
    ///     suite would then pass on the day the mechanism it exists for stopped running.
    /// </remarks>
    [Fact]
    public void No_refusal_in_the_editor_names_a_milestone_instead_of_a_mechanism() {
        using var fixture = EditorSession.Start();

        var planned = fixture.Shell.Commands.Commands.Where(static command => command.IsUnavailable).ToList();

        Assert.NotEmpty(planned);

        foreach (var command in planned) {
            Assert.DoesNotContain(
                "milestone",
                command.Unavailable.Text,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    /// <summary>Duplicate is <c>SceneClone</c> with nothing in between, and now runs it.</summary>
    [Fact]
    public void Duplicate_copies_the_selection_and_leaves_the_copies_selected() {
        using var fixture = EditorSession.Start();

        var original = fixture.Scene.Add("Test Crate", LocalTransform.Identity);

        fixture.Scene.Selection.Set([original]);
        fixture.Settle();

        Assert.True(fixture.CanRun("edit.duplicate"));

        var before = fixture.Scene.Entities.Count();

        fixture.Run("edit.duplicate").Settle();

        Assert.Equal(before + 1, fixture.Scene.Entities.Count());

        // ⚠ The copy is the selection, not the original. Pressing the key twice has to give two
        // copies rather than a copy of the copy of the same thing.
        var selected = Assert.Single(fixture.Scene.Selection.ToList());
        Assert.NotEqual(original, selected);

        // And it is undoable in one step, which is what the transaction inside SceneClone is for.
        fixture.Run("edit.undo").Settle();
        Assert.Equal(before, fixture.Scene.Entities.Count());
    }

    /// <summary>Set Parent moves the selection, and draws its refusals rather than hiding them.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because a dialog that offered only the legal answers would pass the
    ///     first.</b> Dropping a parent into its own child is a thing people try on purpose; the row
    ///     has to be there and has to say no.
    /// </remarks>
    [Fact]
    public void Set_parent_offers_the_impossible_parents_greyed_and_moves_to_a_possible_one() {
        using var fixture = EditorSession.Start();

        var parent = fixture.Scene.Add("Test Shelf", LocalTransform.Identity);
        var child = fixture.Scene.Add("Test Crate", LocalTransform.Identity);
        var inside = fixture.Scene.Add("Test Nail", LocalTransform.Identity);

        fixture.Scene.Reparent(inside, child);
        fixture.Scene.Selection.Set([child]);
        fixture.Settle();

        Assert.True(fixture.CanRun("entity.set-parent"));

        fixture.Run("entity.set-parent").Settle();
        Assert.True(fixture.IsAsking);

        var rows = Buttons(fixture).ToList();

        // Its own descendant is listed and cannot be pressed; the entity itself likewise.
        Assert.True(Assert.Single(rows, button => button.Label == "Test Nail").Disabled);
        Assert.True(Assert.Single(rows, button => button.Label == "Test Crate").Disabled);
        Assert.False(Assert.Single(rows, button => button.Label == "Test Shelf").Disabled);

        fixture.Answer("Test Shelf");

        Assert.Equal(parent, Hierarchy.ParentOf(fixture.Scene.World, child));
    }

    /// <summary>New Asset… asks which kind, over the registry the Create submenu is built from.</summary>
    [Fact]
    public void New_asset_asks_which_kind_and_runs_that_kinds_own_verb() {
        using var fixture = EditorSession.Start();

        fixture.Run("assets.create").Settle();
        Assert.True(fixture.IsAsking);

        var labels = Buttons(fixture).Select(static button => button.Label).ToList();

        Assert.Contains("Shader Graph", labels);
        Assert.Contains("Animation Clip", labels);

        fixture.Answer("Sequence");
        fixture.Settle();

        Assert.Contains(
            fixture.Project.Assets.Entries,
            static entry => entry.Path.EndsWith(".vxseq", StringComparison.Ordinal)
        );
    }

    /// <summary>A package carries the closure of what was selected, sidecars included.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>.meta</c> is the assertion that matters.</b> A package unpacked without one is
    ///     scanned into a project that mints fresh GUIDs, so every reference inside the package points
    ///     at nothing — the same hole the closure was computed to avoid, arriving by the other door.
    /// </remarks>
    [Fact]
    public void A_package_carries_what_the_selection_points_at_and_the_sidecars() {
        using var fixture = EditorSession.Start();

        var folder = fixture.Project.Paths.Assets;
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, "Leaf.yaml"), "name: leaf" + Environment.NewLine);
        File.WriteAllText(Path.Combine(folder, "Loose.yaml"), "name: loose" + Environment.NewLine);
        File.WriteAllText(Path.Combine(folder, "Root.yaml"), "name: root" + Environment.NewLine);
        fixture.Project.Assets.Scan();

        var leaf = Entry(fixture, "Leaf.yaml");
        var root = Entry(fixture, "Root.yaml");
        var loose = Entry(fixture, "Loose.yaml");

        // Written after the scan so the sidecars exist and the GUID is the one on disk.
        File.WriteAllText(
            Path.Combine(folder, "Root.yaml"),
            "uses: " + new AssetReference(leaf) + Environment.NewLine
        );

        fixture.Project.References.Build(fixture.Project.Assets);

        var closure = fixture.Editor.Closure([root]);

        Assert.Contains(leaf, closure);
        Assert.DoesNotContain(loose, closure);

        var package = Path.Combine(fixture.DataDirectory, "exported" + EditorApplication.PackageExtension);

        fixture.Editor.WritePackage(package, closure);

        using var archive = ZipFile.OpenRead(package);
        var names = archive.Entries.Select(static entry => entry.FullName).ToList();

        Assert.Contains("Assets/Root.yaml", names);
        Assert.Contains("Assets/Root.yaml.meta", names);
        Assert.Contains("Assets/Leaf.yaml", names);
        Assert.Contains("Assets/Leaf.yaml.meta", names);
        Assert.DoesNotContain("Assets/Loose.yaml", names);
    }

    static AssetId Entry(EditorSession fixture, string name) =>
        fixture.Project.Assets.Entries.Single(entry => entry.Name == name).Guid;

    static IEnumerable<Button> Buttons(EditorSession fixture) =>
        fixture.Shell.Dialogs.Current is { } dialog ? Descendants(dialog).OfType<Button>() : [];

    static IEnumerable<Vixen.Ui.UiElement> Descendants(Vixen.Ui.UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
