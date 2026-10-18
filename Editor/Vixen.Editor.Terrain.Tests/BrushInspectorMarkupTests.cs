// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Terrain;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.HotReload;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>The shipped markup inspector, built and bound.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P4's exit criterion, and F7's answer.</b> <c>TerrainBrushInspector.vxml</c> has
///         no <c>@code</c> block and no C# in it at all — it is five <c>&lt;PropertyField&gt;</c>s in
///         two <c>&lt;Expander&gt;</c>s — and this asserts that what it produces is the rows the
///         generated inspector would have drawn, in the order the markup put them in.
///     </para>
///     <para>
///         ⚠ <b>Against the real file rather than a fixture.</b> F7 is the warning that a declarative
///         path nobody adopts is a declarative path that does not work; a test over a <c>.vxml</c>
///         written for the test would be exactly that mistake with a green tick on it.
///     </para>
/// </remarks>
public class BrushInspectorMarkupTests : IDisposable {
    readonly UiDocument document = new(400f, 800f);

    /// <remarks>
    ///     ⚠ <b>All three sheets, because one of the claims below is about a rule in the first
    ///     one.</b> Collapsing is <c>expander-content { display: none }</c> and lives in
    ///     <c>ControlTheme</c>; a fixture that loaded only the inspector's own sheet could assert
    ///     that a group's flag went false and never notice that its rows stayed on screen.
    /// </remarks>
    public BrushInspectorMarkupTests() {
        ControlTheme.Install(document);
        AdvancedTheme.Install(document);
        InspectorTheme.Install(document);
    }

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    (TerrainBrushSettings Brush, IReadOnlyList<PropertyField> Fields) Built() {
        var brush = new TerrainBrushSettings();
        var view = BuildContext.Build<TerrainBrushInspector>(document, document.Root);

        MarkupBinding.Bind(view.Root, new InspectorTarget([brush]));
        document.Update();

        return (brush, [.. Descendants(view.Root).OfType<PropertyField>()]);
    }

    [Fact]
    public void Every_row_in_the_markup_found_its_member() {
        var (_, fields) = Built();

        Assert.NotEmpty(fields);
        Assert.All(fields, field => Assert.NotNull(field.Row));
    }

    /// <summary>
    ///     ⚠ <b>The order is the markup's, which is the whole reason the file exists.</b> The
    ///     generated inspector draws members in declaration order because that is the only order it
    ///     has; a brush is a shape, a stroke and a pattern, and Spacing belongs with Rotation rather
    ///     than with Falloff.
    /// </summary>
    [Fact]
    public void The_rows_are_grouped_the_way_the_markup_says_rather_than_the_way_the_type_does() {
        var (_, fields) = Built();
        var order = fields.Select(field => field.Path).ToList();

        Assert.Equal(
            ["Radius", "Falloff", "Curve", "Strength", "Spacing"],
            order
        );

        var groups = Descendants(document.Root).OfType<Expander>().Select(group => group.Label).ToList();

        Assert.Equal(["Shape", "Stroke"], groups);
    }

    /// <summary>
    ///     ⚠ <b>Markup children of an <c>&lt;Expander&gt;</c> belong to its content part, not to the
    ///     control.</b> Hung off the control they are siblings of the part that
    ///     <c>display: none</c> hides, so a group could be shut and its rows would stay — the
    ///     chevron flipping over nine sliders that never moved. This is the structural half of the
    ///     claim the next test measures.
    /// </summary>
    [Fact]
    public void A_groups_rows_are_inside_the_part_that_collapsing_hides() {
        var (_, fields) = Built();
        var groups = Descendants(document.Root).OfType<Expander>().ToList();

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.NotEmpty(group.Content.Children));

