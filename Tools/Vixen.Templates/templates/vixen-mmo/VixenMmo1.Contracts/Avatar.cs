using Vixen.Net.Replication;

namespace VixenMmo1.Contracts;

// What crosses the wire, and the only assembly all four processes agree about.
//
// Replication is a delta PER COMPONENT: an entity's component is compared with what a connection
// has acknowledged, and either the whole of it is sent or none of it. So what changes together
// belongs together, and what never changes belongs on its own — the identity below is sent once,
// and the pose is sent every tick.

/// <summary>Who an avatar belongs to. Sent once, so it is not in the same struct as the pose.</summary>
[Replicated(Priority = 40)]
public struct Avatar {
    /// <summary>The session's number for the player, not their account. See PlayerKey for that.</summary>
    public uint Owner;
}

/// <summary>What changes every tick.</summary>
[Replicated(Priority = 20)]
public struct Pose {
    /// <summary>Where they are, in metres.</summary>
    public float X;

    /// <summary>Where they are, in metres.</summary>
    public float Y;

    /// <summary>Where they are, in metres.</summary>
    public float Z;

    /// <summary>Which way they are facing, in radians.</summary>
    public float Facing;
}
