// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Fuzz;

/// <summary>One decoder, wrapped so that arbitrary bytes can be pushed into it.</summary>
/// <remarks>
///     <para>
///         <b>A target is a place where bytes we did not write meet code that believes things.</b>
///         Every one of these corresponds to a real receive path: the packet reader under the
///         session, the bit reader under replication, the handshake a connection performs before it
///         is anybody, the remote call a client is allowed to make. What they have in common is the
///         contract in <c>PacketReader</c>'s remarks — that a malformed input is a refused message
///         rather than an exception out of a decoder — and this interface is how that contract is
///         tested rather than asserted.
///     </para>
///     <para>
///         <b>The signature is what makes this more than a random-bytes loop.</b> There is no
///         instrumentation here and therefore no edge coverage; what a target returns instead is a
///         cheap number summarising how the decode went — which reads succeeded, which counter
///         moved, where it stopped. An input that produces a signature no earlier input produced is
///         kept, and the mutator works from what was kept. That is a weaker signal than libFuzzer's
///         and it is deliberately not called coverage, but it is enough to walk a decoder into its
///         branches, which uniform random bytes will not do: the odds of thirty-two random bits
///         being a tick a snapshot will accept are what they sound like.
///     </para>
/// </remarks>
public interface IFuzzTarget {
    /// <summary>What to call it on a command line and in a report.</summary>
    string Name { get; }

    /// <summary>Which receive path this is, in one line.</summary>
    string What { get; }

    /// <summary>How its inputs are made, or null for byte havoc.</summary>
    /// <remarks>
    ///     <para>
    ///         Null for a decoder, which is what <see cref="Mutator" /> was built for and is most of
    ///         these. A target whose input is a <i>language</i> declares a domain instead, because
    ///         havoc over the bytes of a source file produces text that does not lex and therefore
    ///         never reaches anything behind the tokeniser. See <see cref="IFuzzDomain" /> for the
    ///         argument and for why the oracles above did not have to change to allow it.
    ///     </para>
    ///     <para>
    ///         It changes what arrives at <see cref="Run" /> and nothing else. A target still receives
    ///         bytes, and is still expected to treat them as bytes it did not write — a domain makes
    ///         grammatical input <i>more likely</i>, and promises nothing at all about any single case.
    ///     </para>
    /// </remarks>
    IFuzzDomain? Domain => null;

    /// <summary>Whether a behaviour nothing produced before is worth keeping the input for.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>True for a decoder and false for a compiler, and the difference is the size of the
    ///         behaviour space rather than a preference.</b> A packet reader has a few dozen outcomes,
    ///         so "this input did something new" is a strong signal and a corpus selected on it is a
    ///         set of representatives. A compiler has a behaviour for every combination of
    ///         declarations, types and diagnostics there is; nearly every input looks new, the
    ///         signature table saturates within seconds, and what it selected before saturating was
    ///         whatever the first few thousand cases happened to be.
    ///     </para>
    ///     <para>
    ///         Declaring false is <b>accepting unguided but structured generation</b>, which is a real
    ///         position and not a shortfall: the guidance existed to walk a decoder into branches that
    ///         random bytes never reach, and a grammar-aware domain reaches them by construction
    ///         instead. What it buys in exchange is stated at <see cref="Corpus.Sample" /> — the pool
    ///         keeps refreshing past saturation rather than freezing, which is what happens today and
    ///         is the half of it that was actually wrong.
    ///     </para>
    /// </remarks>
    bool NoveltyGuides => true;

    /// <summary>Adds well-formed inputs for the mutator to start from.</summary>
    /// <param name="corpus">Where to put them.</param>
    /// <remarks>
    ///     Produced by the real encoders rather than written out by hand, so a change to a wire
    ///     format changes the seeds with it and the corpus cannot quietly go stale. A fuzzer seeded
    ///     with nothing spends its budget discovering that the first four bytes are a tick.
    /// </remarks>
    void Seed(ICollection<byte[]> corpus);

    /// <summary>Puts the target back into a state worth fuzzing, before the next case.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Called outside the measurement, which is the whole reason it exists.</b> A stateful
    ///         target accumulates — a client that has been fed ten thousand snapshots holds ten
    ///         thousand entities, a session whose player was disconnected by the last input has
    ///         nobody left to talk to — and the housekeeping that fixes that allocates. Charging it
    ///         to whichever input happened to trigger it would report the harness as an amplifying
    ///         packet, which is a false finding, and raising the allowance until it fits would hide
    ///         the true ones.
    ///     </para>
    ///     <para>
    ///         Doing nothing is the right answer for a target with no state, which is most of them.
    ///     </para>
    /// </remarks>
    void Maintain() { }

    /// <summary>Pushes one input through the decoder.</summary>
    /// <param name="input">The bytes, which are not to be believed.</param>
    /// <returns>A signature summarising what the decoder did with <i>this</i> input.</returns>
    /// <remarks>
    ///     <para>
    ///         Must not throw, must not allocate more than <see cref="AllowanceFor" />, and must not
    ///         take long. Those three are the whole of what is being tested; a target that catches
    ///         its own exceptions is a target that proves nothing.
    ///     </para>
    ///     <para>
    ///         <b>The signature is about the case, not about the run, and getting that wrong is
    ///         silent.</b> A decoder's counters are lifetime totals, so a signature folded from them
    ///         strictly increases and <i>every</i> case looks like a behaviour never seen before —
    ///         which is not a fuzzer with excellent coverage, it is a fuzzer with no guidance at all
    ///         and a corpus that keeps everything. One target here kept a million inputs in a second
    ///         before this was noticed. Fold the <i>change</i> in each counter, or state that is
    ///         genuinely bounded; never a running total.
    ///     </para>
    /// </remarks>
    long Run(ReadOnlySpan<byte> input);

    /// <summary>How many bytes an input of a given length may cause to be allocated.</summary>
    /// <param name="inputLength">How big the input was.</param>
    /// <returns>The allowance, in bytes.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A multiple of the input, not a constant.</b> The property worth defending is that a
    ///         packet cannot be an amplifier — that a hundred bytes cannot cost a megabyte — and that
    ///         is a statement about the ratio. A flat cap would either be too small for the targets
    ///         that legitimately build a list, or so large that the interesting case fits under it.
    ///     </para>
    ///     <para>
    ///         The default is deliberately loose. A target that has a tighter bound should say so
    ///         and say why, because the number is the claim.
    ///     </para>
    /// </remarks>
    long AllowanceFor(int inputLength) => 4096 + (32L * inputLength);

    /// <summary>How many things the decoder is currently holding on to.</summary>
    /// <remarks>
    ///     Players, entities, list items — whatever this decoder accumulates as packets arrive. Zero
    ///     for one that accumulates nothing, which is most of them and is the default.
    /// </remarks>
    long Held => 0;

    /// <summary>How many it may hold before that is the finding.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The question an allocation budget cannot ask.</b> A packet that costs a hundred
    ///         bytes is proportionate and passes every ratio; a hundred bytes that are never given
    ///         back is a server that dies on the second day. The two failures need two oracles, and
    ///         this is the second one.
    ///     </para>
    ///     <para>
    ///         Unbounded by default, which is the honest reading of a target that has not thought
    ///         about it: no cap means no claim. A target that declares one is asserting that
    ///         <see cref="Maintain" /> plus the decoder's own bounds keep it there, and a breach
    ///         means one of the two is not doing its job.
    ///     </para>
    /// </remarks>
    long HeldCap => long.MaxValue;
}