        // Every row is under some group's content, and none of them is a child of a group itself.
        Assert.All(fields, field => Assert.Contains(groups, group => Descendants(group.Content).Contains(field)));
        Assert.All(groups, group => Assert.DoesNotContain(group.Children, child => child is PropertyField));
    }

    /// <summary>
    ///     <b>Doc 36 § P4's grouping argument, which is only worth anything if a group can shut.</b>
    ///     The headings exist so that a brush reads as a shape and a stroke rather
    ///     than as one column of sliders; a heading that cannot be closed is a caption.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The rows' own rectangles, not just the group's flag.</b> <c>IsExpanded</c> going
    ///     false and the class coming off are both true of the broken version — what was wrong was
    ///     that nothing followed from them, so the assertion has to be about what the user sees.
    /// </remarks>
    [Fact]
    public void A_collapsed_group_does_not_show_its_rows() {
        var (_, fields) = Built();

        var stroke = Descendants(document.Root).OfType<Expander>().Single(group => group.Label == "Stroke");
        var rows = fields.Where(field => Descendants(stroke.Content).Contains(field)).ToList();

        Assert.Equal(["Strength", "Spacing"], rows.Select(row => row.Path));
        Assert.All(rows, row => Assert.True(row.Height > 0f));

        stroke.IsExpanded = false;
        document.Update();

        Assert.False(stroke.HasClass("open"));
        Assert.Equal(0f, stroke.Content.Height);
        Assert.All(rows, row => Assert.Equal(0f, row.Height));

        // And back, because a section that shut once and never reopened would pass everything above.
        stroke.IsExpanded = true;
        document.Update();

        Assert.All(rows, row => Assert.True(row.Height > 0f));
    }

    /// <summary>
    ///     ⚠ <b>The header is what opens it, and a click on a row inside must not shut it.</b> The
    ///     markup path reaches <see cref="Expander" /> through <c>ContentHost</c> rather than
    ///     through <c>Content.Add</c>, so the click routing is worth asserting once from this side
    ///     too — a row that hung off the control would bubble to the same place and look identical.
    /// </summary>
    [Fact]
    public void The_header_toggles_a_group_that_the_markup_asked_to_be_open() {
        var (_, fields) = Built();

        var shape = Descendants(document.Root).OfType<Expander>().Single(group => group.Label == "Shape");
        var radius = fields.Single(field => field.Path == "Radius");

        Assert.True(shape.IsExpanded);
        Assert.True(radius.Height > 0f);

        shape.Header.Activate();
        document.Update();

        Assert.False(shape.IsExpanded);
        Assert.Equal(0f, radius.Height);

        shape.Header.Activate();
        document.Update();

        Assert.True(shape.IsExpanded);
        Assert.True(radius.Height > 0f);
    }

    [Fact]
    public void A_row_writes_the_setting_it_names() {
        var (brush, fields) = Built();
        var radius = fields.Single(field => field.Path == "Radius");

        Assert.True(radius.Row!.Field.Write(12f));
        Assert.Equal(12f, brush.Radius);
    }

    /// <summary>
    ///     ⚠ <b>The reset button and the tooltip come with the row and are not written in the
    ///     markup.</b> That is what makes <c>&lt;PropertyField&gt;</c> worth having over a slider and
    ///     a label: an author who reached for the controls directly would be reimplementing the
    ///     default row, badly, once per member.
    /// </summary>
    [Fact]
    public void A_row_arrives_with_what_the_generated_one_has() {
        var (_, fields) = Built();
        var radius = fields.Single(field => field.Path == "Radius").Row;

        Assert.NotNull(radius);
        Assert.True(radius.Field.CanReset);
        Assert.Equal("Radius", radius.Field.Member.DisplayName);

        // A `[Range]` member is a slider, which is the drawer resolving rather than the markup
        // choosing — nothing in the .vxml says what a radius looks like.
        Assert.Contains(Descendants(radius).OfType<Slider>(), _ => true);
    }

    /// <summary>
    ///     ⚠ <b>A reload throws the elements away and keeps the component.</b> That is
    ///     <c>HotReloadHost</c>'s stated bargain — two <c>Build</c> bodies are two programs with no
    ///     shared identity — so every element a row was joined to is gone afterwards, and a panel
    ///     that did not bind again would come back from an edit showing rows that edit nothing.
    /// </summary>
    [Fact]
    public void A_reload_leaves_the_rows_bound_to_what_they_were_editing() {
        var host = new HotReloadHost(document);
        var brush = new TerrainBrushSettings();

        MarkupInspector.Of<TerrainBrushInspector>(host)(document.Root, new InspectorTarget([brush]));
        document.Update();

        var report = host.ReloadComponents();

        Assert.Empty(report.Errors);
        document.Update();

        var radius = Descendants(document.Root)
            .OfType<PropertyField>()
            .Single(field => field.Path == "Radius");

        Assert.NotNull(radius.Row);
        Assert.True(radius.Row.Field.Write(20f));
        Assert.Equal(20f, brush.Radius);
    }

    /// <summary>What each row is moved to, away from its default, to ask whether the ground notices.</summary>
    /// <remarks>
    ///     ⚠ A row missing from this table fails the test below rather than being skipped: a new row
    ///     has to say what it changes before it is allowed on the panel.
    /// </remarks>
    static readonly Dictionary<string, object> Moved = new() {
        ["Radius"] = 10f,
        ["Falloff"] = 0f,
        ["Curve"] = BrushFalloffKind.Tip,
        ["Shape"] = BrushShape.Alpha,
        ["Strength"] = 0.25f,
        ["Spacing"] = 1.5f,
        ["Rotation"] = BrushRotation.Random,
        ["Angle"] = 45f,
        ["PatternScale"] = 1f
    };

    /// <summary>
    ///     ⚠ <b>Every row the brush inspector offers changes the ground a stroke writes</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1098">#1098</a>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The panel offered <c>Shape</c> (Circle, Alpha, Pattern), <c>Rotation</c>, <c>Angle</c>
    ///         and <c>PatternScale</c>, and every one of them acts only through an
    ///         <see cref="IBrushMask" /> — which no terrain tool supplies, so
    ///         <see cref="TerrainBrush.WeightAt" /> takes its documented null-mask path and paints the
    ///         same disc. Four controls an artist could move and watch nothing happen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Measured through the real tool and the real markup</b>, not through
    ///         <c>WeightAt</c> with a null argument: a row is written the way a person writes it, a
    ///         stroke is dragged through <see cref="TerrainEdit" />, and every sample of the ground is
    ///         compared with the same drag at the defaults. When a mask source lands, a row that comes
    ///         back has to pass this — which it will, because then it will move the ground.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_row_the_brush_inspector_offers_changes_the_ground_a_stroke_writes() {
        var (_, fields) = Built();
        var baseline = Stroked(null);
        var inert = new List<string>();

        foreach (var path in fields.Select(field => field.Path ?? "")) {
            Assert.True(Moved.ContainsKey(path), $"'{path}' is a new row; say what it changes in {nameof(Moved)}.");

            if (Stroked(path).SequenceEqual(baseline)) {
                inert.Add(path);
            }
        }

        Assert.Empty(inert);
    }

    /// <summary>The ground after one drag, with one row of the inspector moved first, or none.</summary>
    static float[] Stroked(string? path) {
        using var panel = new UiDocument(400f, 800f);

        ControlTheme.Install(panel);
        AdvancedTheme.Install(panel);
        InspectorTheme.Install(panel);

        var (edit, terrain) = Ground.Editing();
        var view = BuildContext.Build<TerrainBrushInspector>(panel, panel.Root);

        edit.Tools.Metres = 2f;
        MarkupBinding.Bind(view.Root, new InspectorTarget([edit.Brush]));
        panel.Update();

        if (path is not null) {
            var row = Descendants(view.Root).OfType<PropertyField>().Single(field => field.Path == path).Row;

            Assert.NotNull(row);
            Assert.True(row.Field.Write(Moved[path]), $"the '{path}' row refused {Moved[path]}");
        }

        Assert.True(edit.Begin(new(30f, 30f)));
        edit.Extend(new(40f, 34f));
        edit.Commit(rebuildColliders: false);

        var samples = Ground.Shape.TileSamples * Ground.Shape.TilesX - 1;
        var heights = new float[samples * samples];

        for (var z = 0; z < samples; z++) {
            for (var x = 0; x < samples; x++) {
                heights[(z * samples) + x] = Ground.HeightAt(terrain, x, z);
            }
        }

        return heights;
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
