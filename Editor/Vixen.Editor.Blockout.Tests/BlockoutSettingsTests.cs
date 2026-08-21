// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Editor.Inspector;
using Vixen.Geometry.Remeshing;
using Vixen.Geometry.Uv;
using Xunit;

namespace Vixen.Editor.Blockout.Tests;

/// <summary>Doc 36 § P1's last blockout row: the dials the verbs read are dials somebody can turn.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The finding this answers is that nothing in the editor drew them at all.</b> The
///         remesher's and the unwrapper's settings are <c>Core/</c> records, the mode held one of each,
///         and there was no panel and no descriptor — so <c>Retopologize</c> was a verb whose every
///         parameter was a compile-time constant a designer could not reach.
///     </para>
///     <para>
///         Asserted through <see cref="ReflectedDescriptor" /> rather than by naming the annotated
///         type, because what is being tested is that <i>whatever the mode holds</i> is drawable. A
///         test that named the new class would pass just as well if the mode still held the record.
///     </para>
/// </remarks>
public class BlockoutSettingsTests {
    [Fact]
    public void What_the_retopologize_verb_asks_for_is_something_the_inspector_can_draw() {
        var mode = new BlockoutMode();

        Assert.True(
            ReflectedDescriptor.TryGet(mode.Retopology.GetType(), out var descriptor),
            $"Nothing describes {mode.Retopology.GetType().Name}, so no panel can draw a row for it."
        );

        Assert.NotEmpty(descriptor.Members);
    }

    [Fact]
    public void And_so_are_the_two_the_uv_verbs_ask_for() {
        var mode = new BlockoutMode();

        Assert.True(ReflectedDescriptor.TryGet(mode.Charting.GetType(), out var charting));
        Assert.NotEmpty(charting.Members);

        Assert.True(ReflectedDescriptor.TryGet(mode.Packing.GetType(), out var packing));
        Assert.NotEmpty(packing.Members);
    }

    /// <summary>The panel opens with the mode, which is what stops it outliving the tool.</summary>
    [Fact]
    public void The_mode_names_the_panel_its_settings_are_drawn_in() =>
        Assert.Equal(BlockoutMode.PanelId, new BlockoutMode().Panel);

    /// <summary>
    ///     ⚠ <b>A mirror is only worth having while it mirrors.</b> A dial added to the record and not
    ///     to the class beside it is a dial nobody can turn, which is the whole defect this closes —
    ///     so every member of the record a panel could draw has to be named here. The same check the
    ///     import pipeline runs over <c>ModelImportSettings</c>.
    /// </summary>
    [Theory]
    [InlineData(typeof(RemeshSettings), typeof(BlockoutRetopologySettings))]
    [InlineData(typeof(UvSettings), typeof(BlockoutChartSettings))]
    [InlineData(typeof(PackSettings), typeof(BlockoutPackSettings))]
    public void Every_dial_on_the_record_is_a_row_on_the_class_beside_it(Type record, Type edited) {
        foreach (var member in record.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (!Drawable(member.PropertyType)) {
                continue;
            }

            var mirrored = edited.GetProperty(member.Name, BindingFlags.Public | BindingFlags.Instance);

            Assert.True(
                mirrored is not null,
                $"{record.Name}.{member.Name} is a number or a flag and {edited.Name} has no row for it."
            );

            Assert.Equal(member.PropertyType, mirrored.PropertyType);
            Assert.NotEmpty(mirrored.GetCustomAttributes<InspectorAttribute>());
        }

        // A drawer exists for a number, a flag and a choice. Everything else on these records is a
        // list a stroke produced, a plane a gizmo owns or an interface a plug-in supplies, and
        // `BlockoutSettings.cs` says so member by member.
        static bool Drawable(Type type) => type.IsEnum || type == typeof(bool) || type == typeof(int)
            || type == typeof(float);
    }

    /// <summary>What the panel edits is what the verb runs with, which is the point of the mapper.</summary>
    [Fact]
    public void Turning_a_dial_changes_what_the_verb_is_handed() {
        var mode = new BlockoutMode();

        mode.Retopology.TargetQuads = 777;
        mode.Retopology.Adaptivity = 0.25f;
        mode.Charting.MaxDepth = 3;
        mode.Packing.Resolution = 2048;

        Assert.Equal(777, mode.Retopology.ToRemeshSettings().TargetQuads);
        Assert.Equal(0.25f, mode.Retopology.ToRemeshSettings().Adaptivity);
        Assert.Equal(3, mode.Charting.ToUvSettings().MaxDepth);
        Assert.Equal(2048, mode.Packing.ToPackSettings().Resolution);
    }
}
