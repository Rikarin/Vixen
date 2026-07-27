// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>One paragraph, part-way through the bidirectional algorithm.</summary>
/// <remarks>
///     Mutable and single-use. The algorithm rewrites types in place across a dozen passes, and
///     modelling that as a pipeline of immutable arrays would allocate a dozen copies of the
///     paragraph to express something that is genuinely a sequence of edits.
/// </remarks>
sealed class BidiState {
    readonly int[] codePoints;
    readonly BidiClass[] initial;
    readonly BidiClass[] types;
    readonly int paragraphLevel;
    readonly int[] matchingPdi;
    readonly int[] matchingIsolate;

    /// <summary>The embedding level of each code point.</summary>
    public int[] Levels { get; }

    /// <summary>The levels as the X rules left them, before I1 and I2 rewrote any of them.</summary>
    /// <remarks>
    ///     ⚠ A snapshot, and it has to be one. <c>Levels</c> is the working array: the implicit rules
    ///     raise a right-to-left character by one and a number by two, in place. Everything that
    ///     reads a level for <i>context</i> — which run a position belongs to, and the <c>sos</c> and
    ///     <c>eos</c> at a sequence's boundaries — has to read what the explicit rules decided, not
    ///     what a rule from a different sequence has since written there.
    ///     <para>
    ///         Without it the sequences corrupt each other in source order, and the failure looks
    ///         nothing like its cause: an <c>LRE</c> paragraph came out with the levels of the
    ///         <c>RLE</c> one, because the run before it had already been raised.
    ///     </para>
    /// </remarks>
    public int[] ExplicitLevels { get; private set; } = [];

    public BidiState(ReadOnlySpan<int> codePoints, BidiClass[] classes, int paragraphLevel) {
        this.codePoints = codePoints.ToArray();
        this.paragraphLevel = paragraphLevel;

        initial = (BidiClass[]) classes.Clone();
        types = classes;
        Levels = new int[classes.Length];

        matchingPdi = new int[classes.Length];
        matchingIsolate = new int[classes.Length];
        FindMatchingIsolates();
    }

    /// <summary>BD9 — which PDI closes each isolate initiator, and which initiator each PDI closes.</summary>
    /// <remarks>
    ///     Computed once, up front, because four separate rules need it and each would otherwise
    ///     rescan. An initiator with no matching PDI points past the end of the paragraph, which is
    ///     what BD9 says and is what makes the isolate rules terminate.
    /// </remarks>
    void FindMatchingIsolates() {
        Array.Fill(matchingPdi, -1);
        Array.Fill(matchingIsolate, -1);

        for (var i = 0; i < types.Length; i++) {
            if (types[i] is not (BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)) {
                continue;
            }

            var depth = 1;
            var found = false;

            for (var j = i + 1; j < types.Length; j++) {
                if (types[j] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI) {
                    depth++;
                    continue;
                }

                if (types[j] != BidiClass.PDI) {
                    continue;
                }

                if (--depth != 0) {
                    continue;
                }

                matchingPdi[i] = j;
                matchingIsolate[j] = i;
                found = true;
                break;
            }

            if (!found) {
                matchingPdi[i] = types.Length;
            }
        }
    }

