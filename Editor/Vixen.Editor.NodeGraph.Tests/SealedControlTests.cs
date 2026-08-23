// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Inspector;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.NodeGraph.Tests;
using Vixen.Ui.Controls;
using Xunit;

namespace Tests;

/// <summary>
///     That a <c>.vxml</c> can name the two <c>sealed</c> controls a wave of panel ports stopped on.
/// </summary>
/// <remarks>
///     <para>
///         The panel ledger's sixth shape says <c>NodeInspector</c> and <c>AddComponentMenu</c>
///         "cannot be written" because the sanctioned escape for a control fed by a method, or wanted
///         under another tag, is a four-line subclass — and <see cref="InspectorView" /> and
///         <see cref="ScrollView" /> are both <c>sealed</c>. That was true of the language and is not
///         true of it any more; <c>SealedControlHost.vxml</c> is the counter-example and this reads
///         it.
///     </para>
///     <para>
///         ⚠ <b>The real controls rather than a stand-in.</b> A fixture control that could be derived
///         from would let this pass by writing the subclass the actual panels cannot write, which is
///         precisely the claim under test. The markup compiler's own suite has a sealed fixture for
///         the language question; this one exists for the two named types.
///     </para>
///     <para>
///         ⚠ <b>It does not claim either panel is ported.</b> Neither is. What it claims is that the
///         door they stopped at is open, which is a smaller thing and the one wave 7 actually did.
///     </para>
/// </remarks>
public sealed class SealedControlTests : IDisposable {
    readonly ViewFixture fixture = new();
    readonly NodeTypeRegistry registry = new();
    readonly NodeGraphModel graph = new();

    public SealedControlTests() {
        NodeTypes.Register(registry);
        fixture.Show(graph, registry);
    }

    public void Dispose() {
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     <c>Part&lt;ScrollView&gt;("add-component-list")</c>, said in markup. The control is still
    ///     the control — it built its own interior — and the tag is the one a stylesheet can reach.
    /// </summary>
    [Fact]
    public void A_sealed_control_can_be_created_under_the_tag_a_stylesheet_names() {
        var host = fixture.Ui.Root.Add<SealedControlHost>();

        fixture.Update();

        Assert.Equal("add-component-list", host.List.Tag);

        // ⚠ And it is a ScrollView rather than a plain element wearing the name: a scroller builds
        // its viewport in `OnCreated`, which is what a wrong creation path would have skipped.
        Assert.NotSame(host.List, host.List.Content);
    }

    /// <summary>
    ///     <c>InspectorView.Inspect(descriptor, provider, targets)</c> is a method with no property
    ///     behind it, so no parameter and no <c>bind:</c> could reach it and there was no base class
    ///     to add one to. This is the call <c>NodeInspector.Rebuild</c> makes.
    /// </summary>
    [Fact]
    public void A_sealed_control_fed_by_a_method_is_fed_from_markup() {
        var host = fixture.Ui.Root.Add<SealedControlHost>();
        var node = graph.Add("Test/Named", new(60f, 60f));

        host.Shown.Value = Show(node);
        fixture.Update();

        Assert.NotEmpty(host.Inspector.Rows);
        Assert.Equal(typeof(GraphNode), host.Inspector.Descriptor!.Type);
    }

    /// <summary>
    ///     ⚠ <b>And it is re-fed, which is the half a wrapper property would not have given.</b> The
    ///     escape the ledger recorded is a property assigned by an effect; what <c>use</c> registers
    ///     <i>is</i> the effect, so pointing the panel somewhere else is a signal write rather than a
    ///     method somebody has to remember to call — which is what every hand-written
    ///     <c>Restate</c> in the editor is.
    /// </summary>
    [Fact]
    public void And_it_is_fed_again_when_what_the_expression_read_changes() {
        var host = fixture.Ui.Root.Add<SealedControlHost>();
        var named = graph.Add("Test/Named", new(60f, 60f));
        var vector = graph.Add("Test/Vector", new(300f, 60f));

        host.Shown.Value = Show(named);
        fixture.Update();

        var first = Names(host);
        Assert.NotEmpty(first);

        host.Shown.Value = Show(vector);
        fixture.Update();

        Assert.NotEqual(first, Names(host));
    }

    /// <summary>And the empty state, which is the other value the same expression produces.</summary>
    [Fact]
    public void Pointing_it_at_nothing_empties_it() {
        var host = fixture.Ui.Root.Add<SealedControlHost>();
        var node = graph.Add("Test/Named", new(60f, 60f));

        host.Shown.Value = Show(node);
        fixture.Update();
        Assert.NotEmpty(host.Inspector.Rows);

        host.Shown.Value = SealedControlHost.Inspection.Nothing;
        fixture.Update();

        Assert.Empty(host.Inspector.Rows);
    }

    static IReadOnlyList<string> Names(SealedControlHost host) =>
        [.. host.Inspector.Rows.Select(row => row.Field.Member.Name)];

    /// <summary>What <c>NodeInspector.Rebuild</c> assembles, assembled the same way.</summary>
    SealedControlHost.Inspection Show(GraphNode node) {
        var definition = registry.Get(node.Type);
        var provider = NodePortEditProvider.For(graph, definition, node.Id);

        return new(provider.Descriptor, provider, [node]);
    }
}
