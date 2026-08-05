# Vixen.Gameplay.Collections

Pets, mounts, appearances, titles and toys — and achievements, which doc 28 says are the same thing
with criteria.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Exploration, housing, collections and
**G-Q3**, part of **G8**.

## State

**Built: collectibles with an unlock source, achievements with a counted half on the event bus and a
standing half in the requirement algebra, the cascade, and a wardrobe. 34 tests.**

| | |
|---|---|
| `CollectibleDefinition` · `CollectibleKind` · `AchievementDefinition` | What a designer authors. |
| `Collectible` · `Achievement` · `CollectionLibrary` | Compiled once, with a `Problems` list. |
| `CollectionRecord` · `Unlock` · `UnlockSource` | One **account's** collection. |
| `Wardrobe` | One **character's** presentation. |
| `CollectionsModule` | Two definition types and two tag roots. |

## One mechanism, as doc 28 claims

*"All account-wide, all durable, and all one mechanism: a set of unlocked `DefId`s with an unlock
source recorded."* That is exactly what `CollectionRecord` is. `CollectibleKind` changes nothing about
how anything is stored — it is for the collection screen's tabs and for "how many mounts have I got",
not a second code path.

⚠ **An order rather than a timestamp.** Nothing in this library has a clock, for the same reason
nothing in `Vixen.Gameplay.Housing` does: a collection is a durable row that spends almost all its
life hibernating. A caller that wants dates stamps them on the way into its own store.

## Where this parts company with G-Q3, and why

G-Q3 answers that achievements belong here because *"an achievement is an unlock with criteria,
criteria are tag queries, and the state shape is identical"*. The first and third are right and this
library is built on them. **The second covers "have all five keys" and does not cover "kill thirty
boars"** — a tag query is a standing test and cannot count.

So an achievement has both halves:

- **`Requires`** — the standing half, in the kernel's requirement algebra rather than a bare tag query
  so it can also ask about a number.
- **`Criteria`** — the counted half, riding the kernel's `GameplayEventBus`. Whose own remarks name
  achievements and collections as the listeners it exists for.

⚠ **A criterion's tag query is over the *subject's* tags, not the player's.** "Kill thirty undead"
filters the victim. Keeping the two apart is what stops "kill thirty things while poisoned" from
silently meaning "kill thirty poisoned things".

**"Own fifty mounts" needs neither.** The record answers `Collection.Mount`, `Collection.Total`,
`Collection.Points` and `Collection.Earned` as ordinary requirement *values*, so a count is authored
as `Value Collection.Mount AtLeast 50` and no third requirement kind exists.

## The four things worth knowing before reading the code

### One subscription, and a watch dies when its criterion is done

⚠ A naive achievement system subscribes every criterion of every achievement and keeps them for ever,
so a mature account pays for thousands of dead filters on every kill. Here `Attach` makes **one**
subscription and the per-criterion filters are tested inside it, and a watch is dropped the moment its
criterion completes — **so the cost falls as an account completes things.** That is the curve that
matters: the accounts with the most live watches are the new ones, which generate the fewest events.

The settle pass is candidate-driven for the same reason. A finished criterion makes exactly one
achievement a candidate; only an unlock or an award — which move a tag or a count any requirement
anywhere could be asking about — marks everything, and those are rare where kills are not.

### Earning cascades, and stops

An achievement grants a tag and unlocks collectibles, either of which can complete another
achievement. That is resolved by a work queue rather than by recursion, and it terminates because
nothing is ever earned twice. In the test content one kill sets off Slayer → its title → Decorated →
a toy.

⚠ **An earned achievement never un-earns.** Requirements are checked on the way in and never again: a
refund, a sale or a patch that raises a threshold must not take back something somebody already did.

### The wardrobe is per character where the collection is per account

The split doc 28's paragraph does not make and every game needs. Unlocks are account-wide; two alts
have different transmog and different titles out of the same collection.

⚠ **An override to something no longer unlocked resolves to the real item, never to nothing.** An
appearance can be taken back — a refund, a season ending, a patch — and the character wearing it must
not turn invisible. `Resolve` checks the unlock *every time* rather than being told when one goes
away, precisely so there is no notification to miss. `Worn()` does the same for a title.

⚠ **Hiding a slot and overriding it are separate, and hiding wins.** "No helmet" and "a different
helmet" are different wishes; a game that models hiding as an override to nothing loses the chosen
look the moment the box is ticked and cannot give it back.

Doc 28 calls transmog *"one field and one visual-resolution rule"*. The field is per slot and lives
here rather than on the item, which is the amendment doc 28 already records under Items: a
sixteen-byte `ItemInstance` cannot hold variable-size per-copy data, so the gem list, the transmog
override and the custom name all want a side table.

### A save loads as it was

⚠ **`Restore` is deliberately not a replay.** Re-running `Unlock` would re-derive achievements against
today's content, so a patch that raised a threshold would take back what somebody earned, and one that
lowered it would hand out an achievement with no notification anybody saw.

Nothing settles at construction either, so a caller decides when notifications fire — `Refresh` is
what asks.

## What the build cannot catch, and it is worth knowing

**A misspelt verb on a criterion is undetectable here.** `AchievementDefinition.CollectTags` hands
every verb a criterion names to the content build, which bakes it — so `Event.Kil` resolves to a
perfectly real tag that nothing ever posts, and the kernel's empty-range trap never fires. What
`Compile` *can* catch is a criterion with no verb at all, and it does. Catching the other would need
the composition's list of verbs anything actually posts, which nothing keeps yet.

## What is owed

- **Account-wide durability.** Doc 28 puts collections in `Live.Progression.Cluster`; this is the
  shape, and task **#27**'s bridge is where it becomes a row.
- **The appearance a worn item *has*.** `Resolve` answers in ids and never asks what an item looks
  like, which is why this library does not reference `Vixen.Gameplay.Items` even though the spine
  would allow it. Turning an id into a mesh is the renderer's.
- **Transmog validity rules** — "only to something you could equip", "only to something of the same
  weapon class" — are a game's policy and go in the requirement list on the appearance.
- **Achievement categories, series and the collection screen.** Content organisation, not mechanism.
