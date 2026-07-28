// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>
///     Random sequences of edits, undos and redos, checked against a model that keeps every version
///     of the document it has ever been in.
/// </summary>
/// <remarks>
///     <para>
///         [11](../../docs/plan/11-editor.md) § Editor testing asks for exactly this: "randomised
///         command sequences leave the model equal to a reference; undo to empty restores the initial
///         state exactly". The reference is the dumbest thing that could work — a list of complete
///         snapshots and an index into it — which is the design undo/redo is an optimisation of, and
///         which is therefore incapable of being wrong in the same direction as the code under test.
///     </para>
///     <para>
///         The interesting part is not the values but the <em>merging</em>: an entry on the real stack
///         may stand for any number of edits, so the two structures do not line up entry for entry and
///         the model has to record a snapshot only where the stack records one. That is what
///         <see cref="CommandStack.Depth" /> is compared against, and it is the assertion that would
///         catch a merge that swallowed an edit it should not have.
///     </para>
///     <para>
///         <b><c>threads: 1</c> is load-bearing.</b> CsCheck runs a sample across every logical CPU by
///         default, and the signal graph these stacks are built on keeps its write epoch in one
///         process-wide counter that is incremented without a lock — deliberately, because the graph
///         is single-threaded by design. Four hundred sessions running at once drop increments, a
///         computed is left believing it is still clean, and the sample fails on an input that passes
///         when replayed on its own. Disabling xunit's parallelism is not enough: this parallelism is
///         inside one test.
///     </para>
/// </remarks>
public sealed class CommandStackPropertyTests {
    [Fact]
    public void RandomEditUndoRedoSequencesAgreeWithASnapshotModel() =>
        Gen.Select(Gen.Int[0, int.MaxValue], Gen.Int[10, 200])
            .Sample(input => Replay(input.Item1, input.Item2), iter: 400, threads: 1);

    [Fact]
    public void UndoingEverythingRestoresTheInitialStateExactly() =>
        Gen.Select(Gen.Int[0, int.MaxValue], Gen.Int[10, 200])
            .Sample(input => ReplayThenUnwind(input.Item1, input.Item2), iter: 400, threads: 1);

    static bool Replay(int seed, int steps) {
        var run = new Run(seed);

        for (var step = 0; step < steps; step++) {
            run.Step();

            if (!run.Agrees()) {
                return false;
            }
        }

        return true;
    }

    static bool ReplayThenUnwind(int seed, int steps) {
        var run = new Run(seed);

        for (var step = 0; step < steps; step++) {
            run.Step();
        }

        while (run.Document.Stack.Undo()) {
            // All the way down.
        }

        return run.Knobs.All(knob => knob.Amount.Peek() == 0f && knob.Label.Peek() == "none")
            && !run.Document.Stack.CanUndo.Value;
    }

    /// <summary>One randomised session, with the model beside it.</summary>
    sealed class Run {
        readonly List<State> snapshots = [new(0f, 0f, "none", "none")];
        uint state;
        int position;

        public TestDocument Document { get; }

        public Knob[] Knobs { get; }

        public Run(int seed) {
            state = (uint)seed | 1u;
            Document = new(ModelFixture.Project());
            Knobs = [new(Document), new(Document)];
        }

        public void Step() {
            var choice = Next() % 100;

            switch (choice) {
                case < 15 when position > 0:
                    Document.Stack.Undo();
                    position--;
                    return;

                case < 30 when position < snapshots.Count - 1:
                    Document.Stack.Redo();
                    position++;
                    return;

                case < 40:
                    // What the shell does on a mouse-up: the next edit starts a new entry whether or
                    // not it would otherwise have merged.
                    Document.Stack.Seal();
                    return;

                default:
                    Edit();
                    return;
            }
        }

        public bool Agrees() =>
            Current() == snapshots[position]
            && Document.Stack.Depth.Value == position
            && Document.Stack.CanUndo.Value == (position > 0)
            && Document.Stack.CanRedo.Value == (position < snapshots.Count - 1);

        void Edit() {
            var knob = Knobs[(int)(Next() % (uint)Knobs.Length)];
            var before = Document.Stack.Depth.Value;

            if (Next() % 2 == 0) {
                knob.Amount.Set(Next() % 8);
            } else {
                knob.Label.Set("v" + Next() % 8);
            }

            var after = Document.Stack.Depth.Value;

            if (after > before) {
                // A new entry: everything that could have been redone is gone, and this is a new
                // point the model has to be able to come back to.
                snapshots.RemoveRange(position + 1, snapshots.Count - position - 1);
                snapshots.Add(Current());
                position++;
            } else {
                // Merged into the entry the model already holds, or changed nothing at all. Either
                // way the newest snapshot is now whatever the document says it is.
                snapshots[position] = Current();
            }
        }

        State Current() =>
            new(Knobs[0].Amount.Peek(), Knobs[1].Amount.Peek(), Knobs[0].Label.Peek(), Knobs[1].Label.Peek());

        uint Next() {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }

    readonly record struct State(float First, float Second, string FirstLabel, string SecondLabel);
}
