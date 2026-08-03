// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.HotReload;
using Xunit;

namespace Vixen.Editor.Terrain.Tests;

/// <summary>The shipped markup inspector, built and bound.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P4's exit criterion, and F7's answer.</b> <c>TerrainBrushInspector.vxml</c> has
///         no <c>@code</c> block and no C# in it at all — it is nine <c>&lt;PropertyField&gt;</c>s in
///         three <c>&lt;Expander&gt;</c>s — and this asserts that what it produces is the rows the
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

    public BrushInspectorMarkupTests() => InspectorTheme.Install(document);

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
            ["Radius", "Falloff", "Curve", "Shape", "Strength", "Spacing", "Rotation", "Angle", "PatternScale"],
            order
        );

        var groups = Descendants(document.Root).OfType<Expander>().Select(group => group.Label).ToList();

        Assert.Equal(["Shape", "Stroke", "Pattern"], groups);
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

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
