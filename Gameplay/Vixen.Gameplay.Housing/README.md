# Vixen.Gameplay.Housing

Plots, decoration placement, permission tiers and visitor access.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Exploration, housing, collections,
part of **G8**.

## State

**Built: plots and furniture with a budget, a snap both ends share, placement with surfaces and
requirements, a five-rung permission ladder with bans, and save-and-restore. 38 tests.**

| | |
|---|---|
| `PlotDefinition` · `FurnitureDefinition` · `HouseSurface` | What a designer authors. |
| `Plot` · `Furniture` · `HousingLibrary` | Compiled once, with a `Problems` list. |
| `HousePlot` · `Placement` | One house. |
| `HouseOwner` · `HouseTier` · `HouseAction` · `HousingRefusal` | Whose it is and who may do what. |
| `HousingModule` | Two definition types and one tag root. |

## The property the whole feature rests on: nothing here has a clock

Doc 28 says housing is affordable because **"ten thousand houses are ten thousand rows, not ten
thousand processes"** — which is a claim about hibernation, and hibernation only works if a plot has
nothing that must keep running.

⚠ **No method in this library takes a `now`.** There is no timer, no decay, no growth and no tick;
every question a plot answers is a function of what is stored. Anything that ages — a plant that
wilts, a rent clock — belongs to the caller and is a timestamp compared on load, not a process.

That is also why change is a **`Revision` counter** rather than an event. A subscription is a live
object and a hibernating plot has nowhere to keep one; a counter says "this differs from what was
saved" and doubles as the version an optimistic write checks.

## The five things worth knowing before reading the code

### The snap is content, and both ends call it

The authority table gives the client the placement preview and the realm the validity check. If they
round differently, every placement a player makes comes back corrected by a centimetre, for ever. So
`Plot.Snap` and `Plot.SnapYaw` are on the compiled plot, not in a client's input code and not in the
editor's gizmo — the manipulation grammar is [doc 24](../../docs/plan/24-blockout-tools.md)'s, but the
rounding is content's.

⚠ **Snapped before the checks and stored snapped.** A plot that validated the raw point and stored
the rounded one is a house whose furniture drifts a few centimetres every time somebody logs in.

⚠ **A placement is a position and a yaw, not a transform.** Furniture turns about the up axis and
nothing else, which is what every housing feature that shipped does: free three-axis rotation is
unauthorable with a mouse and doubles a row that ten thousand houses each have hundreds of.

### Surfaces are declared, never measured

This library does not know whether the point somebody picked is on a wall — that is a scene question
with a collision mesh behind it, and it is the same boundary `PvpMatch.Occupy` and `Leash.Check` sit
on. The caller says which surface it found; what is checked here is whether the furniture and the plot
both allow that *kind*.

⚠ **Exactly one surface per placement.** A caller that passes two is guessing, and a guess here is a
chandelier on the lawn.

### A ban is not the bottom of the ladder

⚠ **`HouseTier` has no `Banned` rung and must not grow one.** A ban expressed as the bottom rung does
nothing to a house whose owner has opened it to the public, because there everybody is on the bottom
rung and the bottom rung is admitted. The ban set beats the ladder outright.

⚠ **An owner cannot be banned from, or demoted in, their own house**, and **nobody may grant standing
at or above their own**. The first stops a mis-click locking somebody out of a house only a support
ticket can reopen; the second stops a resident promoting a friend to owner and the house having two
owners, one of whom can evict the other.

A ladder is right here where a tag is right for a guild: a guild has dozens of orthogonal permissions
and as many ranks as a leader invents, so `Vixen.Gameplay.Social` makes a permission a tag. A house
has five relationships and four verbs, and "is my friend allowed to decorate" is one integer
comparison.

### Guild housing is the same type, and it needs one extra door

Doc 28: *"the same thing with an `IGuildGrain` owner and a permission matrix instead of a single
owner"*. A `HouseOwner` is a player **or** a `Guid` — an opaque key rather than a `GuildId`, because
the spine forbids Housing → Social and a game converts at the edge.

⚠ **`Grant` cannot bootstrap a guild hall**, because a guild plot has no implicit owner and so nobody
outranks anybody. What seats standing there is `Assign`, which is unchecked and belongs to the
authority rather than to a player: the guild's rank matrix is applied wholesale by whoever holds the
guild and arrives here already resolved. `Bar` and `Open` are its siblings, and all three are also
what loads a save.

### A save loads as it was, even when a patch has made it illegal

⚠ **`Restore` is deliberately not `Place` in a loop.** A layout that was legal when it was made must
load after a patch lowers the budget or adds a requirement, or a content change silently deletes
people's houses. `Free` goes negative and says so; reconciling — hide the overflow, refuse new
placements, ask the owner — is a decision this library may not make for a game.

## What the tests found

**The tag revoke that leaks.** The obvious rule — "take the furniture's tag back only when the last
one comes up" — is wrong, because `GameplayTagSet` is already a counted set. Two forges hold the tag
twice, so revoking once for two grants leaks it for the rest of the session. One grant out for one
grant in; the counting is the kernel's.

## What is owed

- **Collision, ground and bounds.** Everything geometric is the scene's, and a game that wants
  "furniture may not intersect" implements it beside the placement call.
- **Storage, mailboxes and portals in a house** — a furniture tag and somebody else's container.
- **Durability**, on the same terms as everything else: task **#27**'s bridge, where a plot becomes
  doc 27's `Persistent` shard.
- **The editor side.** Doc 28 asks for placement to reuse doc 24's in-viewport manipulation grammar;
  the snap is here and the gizmo is not wired to it.