    /// <summary>X1 to X8 — the explicit embedding, override and isolate controls.</summary>
    /// <remarks>
    ///     A stack machine, and the only part of the algorithm that is one. The two counters are
    ///     what stop a malformed run of controls from either overflowing the stack or silently
    ///     rebalancing: an embedding that could not be pushed is remembered as <i>overflowed</i> so
    ///     that its terminator pops nothing.
    /// </remarks>
    public void ResolveExplicitLevels() {
        var stack = new Status[MaxStack];
        var depth = 0;

        stack[0] = new Status(paragraphLevel, BidiClass.Other, false);

        var overflowIsolates = 0;
        var overflowEmbeddings = 0;
        var validIsolates = 0;

        for (var i = 0; i < types.Length; i++) {
            var type = types[i];

            switch (type) {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO: {
                    // X2 to X5. The control itself takes the level it is *raising from*, and is
                    // removed later by X9.
                    Levels[i] = stack[depth].Level;

                    var next = type is BidiClass.RLE or BidiClass.RLO
                        ? NextOdd(stack[depth].Level)
                        : NextEven(stack[depth].Level);

                    if (next <= BidiAlgorithm.MaximumDepth && overflowIsolates == 0 && overflowEmbeddings == 0) {
                        stack[++depth] = new Status(
                            next,
                            type switch {
                                BidiClass.RLO => BidiClass.R,
                                BidiClass.LRO => BidiClass.L,
                                _ => BidiClass.Other
                            },
                            false
                        );
                    } else if (overflowIsolates == 0) {
                        overflowEmbeddings++;
                    }

                    break;
                }

                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI: {
                    // X5a, X5b, X5c. An isolate takes the level it sits at, unlike an embedding,
                    // because it is not removed and has to be reordered with its surroundings.
                    var effective = type;

                    if (type == BidiClass.FSI) {
                        var end = matchingPdi[i];
                        effective = BidiAlgorithm.AutoLevel(initial, i + 1, Math.Min(end, types.Length)) == 1
                            ? BidiClass.RLI
                            : BidiClass.LRI;
                    }

                    Levels[i] = stack[depth].Level;

                    if (stack[depth].Override != BidiClass.Other) {
                        types[i] = stack[depth].Override;
                    }

                    var next = effective == BidiClass.RLI
                        ? NextOdd(stack[depth].Level)
                        : NextEven(stack[depth].Level);

                    if (next <= BidiAlgorithm.MaximumDepth && overflowIsolates == 0 && overflowEmbeddings == 0) {
                        validIsolates++;
                        stack[++depth] = new Status(next, BidiClass.Other, true);
                    } else {
                        overflowIsolates++;
                    }

                    break;
                }

                case BidiClass.PDI: {
                    // X6a. An isolate terminator closes the innermost *valid* isolate, popping every
                    // embedding opened inside it on the way — which is what makes isolates isolating.
                    if (overflowIsolates > 0) {
                        overflowIsolates--;
                    } else if (validIsolates > 0) {
                        overflowEmbeddings = 0;

                        while (!stack[depth].Isolate) {
                            depth--;
                        }

                        depth--;
                        validIsolates--;
                    }

                    Levels[i] = stack[depth].Level;

                    if (stack[depth].Override != BidiClass.Other) {
                        types[i] = stack[depth].Override;
                    }

                    break;
                }

                case BidiClass.PDF: {
                    // X7. It pops nothing if the matching initiator overflowed, which is what keeps
                    // a malformed document from unbalancing the whole paragraph.
                    Levels[i] = stack[depth].Level;

                    if (overflowIsolates > 0) {
                        break;
                    }

                    if (overflowEmbeddings > 0) {
                        overflowEmbeddings--;
                    } else if (!stack[depth].Isolate && depth > 0) {
                        depth--;
                    }

                    break;
                }

                case BidiClass.B: {
                    // X8. A paragraph separator resets everything; it belongs to the paragraph.
                    depth = 0;
                    overflowIsolates = 0;
                    overflowEmbeddings = 0;
                    validIsolates = 0;
                    Levels[i] = paragraphLevel;
                    break;
                }

                default: {
                    // X6.
                    Levels[i] = stack[depth].Level;

                    if (stack[depth].Override != BidiClass.Other) {
                        types[i] = stack[depth].Override;
                    }

                    break;
                }
            }
        }

        ExplicitLevels = (int[]) Levels.Clone();
    }

    /// <summary>BD13 and X10 — chops the paragraph into isolating run sequences and resolves each.</summary>
    public void ResolveSequences() {
        // X9 — the embedding controls and boundary neutrals are removed from consideration. Done by
        // *marking* rather than by deleting, because the levels array has to stay indexable by the
        // original positions all the way to the end.
        var removed = new bool[types.Length];
        for (var i = 0; i < types.Length; i++) {
            removed[i] = initial[i] is BidiClass.RLE or BidiClass.LRE or BidiClass.RLO
                or BidiClass.LRO or BidiClass.PDF or BidiClass.BN;
        }

        foreach (var sequence in BuildSequences(removed)) {
            new BidiRunSequence(this, sequence, paragraphLevel).Resolve();
        }
    }

