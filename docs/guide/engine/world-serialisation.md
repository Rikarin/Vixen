---
title: Saving and restoring a world
slug: engine/world-serialisation
kind: guide
area: Engine
summary: Writing every entity in a world to bytes, and making the world again from them.
api: [T:Vixen.Engine.Worlds.WorldSerializer, T:Vixen.Engine.Worlds.WorldContent]
tags: [ecs, serialization, scenes, save]
since: 0.1
status: stable
related: [ecs/queries]
---

## What it is

`WorldSerializer` writes every live entity in a `World` — what each one carries and where it hangs in
the hierarchy — into a `WorldContent`, and makes the world again from one. `WorldContent` is the
written-down form: a table of parents, and the entities grouped into blocks that share an archetype
with one column of bytes per component.

## What it is for

A save game, a determinism checkpoint, and a bug report that somebody else can open are all the same
operation: turn the world into bytes that mean the same thing in another process.

You do not want this for play mode in the editor, which copies chunk rows between two live worlds and
is much faster because it never asks what a component *means*. You do not want it for shipping a
level either — that is a compiled scene, authored as `.vxscene` and built into content, and it is
authored rather than captured.

A component is written only if it carries `[DataContract]`, because that is what gives it a name and
a serializer. Anything else — a physics body holding a native handle, say — is named in
`WorldContent.Dropped` rather than dropped in silence, so a caller that cannot afford to lose
anything can check `IsComplete` and refuse.

## Using it

Capture, write the bytes wherever they go, and restore into a world later. Restoring clears the
target first: it is a restore and not a merge.

```csharp compile
using Vixen.Core.Serialization;
using Vixen.Ecs;
using Vixen.Engine.Worlds;

public static class SaveGame {
    public static byte[] Save(World world) {
        var content = WorldSerializer.Capture(world);

        if (!content.IsComplete) {
            throw new InvalidOperationException(
                $"These components cannot be saved: {string.Join(", ", content.Dropped)}."
            );
        }

        return Serializer.ToBytes(content);
    }

    public static void Load(World world, byte[] saved) =>
        WorldSerializer.Restore(Serializer.Read<WorldContent>(saved), world);
}
```

`Restore` returns the entity it made for each of the content's indices, and `Capture` will fill a
list with the entity it read at each index if you pass one. Zipping the two is how anything holding
handles across the round trip translates them — a selection, a target, a component of your own that
stores an `Entity`.

## Examples

Handles are not written down, so a component holding one needs translating on the way back. The
hierarchy is already handled; a component of your own is not:

```csharp compile
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Worlds;

[Component]
[DataContract("GuideFollowTarget")]
public struct FollowTarget {
    public Entity Value;
}

public static class Reload {
    public static void Round(World world) {
        List<Entity> before = [];
        var content = WorldSerializer.Capture(world, before);
        var after = WorldSerializer.Restore(content, world);

        Dictionary<Entity, Entity> translation = [];

        for (var index = 0; index < before.Count; index++) {
            translation[before[index]] = after[index];
        }

        foreach (var entity in after) {
            if (world.Has<FollowTarget>(entity)) {
                ref var follow = ref world.Get<FollowTarget>(entity);

                follow.Value = translation.TryGetValue(follow.Value, out var now) ? now : Entity.Null;
            }
        }
    }
}
```

## See also

- [Entity queries](ecs/queries) — how the data a capture writes is read in a frame.
