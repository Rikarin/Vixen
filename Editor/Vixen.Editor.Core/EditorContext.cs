// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.Core;

/// <summary>What a command is handed: the project it is changing, and which document it is in.</summary>
/// <remarks>
///     <para>
///         One context per command stack, not one per execution. A per-document stack has a context
///         whose <see cref="Document" /> is that document; the project's global stack has one whose
///         <see cref="Document" /> is <see langword="null" />, which is the whole of what makes a
///         command "global".
///     </para>
///     <para>
///         <b><see cref="Touch" /> is how a cross-document operation declares its blast radius.</b>
///         Renaming an asset rewrites references inside documents that are open and that have undo
///         stacks of their own; those stacks now hold entries that would undo into a world that has
///         moved. The command says which documents it reached, the stack marks them, and the
///         interaction is one documented rule instead of a per-feature guess.
///     </para>
/// </remarks>
public sealed class EditorContext {
    readonly List<EditorDocument> touched = [];
    bool recording;

    /// <summary>The project everything here belongs to.</summary>
    public EditorProject Project { get; }

    /// <summary>The document this stack edits, or <see langword="null" /> for the global stack.</summary>
    public EditorDocument? Document { get; }

    /// <summary>Every asset in the project, by GUID and by path.</summary>
    public AssetDatabase Assets => Project.Assets;

    /// <summary>Who refers to what.</summary>
    public ReferenceIndex References => Project.References;

    /// <summary>What is selected in the project browser.</summary>
    public Selection<AssetId> Selection => Project.Selection;

    internal EditorContext(EditorProject project, EditorDocument? document) {
        Project = project;
        Document = document;
    }

    /// <summary>Declares that the running command changed something inside <paramref name="document" />.</summary>
    /// <param name="document">The document that was changed.</param>
    /// <remarks>
    ///     Call it from <see cref="IEditorCommand.Do" /> and from
    ///     <see cref="IEditorCommand.Undo" /> both — a command that reaches into a document on the
    ///     way forward reaches into it on the way back too. Outside a command it does nothing, so a
    ///     helper shared between command and non-command paths does not need to know which it is in.
    /// </remarks>
    public void Touch(EditorDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (!recording || touched.Contains(document)) {
            return;
        }

        touched.Add(document);
    }

    internal void BeginRecording() {
        touched.Clear();
        recording = true;
    }

    /// <summary>
    ///     The documents touched since <see cref="BeginRecording" />. The list is reused, so the
    ///     caller reads it before anything else runs a command.
    /// </summary>
    internal List<EditorDocument> EndRecording() {
        recording = false;
        return touched;
    }
}
