// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Text;

/// <summary>One isolating run sequence, and the rules that resolve its types.</summary>
/// <remarks>
///     <para>
///         Everything after X10 works on one of these rather than on the paragraph, which is what
///         lets a rule say "the previous character" and mean it — inside an isolate, the character
///         before the PDI is the one before the matching initiator, not the last one inside.
///     </para>
///     <para>
///         The <b>sos</b> and <b>eos</b> types are how a sequence knows what it is embedded in. They
///         stand in for the neighbouring context at the boundaries, so that a rule needing a
///         preceding character always has one, and are computed from the higher of the sequence's
///         level and its neighbour's.
///     </para>
/// </remarks>
sealed class BidiRunSequence {
    readonly BidiState state;
    readonly List<int> indices;
    readonly BidiClass sos;
    readonly BidiClass eos;
    readonly int level;

    public BidiRunSequence(BidiState state, List<int> indices, int paragraphLevel) {
        this.state = state;
        this.indices = indices;
        level = state.ExplicitLevelOf(indices[0]);

        // X10 — the boundary types. Each end compares this sequence's level with the level of what
        // is actually adjacent in the paragraph, skipping what X9 removed, and takes the direction
        // implied by the higher of the two.
        sos = BoundaryType(Math.Max(level, PreviousLevel(indices[0], paragraphLevel)));

        var last = indices[^1];
        var trailing = state.InitialTypeOf(last) is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI
            ? paragraphLevel
            : NextLevel(last, paragraphLevel);

        eos = BoundaryType(Math.Max(level, trailing));
    }

    /// <summary>Runs W1 to I2 over the sequence.</summary>
    public void Resolve() {
        ResolveWeak();
        ResolveBrackets();
        ResolveNeutrals();
        ResolveImplicit();
    }

    /// <summary>W1 to W7 — the weak types, in order and in place.</summary>
    /// <remarks>
    ///     Each rule sees what the one before it produced, which is why they cannot be collapsed
    ///     into a single pass: W4 turns a separator between two numbers into a number, and W5 then
    ///     treats a run of terminators next to <i>that</i> number as European numbers too.
    /// </remarks>
    void ResolveWeak() {
        // W1 — a non-spacing mark takes the type of what it marks, and an isolate boundary makes it
        // ON rather than letting it inherit across the isolate.
        var previous = sos;
        for (var i = 0; i < indices.Count; i++) {
            var type = TypeAt(i);

            if (type == BidiClass.NSM) {
                SetType(i, previous is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI
                    ? BidiClass.ON
                    : previous);
            }

            previous = TypeAt(i);
        }

        // W2 — a European number after an Arabic letter is an Arabic number.
        var strong = sos;
        for (var i = 0; i < indices.Count; i++) {
            var type = TypeAt(i);

            if (type is BidiClass.L or BidiClass.R or BidiClass.AL) {
                strong = type;
            } else if (type == BidiClass.EN && strong == BidiClass.AL) {
                SetType(i, BidiClass.AN);
            }
        }

        // W3 — and the Arabic letter itself is simply right-to-left from here on.
        for (var i = 0; i < indices.Count; i++) {
            if (TypeAt(i) == BidiClass.AL) {
                SetType(i, BidiClass.R);
            }
        }

        // W4 — a single separator between two numbers of the same kind joins them. `1,000` and
        // `1.000` are one number; `1,,000` is not.
        for (var i = 1; i < indices.Count - 1; i++) {
            var type = TypeAt(i);

            if (type is not (BidiClass.ES or BidiClass.CS)) {
                continue;
            }

            var before = TypeAt(i - 1);
            var after = TypeAt(i + 1);

            if (before == BidiClass.EN && after == BidiClass.EN) {
                SetType(i, BidiClass.EN);
            } else if (type == BidiClass.CS && before == BidiClass.AN && after == BidiClass.AN) {
                SetType(i, BidiClass.AN);
            }
        }

        // W5 — a run of terminators adjacent to a European number becomes European numbers. `$12`
        // and `12%` both, and the run may be several characters long.
        for (var i = 0; i < indices.Count; i++) {
            if (TypeAt(i) != BidiClass.ET) {
                continue;
            }

            var start = i;
            while (i < indices.Count && TypeAt(i) == BidiClass.ET) {
                i++;
            }

            var before = start == 0 ? sos : TypeAt(start - 1);
            var after = i == indices.Count ? eos : TypeAt(i);

            if (before != BidiClass.EN && after != BidiClass.EN) {
                i--;
                continue;
            }

            for (var j = start; j < i; j++) {
                SetType(j, BidiClass.EN);
            }

            i--;
        }

        // W6 — whatever separators and terminators are left are just neutral.
        for (var i = 0; i < indices.Count; i++) {
            if (TypeAt(i) is BidiClass.ES or BidiClass.ET or BidiClass.CS) {
                SetType(i, BidiClass.ON);
            }
        }

        // W7 — a European number after a left-to-right letter is left-to-right.
        strong = sos;
        for (var i = 0; i < indices.Count; i++) {
            var type = TypeAt(i);

            if (type is BidiClass.L or BidiClass.R) {
                strong = type;
            } else if (type == BidiClass.EN && strong == BidiClass.L) {
                SetType(i, BidiClass.L);
            }
        }
    }

