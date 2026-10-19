// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Vixen.Editor.Tests;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Doc 50 § D4's pairing invariant over a running editor's own registries (#1294): every
///     registered <see cref="DocumentKind" /> has a <see cref="DocumentView" /> for each type it opens,
///     and every view a kind.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read from the services a module is handed, not from <c>StandardEditors</c>.</b> A
///         second registry built the same way would agree with itself on the day the editor stopped
///         registering a view — or the day a module registered a kind and forgot its view, which is
///         the case <c>StandardDocumentPairingTests</c> cannot see, because only a running editor
///         holds what modules registered.
///     </para>
///     <para>
///         ⚠ <b>The instrument first.</b> An editor that published no host would pair nothing with
///         nothing and report no problems, so the shipped kinds are counted before the invariant is
///         asked, and a module that registers half a pair is shown to be reported.
///     </para>
/// </remarks>
public class DocumentPairingTests {
    [Fact]
    public void Every_registered_kind_has_a_view_and_every_view_a_kind() {
        using var editor = EditorSession.Start();

        var host = editor.Plugins.Services.Require<IDocumentHost>();
        var workspace = editor.Plugins.Services.Require<IWorkspace>();

        Assert.True(host.Kinds.Count >= 26, $"Only {host.Kinds.Count} kinds are registered.");
        Assert.True(workspace.Views.Count >= host.Kinds.Count, $"Only {workspace.Views.Count} views are registered.");

        Assert.Equal([], DocumentPairing.Unpaired(host.Kinds, workspace.Views));
    }

    /// <summary>A module that registers a kind through the contract and no view is what this reports.</summary>
    [Fact]
    public void A_module_kind_with_no_view_is_reported() {
        using var editor = EditorSession.Start();

        var host = editor.Plugins.Services.Require<IDocumentHost>();
        var workspace = editor.Plugins.Services.Require<IWorkspace>();

        Assert.Equal(PluginState.Active, editor.Plugins.Activate("fixture.half", "Half", new HalfModule()).State);

        var problem = Assert.Single(DocumentPairing.Unpaired(host.Kinds, workspace.Views));

        Assert.Contains("Half", problem, StringComparison.Ordinal);
    }

    sealed class HalfModule : IEditorPlugin {
        public void Activate(PluginContext context) =>
            context.Owns(context.Services.Require<IDocumentHost>().Add(new HalfKind()));

        public void Deactivate() {
        }
    }

    sealed class HalfDocument(EditorProject project) : EditorDocument(project, default, "Half") {
        protected override void SaveCore() {
        }
    }

    sealed class HalfKind : DocumentKind {
        public override string Name => "Half";

        public override IReadOnlyList<string> Extensions { get; } = [".half"];

        public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(HalfDocument)];

        public override EditorDocument Open(DocumentRequest request) => new HalfDocument(request.Project);
    }
}
