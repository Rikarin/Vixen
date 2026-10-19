// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Ui.Documents;

namespace Vixen.Editor.Tests;

/// <summary>Doc 50 § D4's pairing invariant, as a function of the two registries (#1294).</summary>
/// <remarks>
///     <para>
///         <b>What <c>IAssetEditorFactory</c> held as a type</b>: "a document with no view is a model
///         nobody can reach and a view with no document is a control with nothing behind it". The
///         kind is <c>Vixen.Editor.Core</c>'s and the view <c>Vixen.Editor.Ui</c>'s, so no type can
///         hold it any more and this is where it lives — compiled into
///         <c>Vixen.Editor.AssetEditors.Tests</c> and, by a <c>&lt;Compile Include&gt;</c>, into
///         <c>Vixen.Editor.App.Tests</c>, which asks it about a running editor's registries.
///     </para>
///     <para>
///         ⚠ <b>Both directions, and the reverse is not a formality.</b> A view whose type no kind
///         opens is a registration that can never be reached — the shape a renamed document type
///         leaves behind — and it compiles exactly as well as a kind with no view.
///     </para>
/// </remarks>
static class DocumentPairing {
    /// <summary>One sentence per kind type with no view and per view with no kind.</summary>
    /// <param name="kinds">The registered kinds.</param>
    /// <param name="views">The registered views.</param>
    /// <returns>Empty when every pairing holds.</returns>
    public static List<string> Unpaired(IEnumerable<DocumentKind> kinds, IEnumerable<DocumentView> views) {
        var kindList = kinds.ToList();
        var viewList = views.ToList();
        var problems = new List<string>();

        foreach (var kind in kindList) {
            foreach (var type in kind.DocumentTypes) {
                if (!viewList.Any(view => view.DocumentType.IsAssignableFrom(type))) {
                    problems.Add($"'{kind.Name}' opens a {type.Name} and no DocumentView shows one.");
                }
            }
        }

        foreach (var view in viewList) {
            if (!kindList.Any(kind => kind.DocumentTypes.Any(type => view.DocumentType.IsAssignableFrom(type)))) {
                problems.Add($"{view.GetType().Name} shows a {view.DocumentType.Name} and no DocumentKind opens one.");
            }
        }

        return problems;
    }
}
