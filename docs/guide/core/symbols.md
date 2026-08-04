---
title: Symbols
slug: core/symbols
kind: guide
area: Core
summary: An interned name in four bytes, hashed rather than numbered, so two machines agree on it.
api: [T:Vixen.Core.Symbol]
tags: [core, symbols, determinism, interning]
since: 0.1
status: stable
related: [animation/move-sets, ai/blackboard]
---

## What it is

A `Symbol` is a name compressed to four bytes: `Symbol.Intern("left-palm")` gives you a value that
compares, sorts and hashes as fast as a `uint`, because it *is* one. The number is the FNV-1a hash of
the name, not an index into a table.

The spelling is kept in a process-wide table so a debugger and an error message can print `left-palm`
rather than `0x1f3c9a02`. Nothing in a frame reads it.

## What it is for

Anywhere content names something and a frame compares that name: a move set's facets, a pose
constraint's slot, a blackboard key, a GOAP world key.

The alternative is a `string`, which costs a reference, a length check and a character loop per
comparison, or an index assigned in first-seen order — which is the one thing this type exists to
avoid. **An index depends on what a process happened to load first**, so two machines running the
same build assign different numbers to the same word, and any selection that breaks a tie on a number
then picks differently on each of them. Over a network that is a desync rather than a curiosity. A
hash is the same everywhere, for ever, with no table to ship and no order to agree on.

You do *not* want it as a general string replacement. There is no way back from a symbol to its name
in a process that never interned it, so anything a user types, anything you display, and anything you
round-trip through a file as text stays a `string`.

## Using it

```csharp compile
using Vixen.Core;

public static class Interning {
    public static void Run() {
        var gait = Symbol.Intern("gait");
        var same = Symbol.Intern("gait");

        // Four bytes, compared as four bytes.
        Console.WriteLine(gait == same);        // True

        // The empty symbol. Matches nothing, including itself where a vocabulary asks.
        var none = Symbol.None;

        Console.WriteLine(none.IsSome);         // False
        Console.WriteLine(gait.ToString());     // gait
    }
}
```

⚠ **Interning is case- and culture-sensitive, deliberately.** A vocabulary is authored content, and
content that means two different things depending on how somebody typed it is a bug the build should
surface rather than paper over.

⚠ **32 bits collide, and the place to catch that is where a vocabulary is composed.** Two words in
one vocabulary hashing alike would silently become the same word. `Symbol` records the second
spelling when it sees one, and `TryGetCollision` is what a builder asks before it accepts a
vocabulary — `MoveSet.Compose` does it for a movement vocabulary, `BlackboardLayoutBuilder.Build`
does it for a blackboard's keys. A one-in-fifty-thousand mystery becomes a build error naming both
words.

Interning happens in static initialisers all over an assembly, which is no place to throw from, so
`Intern` itself never refuses.

## Examples

Refusing a vocabulary that collides, which is what every builder over symbols should do:

```csharp compile
using Vixen.Core;

public static class Vocabulary {
    public static Symbol Add(HashSet<Symbol> vocabulary, string name) {
        var symbol = Symbol.Intern(name);

        if (symbol.TryGetCollision(out var first, out var second)) {
            throw new InvalidOperationException($"'{first}' and '{second}' hash alike; rename one.");
        }

        if (!vocabulary.Add(symbol)) {
            throw new InvalidOperationException($"'{name}' is already in this vocabulary.");
        }

        return symbol;
    }
}
```

## See also

- [Move sets](../animation/move-sets.md) — the first vocabulary built on symbols, and where this type
  originally lived.
- [The blackboard](../ai/blackboard.md) — a key table whose names are symbols and whose reads are
  indices.
