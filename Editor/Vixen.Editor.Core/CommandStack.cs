// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Core;

/// <summary>An undo history: what has been done, what has been undone, and where "saved" was.</summary>
/// <remarks>
///     <para>
///         <b>There is one of these per document and one for the project.</b> A document's stack holds
///         the edits made inside it; the project's global stack holds the operations that are not
///         inside any one document — renaming an asset, moving a file, reorganising a folder — because
///         putting a cross-document operation on one of the documents it happened to touch means undoing
///         it depends on which tab has focus.
///     </para>
///     <para>
///         <b>The interaction between the two is one rule:</b> when a command touches a document that
///         is not the one its stack belongs to, that document's redo stack is discarded and it is
///         marked modified. Its redo entries were recorded against a world that has since moved, and
///         replaying them would write stale values back. Discarding is the only answer that cannot
///         silently corrupt; the alternative — rewriting the affected entries — needs every command
///         type to know how to be rebased, which is a research project.
///     </para>
///     <para>
///         <b>Everything a menu binds to is a signal.</b> "Undo" is enabled from
///         <see cref="CanUndo" /> and labelled from <see cref="UndoName" />, and a title bar's asterisk
///         comes from <see cref="IsDirty" />, with no change notifications to wire up and nothing to
///         forget to raise.
///     </para>
///     <para>
///         ⚠ <b>It is also <see cref="IUndoManager" />, which is how a <c>Vixen.Ui</c> control's
///         edit lands here.</b> A control does not own an undo stack — <c>CodeBuffer</c> argues that
///         and is right — it <i>finds</i> one by walking the responder chain, and until this
///         implemented the interface the answer everywhere in the editor was nothing, so a text box
///         in any dialog had no ⌘Z at all. One stack rather than an adapter beside it, because two
///         would mean ⌘Z undid the typing and left the move that came before it.
///     </para>
/// </remarks>
public sealed class CommandStack : IUndoManager {
    /// <summary>How many entries a stack keeps before the oldest starts falling off.</summary>
    public const int DefaultCapacity = 256;

    readonly List<IEditorCommand> undoable = [];
    readonly List<IEditorCommand> redoable = [];
    readonly List<IEditorCommand> transacted = [];
    readonly Signal<int> revision = new(0);

    /// <summary>How deep the undo list was when the document was last saved, or null when that point is gone.</summary>
    int? cleanDepth;

    int capacity = DefaultCapacity;
    int transactionDepth;
    string transactionName = "";
    bool transactionAbandoned;
    bool sealedForMerge;

    /// <summary>What the commands on this stack are handed.</summary>
    public EditorContext Context { get; }

    /// <summary>Whether this is the project's stack rather than a document's.</summary>
    public bool IsGlobal => Context.Document is null;

    /// <summary>Whether <see cref="Undo" /> would do something.</summary>
    public IReadOnlySignal<bool> CanUndo { get; }

    /// <summary>Whether <see cref="Redo" /> would do something.</summary>
    public IReadOnlySignal<bool> CanRedo { get; }

    /// <summary>Whether anything has changed since the last <see cref="MarkClean" />.</summary>
    public IReadOnlySignal<bool> IsDirty { get; }

    /// <summary>What <see cref="Undo" /> would undo, or <see langword="null" /> if nothing.</summary>
    public IReadOnlySignal<string?> UndoName { get; }

    /// <summary>What <see cref="Redo" /> would redo, or <see langword="null" /> if nothing.</summary>
    public IReadOnlySignal<string?> RedoName { get; }

    /// <summary>How many entries can be undone.</summary>
    public IReadOnlySignal<int> Depth { get; }

    /// <summary>The undo entries, oldest first.</summary>
    /// <remarks>For an undo-history panel, and for a crash report, which is worth more than it sounds.</remarks>
    public IReadOnlyList<IEditorCommand> History => undoable;

    /// <summary>Whether a transaction is open.</summary>
    public bool InTransaction => transactionDepth > 0;

