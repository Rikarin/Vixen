// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Fuzz;

/// <summary>How one target's inputs are made, when bytes are the wrong thing to mutate.</summary>
/// <remarks>
///     <para>
///         <b>The byte mutator is the right tool for a decoder and the wrong one for a compiler.</b>
///         <see cref="Mutator" />'s havoc is aimed at length prefixes and varints, and it is very good
///         at those; pointed at a language it spends effectively all of its budget on text that does
///         not lex. A shader that fails at its first token exercises the tokeniser and nothing behind
///         it, so the binder, the type checker and the backend — which is where a compiler's
///         interesting defects are — are never reached at all.
///     </para>
///     <para>
///         <b>What changes is only how the next input is produced.</b> Everything else is untouched
///         and deliberately so: <see cref="IFuzzTarget.Run" /> still takes a
///         <c>ReadOnlySpan&lt;byte&gt;</c>, <see cref="FuzzSession" /> still measures allocation, time
///         and retention around that one call, the corpus is still bytes on disk, and a
///         <see cref="FuzzFinding" /> still carries the exact bytes that broke something. <b>The four
///         oracles are statements about behaviour, not about input shape</b>, and a seam that made
///         them care what an input <i>is</i> would have thrown away the only reason to grow this
///         harness rather than adopt <c>SharpFuzz</c>.
///     </para>
///     <para>
///         Implemented as <see cref="FuzzDomain{T}" /> in practice. This interface is what the session
///         sees, and it is byte-in byte-out precisely so that the session learns nothing about trees.
///     </para>
/// </remarks>
public interface IFuzzDomain {
    /// <summary>What is being mutated, in one line, for a report.</summary>
    string What { get; }

    /// <summary>Produces a mutant of an input, possibly drawing on another.</summary>
    /// <param name="input">A corpus entry.</param>
    /// <param name="other">Another one, to splice or graft from. May be the same.</param>
    /// <param name="random">Where the choices come from.</param>
    /// <returns>The mutant, as bytes.</returns>
    byte[] Mutate(ReadOnlySpan<byte> input, ReadOnlySpan<byte> other, FuzzRandom random);

    /// <summary>Produces an input from nothing.</summary>
    /// <param name="random">Where the choices come from.</param>
    /// <returns>It, as bytes.</returns>
    byte[] Fresh(FuzzRandom random);
}

/// <summary>A domain whose inputs are values of some type rather than byte strings.</summary>
/// <typeparam name="T">What an input is — a syntax tree, usually.</typeparam>
/// <remarks>
///     <para>
///         Three operations define a domain: read a corpus entry into one of these, change one, and
///         write one back out. This class does the sandwich — read, mutate, write — so that a target
///         author writes only the three, and so that the two decisions below are made once rather
///         than per grammar.
///     </para>
///     <para>
///         ⚠ <b>The corpus stays bytes, and for a language that costs nothing.</b> A tree's
///         serialization is its source text, which is what a corpus file should contain anyway: it can
///         be read in a diff, committed as a regression, and handed to the real compiler by somebody
///         reproducing a finding. A corpus of serialized trees would be none of those things. The
///         price is a parse per case on the way in and another inside <see cref="IFuzzTarget.Run" />,
///         which is real and is worth it.
///     </para>
///     <para>
///         ⚠ <b>Garbage is still generated, and leaving it out is the mistake this design is most
///         likely to make.</b> A tree mutator only ever emits text the printer produced, so the
///         lexer's error paths — an unterminated string, a stray byte, a nesting depth that runs the
///         parser out of stack — stop being reached the moment structure-aware generation replaces
///         byte havoc rather than joining it. A grammar-aware fuzzer that never sends garbage has
///         quietly stopped fuzzing the front end. So one mutation in <see cref="GarbageIn" /> is byte
///         havoc over the serialized form, and the two run against the same corpus.
///     </para>
/// </remarks>
public abstract class FuzzDomain<T> : IFuzzDomain {
    /// <summary>One mutation in this many is byte havoc rather than a change to the tree.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The number is a split of the budget between two failure populations.</b> Low, because
    ///         the byte targets already establish that a front end survives noise and because the
    ///         reason to build this seam at all was that noise does not get past one. High enough that
    ///         the lexer keeps being visited by inputs no printer would emit, which is the population
    ///         the tree mutator cannot reach by construction.
    ///     </para>
    ///     <para>
    ///         It is also what keeps a *committed regression* useful. A crasher found by byte havoc is
    ///         usually not a tree, so it fails <see cref="TryRead" /> — and without this it would be
    ///         replayed once at start-up and never mutated again.
    ///     </para>
    /// </remarks>
    public const int GarbageIn = 8;

