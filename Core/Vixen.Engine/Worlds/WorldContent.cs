// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Scenes;

namespace Vixen.Engine.Worlds;

/// <summary>A whole world in bytes: every live entity, what it carries, and where it hangs.</summary>
/// <remarks>
///     <para>
///         <b>The on-disk sibling of <c>WorldSnapshot</c>, and the difference between them is the
///         whole reason this waited for the scene format.</b> A snapshot copies chunk rows into
///         another world — raw bytes, one memcpy an archetype, no knowledge of what a component
///         <i>is</i> — which is fast, is what makes in-process play mode possible, and is worth
///         nothing outside the process that took it. Bytes with a field order in them are not a
///         format. Naming each component by its <c>[DataContract]</c> alias and writing it through
///         its generated serializer is what makes a world something a save file, a determinism
///         checkpoint or a bug report can hold.
///     </para>
///     <para>
///         <b>It reuses <see cref="SceneBlock" /> and <see cref="SceneColumn" /> rather than
///         declaring a second pair of the same thing.</b> A compiled scene and a captured world
///         differ in what they contain and not in how a run of entities that share an archetype is
///         written down — archetype-major, one column per component, the entity order implied by the
///         block. Two shapes would be two things to keep in step.
///     </para>
///     <para>
///         ⚠ <b>Entity handles are not stored, and this is the one design decision everything else
///         here follows from.</b> <see cref="Entity" /> is a slot index, a generation and a world id;
///         all three are properties of a running process, and <c>Entity</c>'s own remarks say a saved
///         one means nothing when loaded back. So the hierarchy travels as <see cref="Parents" /> —
///         an <i>index</i> into this content's own order — exactly as a compiled scene's does, and
///         <c>Parent</c>, <c>Child</c> and <c>Sibling</c> are never written at all: they are rebuilt
///         by the link pass from that table. A game component holding an <c>Entity</c> is a different
///         problem and is not solved here; see <c>WorldSerializer.Capture</c>, which reports it.
///     </para>
/// </remarks>
[DataContract("WorldContent")]
public sealed class WorldContent {
    /// <summary>How many entities it holds.</summary>
    public int Count { get; set; }

    /// <summary>Each entity's parent, as an index into this content's order, or <c>-1</c> for a root.</summary>
    /// <remarks>
    ///     Ordered so a parent is always before anything that hangs from it, which is what makes the
    ///     restore a single forward pass with no map. An entity in no hierarchy at all is a root by
    ///     the same rule that a root is: it has no <c>Parent</c>.
    /// </remarks>
    public int[] Parents { get; set; } = [];

    /// <summary>Its entities, grouped by what they carry.</summary>
    public SceneBlock[] Blocks { get; set; } = [];

    /// <summary>
    ///     The components that were on entities in the captured world and are not in here, by CLR type
    ///     name, sorted and without repeats.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Reported rather than refused, and reported rather than dropped in silence — the
    ///         two things a capture could otherwise do, both of which are worse.</b> Refusing would
    ///         make capturing a world impossible for anybody holding one runtime-only component, and
    ///         a <c>PhysicsBody</c> is a native handle that cannot be written down by construction.
    ///         Dropping silently would make a save game that loses an entity's <c>Health</c> look
    ///         exactly like one that did not.
    ///     </para>
    ///     <para>
    ///         So the content carries the list and a caller decides. A save-game path asserts it is
    ///         empty; a determinism checkpoint compares two of them and does not care what is in
    ///         either, as long as both say the same thing.
    ///     </para>
    /// </remarks>
    public string[] Dropped { get; set; } = [];

    /// <summary>Whether everything on every captured entity is in here.</summary>
    public bool IsComplete => Dropped.Length == 0;

    /// <summary>Checks the tables agree with each other before anything is created.</summary>
    /// <exception cref="ArgumentException">They do not.</exception>
    /// <remarks>
    ///     ⚠ <b>Checked rather than trusted, because this data came off a disk.</b> The same argument
    ///     <c>SceneContent.Validate</c> makes: a truncated download and a chunk from a future format
    ///     both arrive here as arrays of the wrong length, and the failure without this is an
    ///     <c>IndexOutOfRangeException</c> from inside the restore with nothing in it that names what
    ///     was being read.
    /// </remarks>
    public void Validate() {
        if (Count < 0) {
            throw new ArgumentException($"A captured world cannot hold {Count} entities.");
        }

        if (Parents.Length != Count) {
            throw new ArgumentException(
                $"This world has {Count} entities and {Parents.Length} parents. The data is truncated or was "
                + "written by something that does not agree with this format."
            );
        }

        var seen = new bool[Count];

        foreach (var block in Blocks) {
            foreach (var entity in block.Entities) {
                if ((uint) entity >= (uint) Count) {
                    throw new ArgumentException(
                        $"A block names entity {entity} and the world has {Count}."
                    );
                }

                if (seen[entity]) {
                    throw new ArgumentException(
                        $"Entity {entity} is in two blocks. A block is an archetype, so an entity is in exactly "
                        + "one of them."
                    );
                }

                seen[entity] = true;
            }
        }

        for (var index = 0; index < Count; index++) {
            if (!seen[index]) {
                throw new ArgumentException(
                    $"Entity {index} is in no block, so nothing would create it. An entity carrying nothing at "
                    + "all is still in a block — the empty one."
                );
            }

            var parent = Parents[index];

            if (parent >= index) {
                throw new ArgumentException(
                    $"Entity {index}'s parent is {parent}, which is not before it. A captured world is ordered "
                    + "so that a parent exists before anything that hangs from it."
                );
            }

            if (parent < -1) {
                throw new ArgumentException($"Entity {index}'s parent is {parent}. A root's is -1.");
            }
        }
    }
}
