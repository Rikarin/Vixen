// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;
using Vixen.Editor.Core;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Code;

/// <summary>One run of typing, as an undo entry.</summary>
/// <remarks>
///     <para>
///         <b>The whole text, before and after.</b> A structural edit — an insert with its position
///         and length — is what a text editor's own undo stack holds, and it is the right shape when
///         the stack belongs to the editor. This stack belongs to the <i>document</i>, alongside the
///         command that renamed the asset and the one that changed its import settings, so an entry
///         has to be replayable against a buffer that something else may have touched. A snapshot is;
///         an offset is not.
///     </para>
///     <para>
///         The cost is the file's size per undo entry, which for source files is kilobytes. That is
///         the trade this makes deliberately, and it is why <see cref="TryMergeWith" /> is where the
///         entry count is actually controlled.
///     </para>
/// </remarks>
public sealed class TextEditCommand : IEditorCommand {
    readonly CodeDocument document;
    readonly string before;

    /// <summary>The text this entry leaves behind.</summary>
    public string After { get; private set; }

    /// <inheritdoc />
    public string Name => "Edit";

    /// <summary>Describes one run of typing.</summary>
    /// <param name="document">The document.</param>
    /// <param name="before">What the text was.</param>
    /// <param name="after">What it became.</param>
    public TextEditCommand(CodeDocument document, string before, string after) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        this.document = document;
        this.before = before;
        After = after;
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Replace(After);
        context.Touch(document);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        ArgumentNullException.ThrowIfNull(context);

        document.Replace(before);
        context.Touch(document);
    }

    /// <summary>Whether this edit crossed a line boundary.</summary>
    /// <remarks>
    ///     ⚠ <b>An edit that did merges with nothing, in either direction.</b> Refusing only to merge
    ///     <i>into</i> it would leave the newline's own entry free to absorb whatever was typed on the
    ///     new line — so pressing Enter and typing would undo back past the Enter, which is the
    ///     opposite of what "a newline ends the run" means.
    /// </remarks>
    bool SpansLines => Lines(before) != Lines(After);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Merging is what makes typing a word one undo entry, and it is bounded by newlines.</b>
    ///     A time window would make how many undo steps a paragraph produced depend on how fast
    ///     somebody types, which is the argument <c>CommandStack</c> already makes about drags — and a
    ///     per-keystroke history is one nobody can use.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        if (previous is not TextEditCommand earlier
            || !ReferenceEquals(earlier.document, document)
            || earlier.SpansLines
            || SpansLines) {
            return false;
        }

        merged = new TextEditCommand(document, earlier.before, After);
        return true;
    }

    static int Lines(string text) {
        var count = 1;

        foreach (var character in text) {
            if (character == '\n') {
                count++;
            }
        }

        return count;
    }
}

/// <summary>A text asset, open for editing: its buffer, its highlighting, and what is wrong with it.</summary>
/// <remarks>
///     <para>
///         <b>The text lives on the document and the caret lives on the control.</b> A file can be
///         open in two panes and has one set of bytes; where a caret is, is a property of a pane.
///         <see cref="CodeEditor" /> takes a <see cref="CodeBuffer" /> and this is what owns one.
///     </para>
///     <para>
///         <b>Analysis is the document's and it is not a build.</b> <see cref="Reanalyse" /> runs
///         the front end far enough to know where the squiggles go, and deliberately no further:
///         nothing here emits SPIR-V or writes an artefact. "Live recompile" in doc 11's sense is
///         this plus the shader compiler service, which runs out of process and is not what a
///         keystroke should wait for.
///     </para>
///     <para>
///         ⚠ <b>Analysis is run when it is asked for, not on every change.</b> The buffer raises
///         <c>Changed</c> per keystroke and parsing a shader per keystroke is a parse per keystroke;
///         the view debounces by running it on a pause the host decides. A document that analysed
///         itself on change would make that impossible to opt out of.
///     </para>
/// </remarks>
public class CodeDocument : EditorDocument {
    string recorded;
    bool applying;