    /// <summary>How many entries this stack keeps, or zero for no limit.</summary>
    /// <remarks>
    ///     A limit rather than none by default, because an editor left open for a week otherwise
    ///     holds every value every slider has ever had. Lowering it drops the oldest entries
    ///     immediately, and if the last-saved point was among them the document stays dirty for good
    ///     — which is honest: there is no longer a sequence of undos that gets back to what is on disk.
    /// </remarks>
    public int Capacity {
        get => capacity;
        set {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            capacity = value;
            Trim();
            Bump();
        }
    }

    internal CommandStack(EditorContext context) {
        Context = context;
        cleanDepth = 0;

        // Every one of these is a pure function of the two lists, and the lists are not signals — so
        // they read `revision`, which every mutation below bumps. Equality still stops propagation:
        // a stack going from two entries to three does not re-evaluate anything bound to CanUndo.
        CanUndo = new Computed<bool>(() => Tracked(undoable.Count > 0));
        CanRedo = new Computed<bool>(() => Tracked(redoable.Count > 0));
        IsDirty = new Computed<bool>(() => Tracked(cleanDepth != undoable.Count));
        Depth = new Computed<int>(() => Tracked(undoable.Count));
        UndoName = new Computed<string?>(() => Tracked(undoable.Count > 0 ? undoable[^1].Name : null));
        RedoName = new Computed<string?>(() => Tracked(redoable.Count > 0 ? redoable[^1].Name : null));
    }

    /// <summary>Runs a command and records it.</summary>
    /// <param name="command">What to do.</param>
    /// <remarks>
    ///     The command is executed first and merged second, so a merge replaces the top entry with
    ///     one that has already been applied rather than re-running anything.
    /// </remarks>
    public void Execute(IEditorCommand command) {
        ArgumentNullException.ThrowIfNull(command);

        if (transactionAbandoned) {
            throw new InvalidOperationException(
                "The open transaction was cancelled and everything in it has been rolled back. "
                + "Close it before executing anything else."
            );
        }

        Run(command, undo: false);

        if (transactionDepth > 0) {
            transacted.Add(command);
            return;
        }

        Record(command);
    }

    /// <summary>Undoes the newest entry.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Undo() {
        AssertNoTransaction();

        if (undoable.Count == 0) {
            return false;
        }

        var command = undoable[^1];
        undoable.RemoveAt(undoable.Count - 1);
        Perform(command, undo: true);
        redoable.Add(command);