    /// <summary>N0 — paired brackets take the direction of what is inside them.</summary>
    /// <remarks>
    ///     <para>
    ///         The rule that makes <c>(الاسم)</c> render with its brackets the right way round. It is
    ///         also the only rule in the algorithm that looks at <i>pairs</i> rather than at runs,
    ///         and the only one that needs the code points rather than just the types.
    ///     </para>
    ///     <para>
    ///         The canonical equivalences matter: U+2329 and U+3008 are the same bracket written two
    ///         ways, and a document mixing them has to pair them anyway.
    ///     </para>
    /// </remarks>
    void ResolveBrackets() {
        var stack = new List<(int Position, int Expected)>();
        var pairs = new List<(int Open, int Close)>();

        for (var i = 0; i < indices.Count && stack.Count <= 63; i++) {
            if (TypeAt(i) != BidiClass.ON) {
                continue;
            }

            var codePoint = state.CodePointOf(indices[i]);
            if (!BidiBracketTable.TryGet(codePoint, out var paired, out var opens)) {
                continue;
            }

            if (opens) {
                if (stack.Count == 63) {
                    // BD16 — a stack this deep stops the rule rather than growing without bound.
                    return;
                }

                stack.Add((i, Canonical(paired)));
                continue;
            }

            for (var j = stack.Count - 1; j >= 0; j--) {
                if (stack[j].Expected != Canonical(codePoint)) {
                    continue;
                }

                pairs.Add((stack[j].Position, i));
                stack.RemoveRange(j, stack.Count - j);
                break;
            }
        }

        pairs.Sort(static (left, right) => left.Open.CompareTo(right.Open));

        var embedding = level % 2 == 1 ? BidiClass.R : BidiClass.L;
        var opposite = level % 2 == 1 ? BidiClass.L : BidiClass.R;

        foreach (var (open, close) in pairs) {
            var found = BidiClass.Other;

            for (var i = open + 1; i < close; i++) {
                var strong = StrongType(TypeAt(i));

                if (strong == BidiClass.Other) {
                    continue;
                }

                if (strong == embedding) {
                    found = embedding;
                    break;
                }

                found = opposite;
            }

            if (found == BidiClass.Other) {
                // Nothing strong inside: the brackets keep whatever the neutrals around them get.
                continue;
            }

            if (found == embedding) {
                SetBracketPair(open, close, embedding);
                continue;
            }

            // Something strong, but the wrong way. It takes the established direction before the
            // bracket if that agrees, and the embedding direction otherwise.
            var preceding = sos;

            for (var i = open - 1; i >= 0; i--) {
                var strong = StrongType(TypeAt(i));

                if (strong == BidiClass.Other) {
                    continue;
                }

                preceding = strong;
                break;
            }

            SetBracketPair(open, close, preceding == opposite ? opposite : embedding);
        }
    }

