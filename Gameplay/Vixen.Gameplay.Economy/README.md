# Vixen.Gameplay.Economy

Currencies, vendors, player trade, mail and an auction house — every one of them one balanced,
idempotent intent against a ledger seam.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Economy, which is **G5**.

## State

**Built: the ledger seam with its conservation oracle, currencies, vendors, the trade escrow with its
confirm-lock, mail with cash on delivery, the auction house with its deposit and fee, and the price
model. 70 tests, and G5 with them.**

| | |
|---|---|
| `EconomyAccount` · `AssetMove` · `EconomyIntent` · `EconomyVerdict` · `EconomyResult` | The gameplay-side mirror of doc 27's ledger. |
| `IEconomyLedger` · `MemoryEconomyLedger` | The seam, and the one a test and a single-process game use. |
| `CurrencyDefinition` · `Currency` · `CurrencyConversion` · `CurrencyExchange` · `CurrencyScope` | Gold, tokens, marks, karma — one type. |
| `VendorDefinition` · `VendorStock` · `Vendor` · `VendorState` · `BuybackEntry` · `VendorRefusal` | Stock, restock, buyback. |
| `TradeSession` · `TradeOffer` · `TradeStatus` · `TradeRefusal` | The confirm-lock. |
| `PostOffice` · `MailMessage` · `MailAttachment` · `MailId` · `MailRefusal` | Escrowed at send; claimed all at once. |
| `AuctionHouse` · `AuctionListing` · `ListingId` · `ListingStatus` · `AuctionRefusal` | An order book, a deposit and the primary sink. |
| `IMarketModel` · `MovingAverageMarket` · `TradeRecord` | A price model, not an economy simulation. |
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

## The eight things worth knowing before reading the code

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

### Everything is escrowed at the moment it is offered, never at the moment it is taken

Mail escrows an attachment when the letter is posted; an auction escrows the goods when the listing
goes up. The obvious alternative — record what was promised and move it on claim — lets a sender
attach a sword, post it, sell the sword, and have the recipient claim a second one.

That rule needed a second entry point. `PostOffice.Send` escrows and posts; `PostOffice.Deliver`
posts a letter for goods **already** in the mail account, which is what an auction settlement needs
because it moves both halves in one intent. ⚠ **The first version did not have it** and routed the
return of an expired letter through `Send`, moving goods a second time out of an account that no
longer held them, then posting a corrective intent to undo it — two writes in a library whose whole
argument is that there is one.

### The auction's asymmetries are each a specific abuse

⚠ **Outbidding refunds the previous bidder in the same intent.** Two operations is a window in which
the refund fails and somebody's gold is gone.

⚠ **The deposit comes back on a sale and is destroyed on an expiry.** That is what prices listing
something nobody wants, and it is why the auction house is the primary currency sink along with the
fee.

⚠ **A listing with a bid may not be withdrawn.** Otherwise a seller uses the house to discover what
somebody will pay and then sells privately — and, worse, cancels every auction they are about to lose.

⚠ **Kept means destroyed, not pocketed.** A deposit that went to a world account would make that
account grow for ever and stop being a sink.

### The price model is weighted by count, and windowed rather than decayed

⚠ **A unit price, not a total** — recording totals makes a stack of a hundred look like a hundred-fold
price rise. ⚠ **Weighted by count**, because one sale of a hundred ore says more about the price than
one sale of one, and an unweighted mean lets somebody move the reference by listing a single one at an
absurd number. ⚠ **A fixed window rather than a time decay**, because a decay answers differently
depending on when it is asked and makes a displayed price flicker while nobody trades.

### The key set is the one thing in this library that leaks, and the fix is safety-critical

`MemoryEconomyLedger` remembers every key it has applied so that a retried trade writes nothing the
second time. Nothing removed one, and a shard that runs for a week kept every key of that week —
`Samples/14-Mmo`'s soak measured about a megabyte a minute.

⚠ **The two failure modes are not comparable, and the whole of `KeyHorizon` follows from that.** Too
long costs memory: visible in a graph, recovered by a restart. Too short duplicates an item:
invisible, permanent, and indistinguishable from an exploit when a player reports it. So there is
**no default horizon** — a number nobody chose is not safer than an unbounded set, it is the same risk
with the evidence removed — and the only bounded constructor takes the **retry window** rather than
the horizon, which makes a horizon shorter than the retries it must outlive unrepresentable.

⚠ **`Guaranteed` is the number a safety argument is made of, and it is not `Length`.** Forgetting
happens a generation at a time, so a key added just before a rotation is dropped one `Interval`
earlier than the nominal horizon. The worst case is what the guarantee is about, and a test that
posts at a rotation instead of just before one passes with the bound stated wrongly.

⚠ **Sweeping is explicit and never lazy.** `Forget(now)` is called off the frame path; doing it inside
`Post` would put the cost on a rule mid-frame and would make a quiet realm's keys expire on the next
busy realm's clock.

### Letting a player go is not a movement of value

`Release` hands everything an account holds to the world account it was seeded out of and drops the
rows, because a realm that only ever seeds balances keeps every departed player's purse. ⚠ **It moves
the balances rather than deleting them**, so `Total` — the arithmetic check that finds duplication
bugs — still sums to zero. ⚠ **And it is not an `EconomyIntent`**: nothing has moved, the player still
owns what they left with, and a key in the journal saying value changed hands is a lie an auditor
would later have to explain.

## What is owed

- **Durability.** Everything here is in memory; the ledger that matters is doc 27's, behind task
  **#27**'s bridge. `MemoryEconomyLedger` is honest about being for tests and single-process games,
  and `AuctionHouse` is one market rather than doc 28's "a grain per market".
- **The guild bank**, which is where this library and `Vixen.Gameplay.Social` would meet — a container
  with a permission tag on it, and the first thing that needs both.
- **Vendor dynamic pricing.** `IMarketModel` exists and a vendor does not read it; wiring the two is
  a game's decision about which vendors respond to supply, and doc 28 calls it a hook.
