---
title: Currencies, vendors and trade
slug: gameplay/economy
kind: guide
area: Gameplay
summary: Every transaction is one balanced, idempotent intent against a ledger seam — and the trade confirm-lock needs a revision, which is the part doc 28 does not say.
api: [T:Vixen.Gameplay.Economy.EconomyAccount, T:Vixen.Gameplay.Economy.AssetMove, T:Vixen.Gameplay.Economy.EconomyIntent, T:Vixen.Gameplay.Economy.EconomyVerdict, T:Vixen.Gameplay.Economy.EconomyResult, T:Vixen.Gameplay.Economy.IEconomyLedger, T:Vixen.Gameplay.Economy.MemoryEconomyLedger, T:Vixen.Gameplay.Economy.CurrencyScope, T:Vixen.Gameplay.Economy.CurrencyDefinition, T:Vixen.Gameplay.Economy.CurrencyConversionDefinition, T:Vixen.Gameplay.Economy.CurrencyConversion, T:Vixen.Gameplay.Economy.CurrencyExchange, T:Vixen.Gameplay.Economy.Currency, T:Vixen.Gameplay.Economy.VendorStockDefinition, T:Vixen.Gameplay.Economy.VendorDefinition, T:Vixen.Gameplay.Economy.VendorStock, T:Vixen.Gameplay.Economy.Vendor, T:Vixen.Gameplay.Economy.VendorState, T:Vixen.Gameplay.Economy.VendorRefusal, T:Vixen.Gameplay.Economy.BuybackEntry, T:Vixen.Gameplay.Economy.TradeStatus, T:Vixen.Gameplay.Economy.TradeRefusal, T:Vixen.Gameplay.Economy.TradeOffer, T:Vixen.Gameplay.Economy.TradeSession, T:Vixen.Gameplay.Economy.EconomyLibrary, T:Vixen.Gameplay.Economy.EconomyModule]
tags: [gameplay, economy, currency, vendor, trade, ledger, mmo]
since: 0.1
status: preview
related: [gameplay/items, gameplay/inventory, gameplay/requirements]
---

## What it is

An **`EconomyIntent`** is a set of movements that either all happen or none do, named by a key that
makes replaying it free. A **`Currency`** is gold, tokens, marks or karma — one type with a cap, a
decay and conversions. A **`VendorState`** is one vendor's stock, restock clock and buyback window. A
**`TradeSession`** is two players swapping, with the confirm-lock that makes the last-moment swap
impossible.

## What it is for

The part of a game where correctness is not negotiable. Doc 28: *"every one of those is a ledger
transaction with an idempotency key"* — a duplicated settlement, a retried claim and a confirmation
that arrives twice are all no-ops the second time by construction.

## Using it

Compile an `EconomyLibrary`, give the realm an `IEconomyLedger`, and post intents.

⚠ **This library never holds or moves anything.** It says what must move; the realm applies it. That
is what keeps a trade escrow from being a second container implementation, and it is the same shape
`QuestJournal.TurnIn` has.

⚠ **Balanced per asset, not overall** — otherwise gold leaving one account can be paid for by ore
arriving in another.

⚠ **A player may not go negative; a world account may.** That asymmetry is what makes a world account
a source or a sink.

⚠ **A confirmation quotes the revision it saw.** "Any change re-opens both confirmations" loses the
race where a change and a confirmation cross in flight; the revision turns that into a refusal.

⚠ **Stock is taken only after the ledger says yes**, and **buyback costs exactly what was paid**.

⚠ **A cap reports its overflow** and **a conversion keeps its remainder**; decay rounds down so it can
reach zero.

## Examples

Two currencies and a vendor:

```yaml
# Assets/Economy/gold.vxdef
!CurrencyDefinition
displayName: Gold
tag: Currency.Gold
cap: 1000000
scope: Account

# Assets/Economy/smith.vxdef
!VendorDefinition
displayName: Smith
buybackSlots: 12
stock:
  - { item: items/potion, currency: currency/gold, price: 5 }
  - { item: items/sword, currency: currency/gold, price: 100, quantity: 2, restockSeconds: 600 }
```

Buying something:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Economy;

static class Shop {
    public static VendorRefusal Buy(VendorState smith, PlayerId who, IEconomyLedger ledger, float now) =>
        // The operation string is what makes a retry free: the same purchase twice is one purchase.
        smith.Buy(who, row: 1, count: 1, ledger, context: null, now, operation: "click-7134");
}
```

A trade, from the server's side:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Economy;

static class Swap {
    public static TradeRefusal Accept(TradeSession trade, PlayerId who, int revisionTheClientSaw) =>
        // Stale if anything moved since that revision — which is the whole confirm-lock.
        trade.Confirm(who, revisionTheClientSaw);

    public static TradeRefusal Finish(TradeSession trade, IEconomyLedger ledger) {
        var refusal = trade.Settle(ledger, out var result);

        // One intent, so a trade that half-applied is not a state the ledger can be left in.
        return refusal != TradeRefusal.None || !result.Ok ? refusal : TradeRefusal.None;
    }
}
```

Minting and sinking, which is what a world account is for:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Economy;

static class Mint {
    public static EconomyResult Reward(IEconomyLedger ledger, PlayerId who, DefId currency, long amount) =>
        ledger.Post(
            EconomyIntent.Transfer(
                $"reward/{who}/{currency}",
                EconomyAccount.Of(EconomyAccount.Vendor),
                EconomyAccount.Of(who),
                currency,
                amount
            )
        );
}
```

## See also

- [Items](gameplay/items) — what a vendor's stock names.
- [Inventory](gameplay/inventory) — what actually moves the goods this library reports.
- [Requirements](gameplay/requirements) — what gates a stock row.