    /// <summary>BD13 — a level run, plus every run continuing it across a matching isolate.</summary>
    List<List<int>> BuildSequences(bool[] removed) {
        var order = new List<int>();
        for (var i = 0; i < types.Length; i++) {
            if (!removed[i]) {
                order.Add(i);
            }
        }

        // Level runs first: maximal stretches of equal level, over what X9 left behind.
        var runs = new List<List<int>>();
        for (var i = 0; i < order.Count;) {
            var run = new List<int> { order[i] };
            var level = ExplicitLevels[order[i]];
            i++;

            while (i < order.Count && ExplicitLevels[order[i]] == level) {
                run.Add(order[i]);
                i++;
            }

            runs.Add(run);
        }

        // Then join them: a run ending in an isolate initiator that has a matching PDI continues
        // into the run beginning with that PDI. This is what makes an isolate one context rather
        // than three, and it is the rule everything downstream silently depends on.
        var used = new bool[runs.Count];
        var sequences = new List<List<int>>();

        for (var i = 0; i < runs.Count; i++) {
            if (used[i] || (initial[runs[i][0]] == BidiClass.PDI && matchingIsolate[runs[i][0]] >= 0)) {
                continue;
            }

            var sequence = new List<int>();
            var current = i;

            while (true) {
                used[current] = true;
                sequence.AddRange(runs[current]);

                var last = runs[current][^1];
                if (initial[last] is not (BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)
                    || matchingPdi[last] >= types.Length) {
                    break;
                }

                var next = runs.FindIndex(run => run[0] == matchingPdi[last]);
                if (next < 0 || used[next]) {
                    break;
                }

                current = next;
            }

            sequences.Add(sequence);
        }

        return sequences;
    }

    /// <summary>L1 — the levels that snap back to the paragraph's regardless of what was resolved.</summary>
    /// <remarks>
    ///     Trailing whitespace, and the separators that end a line. Without it a right-to-left
    ///     paragraph puts its trailing spaces on the left, where they push the text away from the
    ///     margin it should be against.
    /// </remarks>
    public void ApplyL1() {
        var resetFrom = 0;

        for (var i = 0; i < types.Length; i++) {
            switch (initial[i]) {
                case BidiClass.B or BidiClass.S:
                    Levels[i] = paragraphLevel;

                    for (var j = resetFrom; j < i; j++) {
                        Levels[j] = paragraphLevel;
                    }

                    resetFrom = i + 1;
                    break;

                case BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI:
                    break;

                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO
                    or BidiClass.PDF or BidiClass.BN:
                    // Removed by X9, and transparent here too — a space before an embedding control
                    // is still trailing whitespace.
                    break;

                default:
                    resetFrom = i + 1;
                    break;
            }
        }

        for (var j = resetFrom; j < types.Length; j++) {
            Levels[j] = paragraphLevel;
        }
    }

    /// <summary>L2 — the visual order, with the characters L3 and L4 would remove already gone.</summary>
    /// <remarks>
    ///     Reverse every run at the deepest level, then every run at one level up, and so on down to
    ///     the lowest odd level. Doing it from the deepest up is what makes nested direction changes
    ///     compose; doing it the other way round produces something that is right for one level of
    ///     nesting and wrong for two.
    /// </remarks>
    public int[] VisualOrder() {
        var visible = new List<int>();
        for (var i = 0; i < types.Length; i++) {
            if (initial[i] is BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO
                or BidiClass.PDF or BidiClass.BN) {
                continue;
            }

            visible.Add(i);
        }

        if (visible.Count == 0) {
            return [];
        }

        var highest = 0;
        var lowestOdd = BidiAlgorithm.MaximumDepth + 1;

        foreach (var i in visible) {
            highest = Math.Max(highest, Levels[i]);

            if (Levels[i] % 2 == 1) {
                lowestOdd = Math.Min(lowestOdd, Levels[i]);
            }
        }

        for (var level = highest; level >= lowestOdd; level--) {
            for (var i = 0; i < visible.Count; i++) {
                if (Levels[visible[i]] < level) {
                    continue;
                }

                var start = i;
                while (i < visible.Count && Levels[visible[i]] >= level) {
                    i++;
                }

                visible.Reverse(start, i - start);
            }
        }

        return [.. visible];
    }

    internal BidiClass TypeOf(int index) => types[index];

    internal BidiClass InitialTypeOf(int index) => initial[index];

    internal int CodePointOf(int index) => codePoints[index];

    internal void SetType(int index, BidiClass value) => types[index] = value;

    internal int LevelOf(int index) => Levels[index];

    /// <summary>The level the explicit rules gave a position, whatever has happened since.</summary>
    internal int ExplicitLevelOf(int index) => ExplicitLevels[index];

    internal void SetLevel(int index, int value) => Levels[index] = value;

    internal int ParagraphLevel => paragraphLevel;

    static int NextOdd(int level) => level + 1 + ((level + 1) % 2 == 0 ? 1 : 0);

    static int NextEven(int level) => level + 1 + ((level + 1) % 2 == 1 ? 1 : 0);

    // 125 levels plus the paragraph's, plus one so that an overflow has somewhere to be detected.
    const int MaxStack = BidiAlgorithm.MaximumDepth + 3;

    readonly record struct Status(int Level, BidiClass Override, bool Isolate);
}
