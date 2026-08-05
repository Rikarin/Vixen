// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Economy;

/// <summary>Every economic definition a build knows, compiled once.</summary>
public sealed class EconomyLibrary {
    readonly Dictionary<uint, Currency> currencies;
    readonly Dictionary<uint, Vendor> vendors;
    readonly string[] problems;

    EconomyLibrary(Dictionary<uint, Currency> currencies, Dictionary<uint, Vendor> vendors, string[] problems) {
        this.currencies = currencies;
        this.vendors = vendors;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static EconomyLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>Every currency, in address order.</summary>
    public IEnumerable<Currency> Currencies =>
        currencies.Values.OrderBy(currency => currency.Definition.Address, StringComparer.Ordinal);

    /// <summary>Every vendor, in address order.</summary>
    public IEnumerable<Vendor> Vendors =>
        vendors.Values.OrderBy(vendor => vendor.Definition.Address, StringComparer.Ordinal);

    /// <summary>What did not resolve, and what a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles everything in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static EconomyLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var currencies = new Dictionary<uint, Currency>();

        foreach (var definition in catalog.OfType<CurrencyDefinition>()) {
            currencies.Add(
                definition.Id.Value,
                new(
                    definition,
                    tags.Resolve(definition.Tag),
                    [
                        .. definition.Conversions.Select(
                            conversion => new CurrencyConversion(
                                DefId.From(conversion.To),
                                conversion.To,
                                Math.Max(1, conversion.Rate),
                                conversion.OneWay
                            )
                        )
                    ]
                )
            );
        }

        // Conversions are checked after every currency is read, for the reason a chain edge is: a
        // designer writes silver → gold in the file that comes first alphabetically as often as not.
        foreach (var currency in currencies.Values) {
            foreach (ref readonly var conversion in currency.Conversions) {
                if (!currencies.ContainsKey(conversion.To.Value)) {
                    problems.Add(
                        $"'{currency.Definition.Address}' converts to '{conversion.Address}', which is not a "
                        + "currency in this build."
                    );
                }
            }
        }

        var vendors = new Dictionary<uint, Vendor>();

        foreach (var definition in catalog.OfType<VendorDefinition>()) {
            var stock = new VendorStock[definition.Stock.Count];

            for (var row = 0; row < stock.Length; row++) {
                var entry = definition.Stock[row];

                if (entry.Item.Length == 0) {
                    problems.Add($"'{definition.Address}' row {row} sells nothing.");
                } else if (!catalog.Contains(DefId.From(entry.Item))) {
                    problems.Add(
                        $"'{definition.Address}' row {row} sells '{entry.Item}', which is not in this build."
                    );
                }

                if (!currencies.ContainsKey(DefId.From(entry.Currency).Value)) {
                    problems.Add(
                        $"'{definition.Address}' row {row} is priced in '{entry.Currency}', which is not a "
                        + "currency in this build — so it costs nothing anybody has."
                    );
                }

                if (entry.Quantity == 0 && entry.RestockSeconds > 0f) {
                    problems.Add(
                        $"'{definition.Address}' row {row} never runs out and has a restock timer, so the "
                        + "timer does nothing."
                    );
                }

                stock[row] = new(entry, row, RequirementSet.Compile(entry.Requires, tags));
            }

            vendors.Add(definition.Id.Value, new(definition, tags.Resolve(definition.Tag), stock));
        }

        return new(currencies, vendors, [.. problems]);
    }

    /// <summary>Finds a currency.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Currency? FindCurrency(DefId id) => currencies.GetValueOrDefault(id.Value);

    /// <summary>Finds a vendor.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public Vendor? FindVendor(DefId id) => vendors.GetValueOrDefault(id.Value);
}
