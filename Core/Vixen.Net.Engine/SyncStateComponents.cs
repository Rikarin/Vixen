// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Net.Engine;

/// <summary>
///     Marks an entity as having behaviour-held networked state, and is what the change versions
///     watch.
/// </summary>
/// <remarks>
///     <para>
///         The join between a <see cref="SyncVar{T}" /> and the ECS's per-chunk change versions, which
///         is the thing that makes replication cost nothing for an object that did not change. A
///         behaviour's state lives in managed fields the ECS cannot see, so setting one has to touch
///         <i>something</i> in a chunk for the capture to notice — and this is that something.
///     </para>
///     <para>
///         A counter rather than a flag, because a flag would have to be cleared and clearing it is
///         another write. Nothing reads the number; the write is the whole point.
///     </para>
/// </remarks>
[Component]
public struct SyncStateVersion {
    /// <summary>Counts up whenever any of this entity's behaviour state changed.</summary>
    public uint Value;
}

/// <summary>
///     Marks an entity as having behaviour-held lists, and is what the change versions watch.
/// </summary>
/// <remarks>
///     Separate from <see cref="SyncStateVersion" /> so that a list changing does not re-send a score
///     and a score changing does not re-send a list. The two travel as different records for the same
///     reason they are different components: the delta encoder rewards a small unit of change.
/// </remarks>
[Component]
public struct SyncListVersion {
    /// <summary>Counts up whenever any of this entity's lists changed.</summary>
    public uint Value;
}