    void SetBracketPair(int open, int close, BidiClass direction) {
        SetType(open, direction);
        SetType(close, direction);

        // N0's final clause: the marks that followed either bracket follow it here too, because W1
        // gave them the bracket's old type and the bracket has just changed.
        for (var i = open + 1; i < indices.Count && state.InitialTypeOf(indices[i]) == BidiClass.NSM; i++) {
            SetType(i, direction);
        }

        for (var i = close + 1; i < indices.Count && state.InitialTypeOf(indices[i]) == BidiClass.NSM; i++) {
            SetType(i, direction);
        }
    }

    /// <summary>N1 and N2 — the neutrals between two strong types, and the ones left over.</summary>
    void ResolveNeutrals() {
        for (var i = 0; i < indices.Count; i++) {
            if (!IsNeutral(TypeAt(i))) {
                continue;
            }

            var start = i;
            while (i < indices.Count && IsNeutral(TypeAt(i))) {
                i++;
            }

            var before = start == 0 ? sos : StrongContext(TypeAt(start - 1));
            var after = i == indices.Count ? eos : StrongContext(TypeAt(i));

            // N1 — surrounded by the same direction on both sides, they take it. A number counts as
            // right-to-left here, which is why `12` between two Hebrew words does not break the run.
            var resolved = before == after && before is BidiClass.L or BidiClass.R
                ? before
                : level % 2 == 1 ? BidiClass.R : BidiClass.L;

            for (var j = start; j < i; j++) {
                SetType(j, resolved);
            }

            i--;
        }
    }

    /// <summary>I1 and I2 — types become levels.</summary>
    /// <remarks>
    ///     At an even level, right-to-left text goes up one and numbers go up two, so that a number
    ///     inside a right-to-left run still reads left to right. At an odd level everything that is
    ///     not right-to-left goes up one. Those two lines are the whole of why a phone number in an
    ///     Arabic sentence comes out the right way round.
    /// </remarks>
    void ResolveImplicit() {
        for (var i = 0; i < indices.Count; i++) {
            var type = TypeAt(i);
            var index = indices[i];

            if (level % 2 == 0) {
                state.SetLevel(index, type switch {
                    BidiClass.R => level + 1,
                    BidiClass.AN or BidiClass.EN => level + 2,
                    _ => level
                });
            } else {
                state.SetLevel(index, type == BidiClass.R ? level : level + 1);
            }
        }
    }

    BidiClass TypeAt(int i) => state.TypeOf(indices[i]);

    void SetType(int i, BidiClass value) => state.SetType(indices[i], value);

    /// <summary>The strong direction a type contributes, or <c>Other</c> for none.</summary>
    static BidiClass StrongType(BidiClass type) => type switch {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => BidiClass.Other
    };

    /// <summary>The direction a resolved type counts as when a neutral looks at its neighbours.</summary>
    static BidiClass StrongContext(BidiClass type) => type switch {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => type
    };

    static bool IsNeutral(BidiClass type) =>
        type is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON
            or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI;

    static BidiClass BoundaryType(int level) => level % 2 == 1 ? BidiClass.R : BidiClass.L;

    /// <summary>Canonical equivalence for the two brackets Unicode spells twice.</summary>
    static int Canonical(int codePoint) => codePoint switch {
        0x3008 => 0x2329,
        0x3009 => 0x232A,
        _ => codePoint
    };

    int PreviousLevel(int from, int paragraphLevel) {
        for (var i = from - 1; i >= 0; i--) {
            if (IsRemoved(i)) {
                continue;
            }

            return state.ExplicitLevelOf(i);
        }

        return paragraphLevel;
    }

    int NextLevel(int from, int paragraphLevel) {
        for (var i = from + 1; i < state.Levels.Length; i++) {
            if (IsRemoved(i)) {
                continue;
            }

            return state.ExplicitLevelOf(i);
        }

        return paragraphLevel;
    }

    bool IsRemoved(int index) =>
        state.InitialTypeOf(index) is BidiClass.RLE or BidiClass.LRE or BidiClass.RLO
            or BidiClass.LRO or BidiClass.PDF or BidiClass.BN;
}