        // Whatever comes next starts a new entry. Merging a fresh edit into an entry the user has
        // just stepped past would make the step they took unrepeatable.
        sealedForMerge = true;
        Bump();
        return true;
    }

    /// <summary>Redoes the newest undone entry.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Redo() {
        AssertNoTransaction();

        if (redoable.Count == 0) {
            return false;
        }

        var command = redoable[^1];
        redoable.RemoveAt(redoable.Count - 1);
        Perform(command, undo: false);
        undoable.Add(command);

        sealedForMerge = true;
        Bump();
        return true;
    }

    /// <summary>Closes the newest entry to merging, so the next command starts a fresh undo step.</summary>
    /// <remarks>
    ///     The shell calls this when a drag ends or a field loses focus. It is explicit rather than a
    ///     time window on purpose: a window makes how many undo steps an edit produced depend on how
    ///     fast somebody moved a mouse, which is neither predictable for the user nor testable without
    ///     a fake clock.
    /// </remarks>
    public void Seal() => sealedForMerge = true;

    /// <summary>Records that this is the state on disk.</summary>
    /// <remarks>Called by <see cref="EditorDocument.Save" />; undoing back to here reports clean again.</remarks>
    public void MarkClean() {
        cleanDepth = undoable.Count;
        Bump();
    }

    /// <summary>Throws away everything that could be redone.</summary>
    /// <remarks>
    ///     What a document does when something outside its own stack changed it. Public because
    ///     "these redo entries are no longer valid" is a real thing for a caller to know and to say.
    /// </remarks>
    public void ClearRedo() {
        if (redoable.Count == 0) {
            return;
        }

        DiscardRedo();
        Bump();
    }

    /// <summary>Throws away the whole history.</summary>
    /// <remarks>
    ///     The dirty flag survives it: a stack cleared while clean is still clean, and one cleared
    ///     while dirty is dirty for good, because the sequence of undos that got back to disk has
    ///     just been deleted.
    /// </remarks>
    public void Clear() {
        var wasClean = cleanDepth == undoable.Count;
        undoable.Clear();
        redoable.Clear();
        transacted.Clear();
        transactionDepth = 0;
        transactionAbandoned = false;
        cleanDepth = wasClean ? 0 : null;
        Bump();
    }

    /// <summary>Collects everything executed until it closes into one undo entry.</summary>
    /// <param name="name">What the one entry is called.</param>
    /// <returns>The transaction. Disposing it commits.</returns>
    /// <remarks>
    ///     For an operation that is several commands and one edit — "paste", which creates entities
    ///     and then selects them. Nesting is allowed and only the outermost commit records anything,
    ///     so a helper that opens one of its own composes rather than producing two entries.
    /// </remarks>
    public CommandTransaction BeginTransaction(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (transactionDepth == 0) {
            transacted.Clear();
            transactionName = name;
            transactionAbandoned = false;
        }

        transactionDepth++;
        return new(this);
    }

    internal void CommitTransaction() {
        // Clear() drops an open transaction along with the history. The transaction object outlives
        // that and still gets disposed, and a depth going negative would make every later
        // BeginTransaction commit at the wrong nesting level.
        if (transactionDepth == 0) {
            return;
        }

        transactionDepth--;

        if (transactionDepth > 0) {
            return;
        }

        if (!transactionAbandoned && transacted.Count > 0) {
            Record(new CompositeCommand(transactionName, [.. transacted]));
        }

        transacted.Clear();
        transactionAbandoned = false;
    }

    /// <summary>
    ///     Undoes everything collected so far, in reverse, and refuses anything further until the
    ///     transaction closes. A cancel abandons the whole transaction rather than one nested frame:
    ///     the frames share a rollback list, so there is nothing coherent a partial one could mean.
    /// </summary>
    internal void CancelTransaction() {
        // Nothing to roll back once it has been abandoned, and nothing to roll back if Clear() took
        // the transaction with the history — marking it abandoned then would block every later
        // Execute on a transaction that no longer exists.
        if (transactionAbandoned || transactionDepth == 0) {
            return;
        }

        transactionAbandoned = true;

        for (var index = transacted.Count - 1; index >= 0; index--) {
            Run(transacted[index], undo: true);
        }

        transacted.Clear();
    }

    void Record(IEditorCommand command) {
        // The redo check is implied by the seal — Undo and Redo both set it, and they are the only
        // things that leave a redo entry behind — and it is written out anyway, because it states the
        // invariant where it matters rather than making it a fact about two other methods.
        if (redoable.Count == 0 && !sealedForMerge && undoable.Count > 0
            && command.TryMergeWith(undoable[^1], out var merged)) {
            undoable[^1] = merged;
        } else {
            DiscardRedo();
            undoable.Add(command);
            Trim();
        }

        sealedForMerge = false;
        Bump();
    }

    /// <summary>Runs a command as an undo or a redo, with <see cref="IsPerforming" /> set.</summary>
    /// <remarks>
    ///     ⚠ <b>Only around undo and redo, and deliberately not around <see cref="Execute" />.</b>
    ///     The flag answers "is this edit a consequence of the stack replaying one?", which is the
    ///     question a control has to ask before recording — a field whose value the undo just put
    ///     back would otherwise push the undo of the undo and never reach the state before it.
    ///     Setting it for an ordinary execute would make the stack refuse every edit made by a
    ///     command, which is most of them.
    /// </remarks>
    void Perform(IEditorCommand command, bool undo) {
        IsPerforming = true;

        try {
            Run(command, undo);
        } finally {
            IsPerforming = false;
        }
    }

    void Run(IEditorCommand command, bool undo) {
        Context.BeginRecording();

        try {
            if (undo) {
                command.Undo(Context);
            } else {
                command.Do(Context);
            }
        } finally {
            // Even a command that threw halfway may have changed a document, and a document that was
            // changed is dirty whatever happened next.
            ApplyTouches(Context.EndRecording());
        }
    }

    void ApplyTouches(List<EditorDocument> touched) {
        for (var index = 0; index < touched.Count; index++) {
            var document = touched[index];

            // Its own document is already accounted for: this stack's depth is that document's
            // dirty flag. Anything else was reached from outside, which is the case the rule is about.
            if (!ReferenceEquals(document, Context.Document)) {
                document.MarkModifiedExternally();
            }
        }
    }

    void DiscardRedo() {
        redoable.Clear();

        // The saved state was somewhere in the branch that has just been abandoned, so no number of
        // undos reaches it any more. Without this a document that was undone past its save point and
        // then edited would come back to the same depth and claim to be clean with different content.
        if (cleanDepth > undoable.Count) {
            cleanDepth = null;
        }
    }

    void Trim() {
        // RemoveAt(0) is a memmove of at most Capacity entries and happens once per command past the
        // limit. A ring buffer would avoid it and would make History, which a panel reads far more
        // often, an allocation.
        while (capacity > 0 && undoable.Count > capacity) {
            undoable.RemoveAt(0);
            cleanDepth = cleanDepth > 0 ? cleanDepth - 1 : null;
        }
    }

    void AssertNoTransaction() {
        if (transactionDepth > 0) {
            throw new InvalidOperationException(
                "Undo and redo are not available inside a transaction: the entry it is building does "
                + "not exist yet. Close the transaction first."
            );
        }
    }

    T Tracked<T>(T value) {
        _ = revision.Value;
        return value;
    }

    void Bump() => revision.Value = unchecked(revision.Peek() + 1);

    // ── IUndoManager: the same stack, seen from Vixen.Ui ─────────────────────

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The one piece of state a control cannot work out for itself.</b> Undoing an edit
    ///     re-runs the code that made it, so a control that recorded unconditionally would push the
    ///     undo of an undo. Every implementation of <see cref="IUndoManager" /> answers this and
    ///     every registrant checks it — and <see cref="IUndoManager.Register" /> below checks it
    ///     again, because "the manager is already doing that check" is what makes the correct
    ///     control's code short.
    /// </remarks>
    public bool IsPerforming { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Explicit, because <see cref="CanUndo" /> on this class is the <i>signal</i> a menu binds
    ///     to and the interface wants the value. Two names for one fact would be worse than one name
    ///     reached two ways.
    /// </remarks>
    bool IUndoManager.CanUndo => CanUndo.Value;

    /// <inheritdoc cref="IUndoManager.CanUndo" />
    bool IUndoManager.CanRedo => CanRedo.Value;

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Recorded and not run, which is the opposite of <see cref="Execute" />.</b> The
    ///         edit has already happened — a text field records what the user just typed — so
    ///         applying it here would apply it twice. It goes through the same
    ///         <see cref="Record" /> the executed path ends at, so a typed run still merges, still
    ///         discards the redo future, and still trims at <see cref="Capacity" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Inside a transaction it is refused rather than mis-filed.</b> An open transaction
    ///         is building one entry out of commands it ran itself; an already-applied edit arriving
    ///         from a control has no place in it, and adding it to <c>transacted</c> would make the
    ///         transaction's rollback call <c>Undo</c> on something it never did.
    ///     </para>
    /// </remarks>
    void IUndoManager.Register(string name, Action undo, Action redo) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);

        // Silently, on `UndoManager`'s terms: this is the guard the correct control also makes, and
        // a throw here would make every correct control responsible for a check it already does.
        if (IsPerforming || transactionDepth > 0) {
            return;
        }

        Record(new AppliedEdit(name, undo, redo));
    }

    /// <summary>An edit somebody else already applied, as something this stack can replay.</summary>
    /// <remarks>
    ///     ⚠ <b>It never merges.</b> <see cref="IEditorCommand.TryMergeWith" /> defaults to refusing,
    ///     and that is right here for a reason the default's own remarks give: merging is a claim
    ///     that two operations are one edit, and this type knows nothing about the two closures it
    ///     is holding. A control that wants a run of typing to be one step coalesces before it
    ///     registers — <c>TextField</c> does, by shape rather than by a clock.
    /// </remarks>
    sealed class AppliedEdit : IEditorCommand {
        readonly Action undo;
        readonly Action redo;

        public AppliedEdit(string name, Action undo, Action redo) {
            Name = name;

            this.undo = undo;
            this.redo = redo;
        }

        public string Name { get; }

        public void Do(EditorContext context) => redo();

        public void Undo(EditorContext context) => undo();
    }
}
