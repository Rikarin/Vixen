// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Net.Engine;

namespace Vixen.Samples.Multiplayer;

/// <summary>A fighter's run of kills, and who they were.</summary>
/// <remarks>
///     <para>
///         <b>The other authoring style, in the one sample that replicates anything.</b> Everything
///         else here is a <c>[Replicated]</c> struct swept out of the world by the generated
///         replicator — <see cref="Combatant" />, <see cref="Vitals" />, <c>NetworkTransform</c>.
///         This is the same job done the way a behaviour does it: fields declared once in a
///         constructor, written from ordinary game code, and never marked dirty by hand.
///     </para>
///     <para>
///         ⚠ <b>It is here because until now nothing outside a test authored against this style at
///         all.</b> <c>SyncVar</c>, <c>SyncList</c> and the sweep that marks them were exercised only
///         by <c>Vixen.Net.Engine.Tests</c>, which is a claim that the machinery works and not that
///         it is usable — and the two come apart exactly where this sample found them to: a sample
///         that replicates has to reference <c>Vixen.Net.Engine</c> and therefore <c>Vixen.Engine</c>,
///         and none did, so nothing in the tree had both a behaviour loop and a session.
///     </para>
///     <para>
///         <b>Why these two values and not health or a position.</b> A <c>[Replicated]</c> struct is
///         a fixed layout compared whole against a baseline, so it can carry a score and cannot carry
///         a list — <see cref="Victims" /> would be an array in a chunk, which a component may not
///         be. The killfeed is the thing this style does that the other cannot, and the streak is
///         beside it to show a plain field costing what a field costs.
///     </para>
///     <para>
///         <b>Nothing here calls <c>MarkChanged</c>.</b> That is the whole point of the style and the
///         reason <c>SyncStateSweepSystem</c> exists: the dirt sits in these managed fields until the
///         sweep walks them once at the end of the frame, so a method that sets three of them touches
///         the entity's version component once rather than three times. A game that had to remember
///         the call would eventually forget it, and forgetting it fails silently — the state stays on
///         the server for ever and nothing says so.
///     </para>
/// </remarks>
internal sealed class FighterScore : NetworkBehaviour {
    /// <summary>The fields, as one module. A behaviour's root module is its wire layout.</summary>
    public sealed class Sheet : NetworkModule {
        /// <summary>Kills since this fighter last died.</summary>
        public SyncVar<int> Streak { get; }

        /// <summary>The longest streak it has managed this match.</summary>
        public SyncVar<int> Best { get; }

        /// <summary>Declares the layout. Both ends walk it in this order and never exchange it.</summary>
        public Sheet() {
            Streak = Declare(new SyncVar<int>(0), nameof(Streak));
            Best = Declare(new SyncVar<int>(0), nameof(Best));
        }
    }

    /// <summary>The network ids this fighter has finished off, oldest first.</summary>
    /// <remarks>
    ///     A list rather than a count, because a killfeed is the thing a fixed-layout component
    ///     cannot be. It is sent as the operations that changed it — an append is an opcode, an index
    ///     and a value — so a match-long feed costs what the kills cost rather than its own length
    ///     every tick.
    /// </remarks>
    public SyncList<uint> Victims { get; }

    readonly Sheet sheet = new();

    /// <summary>
    ///     Declares the list. In the constructor, because the order lists are declared in <i>is</i>
    ///     the wire format — see <see cref="NetworkBehaviour.Lists" />.
    /// </summary>
    public FighterScore() => Victims = DeclareList(new SyncList<uint>(), nameof(Victims));

    /// <summary>The sheet, for a client that wants to read it.</summary>
    /// <remarks>
    ///     Named for the fields rather than "State", which is the base class's name for the same
    ///     object typed as a <see cref="NetworkModule" />. This one is typed as the sheet, so a
    ///     reader gets <see cref="Sheet.Streak" /> rather than a list of <c>ISyncField</c>.
    /// </remarks>
    public Sheet Fields => sheet;

    /// <inheritdoc />
    protected override NetworkModule Build() => sheet;

    /// <summary>Records a kill. Called by <see cref="Arena" />, which is the only thing that decides one.</summary>
    /// <param name="victim">The <c>NetworkId</c> of whoever was finished off.</param>
    public void Record(uint victim) {
        if (!IsServer) {
            return;
        }

        Victims.Add(victim);
        sheet.Streak.Value++;

        if (sheet.Streak.Value > sheet.Best.Value) {
            sheet.Best.Value = sheet.Streak.Value;
        }
    }

    /// <summary>
    ///     Ends a streak when its owner dies, from ordinary behaviour code reading ordinary
    ///     components.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Deliberately a read of <see cref="Vitals" /> rather than a call from
    ///         <see cref="Arena" />.</b> The arena already knows who died and could say so, which is
    ///         what the rest of this sample does — this is the one place that shows the shape a
    ///         behaviour is <i>for</i>: state the game wrote somewhere else, noticed here, turned into
    ///         state the wire carries, with no wiring between the two.
    ///     </para>
    ///     <para>
    ///         Server only. A client runs this behaviour too — that is how it holds the state the
    ///         snapshot applies — and a client zeroing its own streak would be a value overwritten by
    ///         the next snapshot and briefly wrong in between.
    ///     </para>
    /// </remarks>
    protected override void Update() {
        if (!IsServer || !Has<Vitals>()) {
            return;
        }

        if (Read<Vitals>().Health == 0) {
            sheet.Streak.Value = 0;
        }
    }
}