    /// <summary>Where the file is, absolute.</summary>
    public string AssetPath { get; }

    /// <summary>The text.</summary>
    public CodeBuffer Buffer { get; }

    /// <summary>What is wrong with it, as of the last <see cref="Reanalyse" />.</summary>
    public IReadOnlyList<CodeDiagnostic> Diagnostics { get; private set; } = [];

    /// <summary>What turns a line into colours.</summary>
    public virtual ICodeTokenizer Tokenizer => PlainTokenizer.Instance;

    /// <summary>Raised after <see cref="Reanalyse" /> has run.</summary>
    public event Action<CodeDocument>? Analysed;

    /// <summary>Opens a text asset.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public CodeDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, Path.GetFileName(path)) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        AssetPath = path;
        recorded = AssetFile.Read(path);
        Buffer = new(recorded);

        Buffer.Changed += Edited;
    }

    /// <summary>The text as it stands.</summary>
    public string Text => Buffer.Text;

    /// <summary>Runs the analysis and replaces <see cref="Diagnostics" />.</summary>
    /// <returns>What it found.</returns>
    public IReadOnlyList<CodeDiagnostic> Reanalyse() {
        Diagnostics = Analyse(Buffer.Text);
        Analysed?.Invoke(this);

        return Diagnostics;
    }

    /// <summary>Whether anything it found is an error.</summary>
    public bool HasErrors {
        get {
            foreach (var diagnostic in Diagnostics) {
                if (diagnostic.Severity == CodeSeverity.Error) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>What is wrong with a text, for whatever language this document is.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The diagnostics, in whatever order the front end produced them.</returns>
    /// <remarks>Nothing, for a document whose language nothing analyses. Plain text is such a case.</remarks>
    protected virtual IReadOnlyList<CodeDiagnostic> Analyse(string text) => [];

    /// <inheritdoc />
    protected override void SaveCore() {
        AssetFile.Write(AssetPath, Buffer.Text);
        recorded = Buffer.Text;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A text document is its file, so re-reading it is one assignment. Everything else it has —
    ///     the highlighting, the diagnostics — is derived from the buffer and follows it.
    /// </remarks>
    public override bool CanReload => true;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Through <see cref="Replace" />, which is what an undo goes through</b>, so the
    ///     buffer's own change event does not record the file's contents as something somebody typed.
    ///     A reload that pushed an undo entry would put the version it just replaced back one Ctrl+Z
    ///     later — and the caller has already decided that version is gone.
    /// </remarks>
    protected override bool ReloadCore() {
        Replace(AssetFile.Read(AssetPath));
        return true;
    }

    /// <summary>Puts a text into the buffer without recording an edit for it.</summary>
    /// <param name="text">The text.</param>
    /// <remarks>
    ///     What an undo and a redo call. ⚠ The guard is what stops the buffer's own change event
    ///     recording the undo as a fresh edit, which would make undo push a new entry and the history
    ///     grow every time somebody pressed Ctrl+Z.
    /// </remarks>
    internal void Replace(string text) {
        recorded = text;

        // ⚠ Skipped when the buffer already holds it, which is the case on the executing half of a
        // fresh edit: assigning would re-split every line and throw away the editor's highlighting
        // cache once per keystroke, for no change at all.
        if (string.Equals(Buffer.Text, text, StringComparison.Ordinal)) {
            return;
        }

        applying = true;

        try {
            Buffer.Text = text;
        } finally {
            applying = false;
        }
    }

    void Edited(CodeBuffer buffer) {
        if (applying) {
            return;
        }

        var text = buffer.Text;

        if (string.Equals(text, recorded, StringComparison.Ordinal)) {
            return;
        }

        var command = new TextEditCommand(this, recorded, text);
        recorded = text;

        // Executed rather than merely pushed, and Do is a no-op re-assignment of the text that is
        // already there — which keeps one path for "how does an entry take effect" instead of two.
        Stack.Execute(command);
    }
}
