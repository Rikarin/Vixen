# Vixen.Gameplay.Economy

Currencies, vendors and player trade — every one of them one balanced, idempotent intent against a
ledger seam.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Economy, part of **G5**.

## State

**Built: the ledger seam with its conservation oracle, currencies with caps, decay and conversion,
vendors with limited stock, restock and buyback, and the trade escrow with its confirm-lock. 38 tests.
Mail, the auction house and the price model are owed** — see below.

| | |
|---|---|
| `EconomyAccount` · `AssetMove` · `EconomyIntent` · `EconomyVerdict` · `EconomyResult` | The gameplay-side mirror of doc 27's ledger. |
| `IEconomyLedger` · `MemoryEconomyLedger` | The seam, and the one a test and a single-process game use. |
| `CurrencyDefinition` · `Currency` · `CurrencyConversion` · `CurrencyExchange` · `CurrencyScope` | Gold, tokens, marks, karma — one type. |
| `VendorDefinition` · `VendorStock` · `Vendor` · `VendorState` · `BuybackEntry` · `VendorRefusal` | Stock, restock, buyback. |
| `TradeSession` · `TradeOffer` · `TradeStatus` · `TradeRefusal` | The confirm-lock. |
| `EconomyLibrary` · `EconomyModule` | Compiled content with a `Problems` list. |

## It never holds anything, and that is what keeps the spine intact

Doc 28's dependency spine names `Items` as the only edge this library is allowed. Its § Inventory says
*"mail attachments, trade windows, vendor buyback… are all containers"*, which read literally would
need `Inventory` too.

The way out is that **this library never holds or moves anything**. A trade produces one
`EconomyIntent`; a purchase produces one; a buyback produces one. The realm applies them — task
**#27**'s bridge — exactly as `QuestJournal.TurnIn` reports a reward rather than paying it. So a trade
escrow is not a second container implementation, because it is not a container at all: it is a list of
what must move, and the thing that moves it is the one that already owns containers.

The same reasoning gives `IEconomyLedger` rather than a reference to doc 27's `ILedger`.
⚠ **It is synchronous where that one is async**, deliberately: a realm's rule runs in a frame and
cannot await a database, so the realm's implementation fronts the durable ledger with a view it
reconciles.

## The five things worth knowing before reading the code

### Balanced per asset, not overall

Summing every delta together would let a hundred gold leaving one account be paid for by a hundred ore
arriving in another — a duplication bug with a receipt.

### A player may not go negative and a world account may

That asymmetry is what makes a world account a *source* or a *sink*. A vendor's stock comes from
somewhere and a fee goes nowhere; modelling either with a real balance would mean seeding the world
with every coin it will ever mint. A player who cannot pay is refused, which is the check the whole
thing exists for.

⚠ **An intent is checked whole before any of it is written**, so an intent whose third movement
overdraws leaves the first two undone — the container algebra's all-or-nothing rule, one layer down.

### The confirm-lock needs a revision, and doc 28 does not say so

*"Both parties confirm, any change re-opens both confirmations"* is necessary and **not sufficient**.
It loses the race where a change and a confirmation cross in flight: the confirmation arrives after
the change has already cleared the flags, and simply sets one again — against goods its sender never
saw. So a confirmation quotes the revision it was looking at, and a stale one is refused.

⚠ **The adversarial test is what proves this is load-bearing.** Mutating `Confirm` to plain boolean
flags cleared on change — doc 28's sentence taken literally — makes
`TheLastMomentSwapCannotHappen` fail. Its assertion had to be strengthened first: comparing what
settled against what is *on the table now* is vacuous, because both sides of it read the same live
state. What it asserts is that a settlement's contents equal **what each party agreed to** when they
confirmed.

⚠ **An offer is allowed while locked**, and bumps the revision. Refusing it would make the swap
impossible by making the gesture impossible — a different and weaker guarantee, and one that also
refuses somebody legitimately changing their mind.

⚠ **A ledger refusal cancels the trade rather than reopening it**, because the usual refusal is that
somebody no longer holds what they offered, and reopening puts both parties in front of a table that
is now a lie.

### The stock is taken only after the ledger says yes

Taking it first and putting it back on refusal is two writes with a window between them, and the
window is where a limited-stock item goes missing without anybody getting it.

⚠ **Buyback costs exactly what was paid.** It exists because somebody sold something by accident;
charging a spread on the undo makes the price of a mis-click depend on how fast it was noticed.

### Caps report their overflow and conversions keep their remainder

A cap reports what did not fit rather than dropping it — only the caller knows whether to mail it,
refuse the reward or convert it, which is the decision `Container.Add` also refuses to make. A
conversion is integer and leaves the remainder: a hundred silver to a gold, converting two hundred and
fifty, gives two gold and leaves fifty. ⚠ **Decay rounds down so it can reach zero**; rounding to
nearest leaves everybody with one coin for ever, and a currency that never quite disappears is not a
sink.

## What is owed

- **Mail**, and doc 28 is explicit that it comes first: *"the delivery mechanism for auction
  settlement, so it must exist before the auction does"*. Attachments and cash-on-delivery are two
  more intents of the shape already here.
- **The auction house** — a market grain with an order book, listings with deposits and durations,
  bids or buyouts, settlement into mail, and a fee that is the primary currency sink.
- **`IMarketModel`** — moving-average pricing over recorded trades, so NPC buy orders can respond to
  supply without anybody writing an economy simulation.
- **Durability.** Everything here is in memory; the ledger that matters is doc 27's, behind task
  **#27**'s bridge. `MemoryEconomyLedger` is honest about being for tests and single-process games.
- **The guild bank**, which is where this library and `Vixen.Gameplay.Social` would meet — a container
  with a permission tag on it, and the first thing that needs both.
