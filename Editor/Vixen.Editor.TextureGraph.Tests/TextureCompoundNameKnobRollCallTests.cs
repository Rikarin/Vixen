// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.TextureGraph.Nodes;
using Xunit;

namespace Tests;

/// <summary>
///     Every name a shipped compound offers is a name the setting it forwards into accepts.
/// </summary>
/// <remarks>
///     <para>
///         <see href="https://github.com/Rikarin/Vixen/issues/1095">#1095</see>, which is
///         <see href="https://github.com/Rikarin/Vixen/issues/964">#964</see>'s finding in a data
///         file. A published graph exposing an inner node's <c>[Setting]</c> writes the names it
///         accepts into its own <c>parameters:</c> block, and the setting it forwards into declares
///         the same names from an <c>enum</c> — in a different file, one of them content, with
///         nothing anywhere comparing the two. Adding a fourth accumulation mode leaves
///         <c>Patterns/Tile Random</c> offering three, silently.
///     </para>
///     <para>
///         ⚠ <b>This is the interim the issue asks for and not the fix it asks for.</b> The fix is a
///         knob that states no list and inherits the setting's, and it is blocked on a file-format
///         decision: <c>TextureGraphParameters.Declared</c> uses "a text parameter <em>with</em> a
///         list" to tell a name knob from a scalar knob saved before the kind existed, and eleven
///         shipped compounds declare <c>default: '0.5'</c> with no <c>kind:</c> key. Inheriting means
///         finding another way to draw that line — most likely an explicit <c>kind: Name</c>, which
///         is a change to what a file means rather than a tidy-up. Until then, two lists that agree
///         are at least two lists something notices when they stop.
///     </para>
///     <para>
///         ⚠ <b>Ask what this prints on the day nothing is forwarded.</b> A walk that found no name
///         knobs would report every compound compliant, which is exactly what "no drift" looks like
///         here — so the sweep counts what it checked and refuses a total that is too small to be
///         the shipped set. And each knob must reach at least one inner setting: a
///         <c>$Knob</c> that names no node's setting is a knob whose picker offers names nothing
///         reads, which is the same defect with the transcription on one side only.
///     </para>
/// </remarks>
public class TextureCompoundNameKnobRollCallTests {
    /// <summary>The name knobs the shipped library declares, and how many are owed.</summary>
    /// <remarks>
    ///     Four ship today — <c>Patterns/Tile Random</c>'s <c>Accumulation</c>,
    ///     <c>Patterns/Cells</c>' <c>Metric</c>, <c>Surface/Metal Reflectance</c>'s <c>Metal</c> and
    ///     <c>Utility/Safe Transform</c>'s <c>Tiling</c>. ⚠ <b>The enumeration said three and the
    ///     floor was three, and the one it left out was the knob whose drift this roll call
    ///     found</b> — a floor set below the shipped count is a roll call one knob can stop
    ///     forwarding without turning red, which is the instrument failing at exactly the thing it
    ///     was built for.
    ///     ⚠ A floor rather than an exact count, for the reason five roll calls in this workstream
    ///     have already had to learn: an exact set over a surface every slice grows is red on the
    ///     merge and green on every branch.
    /// </remarks>
    const int Least = 4;

    [Fact]
    public void Every_shipped_name_knob_offers_exactly_what_its_inner_setting_accepts() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        Assert.NotEmpty(TextureCompoundLibrary.Shipped);

        var forwarded = 0;

        foreach (var path in TextureCompoundLibrary.Shipped) {
            var text = TextureCompoundLibrary.Source(path);

            Assert.NotNull(text);

            var graph = NodeGraphDocument.Load(YamlSerializer.Parse<NodeGraphAsset>(text), out _);

            foreach (var knob in TextureGraphParameters
                .Declared(graph.Parameters)
                .Where(parameter => parameter.Kind == TextureGraphParameterKind.Name)) {
                var reached = 0;

                foreach (var node in graph.Nodes) {
                    foreach (var (setting, value) in node.Texts) {
                        if (!string.Equals(value.Trim(), "$" + knob.Name, StringComparison.Ordinal)) {
                            continue;
                        }

                        var type = Assert.Single(registry.Types, one => one.Path == node.Type);
                        var inner = Assert.Single(type.Settings, one => one.Name == setting);

                        Assert.Equal(inner.Accepted, knob.Accepted);

                        reached++;
                        forwarded++;
                    }
                }

                Assert.True(
                    reached > 0,
                    $"'{path}' declares a name knob '{knob.Name}' that no node's setting spells as "
                    + $"'${knob.Name}', so its picker offers names nothing in the graph reads."
                );
            }
        }

        Assert.True(
            forwarded >= Least,
            $"only {forwarded} name knob(s) in the shipped library reach an inner setting, and "
            + $"{Least} do. A sweep that found none would call the library clean by finding nothing."
        );
    }
}