    /// <summary>The largest input this will produce.</summary>
    /// <remarks>
    ///     ⚠ <b>Much larger than <see cref="Mutator.MaxLength" />, because that number is a
    ///     transport's payload cap and this is a source file.</b> A shader nobody would call large is
    ///     already past 1400 bytes, so capping a grammar domain there would mean fuzzing only
    ///     fragments. It is capped all the same: a tree mutation that duplicates a subtree is a
    ///     doubling, and a few hundred of those unchecked is an input the size of memory and a run
    ///     whose cases each take a second.
    /// </remarks>
    public const int MaxLength = 64 * 1024;

    readonly Mutator havoc;

    /// <summary>Creates a domain.</summary>
    /// <param name="seed">What to seed the byte-havoc half with.</param>
    protected FuzzDomain(ulong seed) => havoc = new(seed);

    /// <inheritdoc />
    public abstract string What { get; }

    /// <summary>Reads a corpus entry.</summary>
    /// <param name="bytes">The entry.</param>
    /// <param name="value">What it says.</param>
    /// <returns>Whether it could be read at all.</returns>
    /// <remarks>
    ///     Allowed to fail, and failing is not an error: the corpus holds committed regressions and
    ///     the empty input, and a front end that refuses one of those has done its job. Such an entry
    ///     is mutated as bytes instead, which is both the honest fallback and the only way a crasher
    ///     found by havoc stays reachable.
    /// </remarks>
    protected abstract bool TryRead(ReadOnlySpan<byte> bytes, out T value);

    /// <summary>Writes one back out.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Its bytes — its source text, for a grammar.</returns>
    protected abstract byte[] Write(T value);

    /// <summary>Changes one.</summary>
    /// <param name="value">What to change.</param>
    /// <param name="other">Something else from the corpus, to graft from. May be the same.</param>
    /// <param name="random">Where the choices come from.</param>
    /// <returns>The changed value.</returns>
    /// <remarks>
    ///     The whole point of the seam. A grammar's version of havoc is: replace a subtree with
    ///     another of the same kind, duplicate one, delete an optional one, graft one in from the
    ///     other input, swap an operator or a keyword for one the grammar also allows there. Each
    ///     produces something that lexes and mostly parses, and therefore something that reaches the
    ///     passes behind the front end.
    /// </remarks>
    protected abstract T Mutate(T value, T other, FuzzRandom random);

    /// <summary>Produces a value from nothing.</summary>
    /// <param name="random">Where the choices come from.</param>
    /// <returns>It.</returns>
    protected abstract T Create(FuzzRandom random);

    /// <inheritdoc />
    public byte[] Mutate(ReadOnlySpan<byte> input, ReadOnlySpan<byte> other, FuzzRandom random) {
        ArgumentNullException.ThrowIfNull(random);

        if (random.Below(GarbageIn) == 0 || !TryRead(input, out var value)) {
            return havoc.Mutate(input.ToArray(), other.ToArray());
        }

        // A graft needs somewhere to graft from, and an entry that does not read is not it. Mutating
        // against itself is what the byte mutator does when the corpus holds one thing, and it is the
        // same answer here.
        if (!TryRead(other, out var second)) {
            second = value;
        }

        return Cap(Write(Mutate(value, second, random)));
    }

    /// <inheritdoc />
    public byte[] Fresh(FuzzRandom random) {
        ArgumentNullException.ThrowIfNull(random);

        return random.Below(GarbageIn) == 0 ? havoc.Fresh() : Cap(Write(Create(random)));
    }

    static byte[] Cap(byte[] bytes) => bytes.Length <= MaxLength ? bytes : bytes.AsSpan(0, MaxLength).ToArray();
}
