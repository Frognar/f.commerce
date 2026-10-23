using System.Globalization;
using FCommerce.Catalog.Contracts;

namespace FCommerce.DemoSeeder;

/// <summary>A deterministic demo assortment of an industrial supplies wholesaler, in Polish and English.</summary>
internal static class DemoCatalog
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<CategoryData> Categories { get; } =
    [
        Category("fasteners", null, "Elementy złączne", "Fasteners", 1),
        Category("bolts", "fasteners", "Śruby", "Bolts", 1),
        Category("nuts", "fasteners", "Nakrętki", "Nuts", 2),
        Category("washers", "fasteners", "Podkładki", "Washers", 3),
        Category("anchors", "fasteners", "Kotwy i kołki", "Anchors and plugs", 4),
        Category("tools", null, "Narzędzia", "Tools", 2),
        Category("drill-bits", "tools", "Wiertła", "Drill bits", 1),
        Category("hand-tools", "tools", "Narzędzia ręczne", "Hand tools", 2),
        Category("electrical", null, "Elektryka", "Electrical", 3),
        Category("cable-ties", "electrical", "Opaski kablowe", "Cable ties", 1),
        Category("safety", null, "BHP", "Safety", 4),
    ];

    public static IReadOnlyList<ProductData> Products { get; } =
    [
        .. Bolts(),
        .. Nuts(),
        .. Washers(),
        .. Anchors(),
        .. DrillBits(),
        .. HandTools(),
        .. CableTies(),
        .. Safety(),
    ];

    private static IEnumerable<ProductData> Bolts() =>
        from size in new[] { 6, 8, 10, 12 }
        from length in new[] { 20, 30, 40, 60, 80 }
        from finish in new[] { ("ZN", "ocynkowana", "zinc-plated"), ("A2", "nierdzewna A2", "stainless A2") }
        select Product(
            $"SR-M{size}x{length}-{finish.Item1}",
            "bolts",
            $"Śruba z łbem sześciokątnym M{size}x{length} {finish.Item2}",
            $"Hex head bolt M{size}x{length} {finish.Item3}",
            "Śruba DIN 933 z pełnym gwintem, do połączeń rozłącznych w konstrukcjach stalowych.",
            "DIN 933 fully threaded bolt for detachable joints in steel structures.",
            "szt",
            ("standard", "Norma", "Standard", "DIN 933", "DIN 933"),
            ("thread", "Gwint", "Thread", $"M{size}", $"M{size}"),
            ("length", "Długość", "Length", $"{length} mm", $"{length} mm"),
            ("material", "Materiał", "Material", finish.Item1 == "A2" ? "Stal nierdzewna" : "Stal 8.8", finish.Item1 == "A2" ? "Stainless steel" : "Steel 8.8"));

    private static IEnumerable<ProductData> Nuts() =>
        from size in new[] { 6, 8, 10, 12, 16 }
        select Product(
            $"NK-M{size}-ZN",
            "nuts",
            $"Nakrętka sześciokątna M{size} ocynkowana",
            $"Hex nut M{size} zinc-plated",
            "Nakrętka DIN 934, klasa 8.",
            "DIN 934 nut, class 8.",
            "szt",
            ("standard", "Norma", "Standard", "DIN 934", "DIN 934"),
            ("thread", "Gwint", "Thread", $"M{size}", $"M{size}"));

    private static IEnumerable<ProductData> Washers() =>
        from size in new[] { 6, 8, 10, 12, 16 }
        select Product(
            $"PD-{size}-ZN",
            "washers",
            $"Podkładka płaska {size} mm ocynkowana",
            $"Flat washer {size} mm zinc-plated",
            "Podkładka DIN 125A.",
            "DIN 125A washer.",
            "szt",
            ("standard", "Norma", "Standard", "DIN 125A", "DIN 125A"),
            ("diameter", "Średnica wewnętrzna", "Inner diameter", $"{size + 0.4m:0.0} mm", $"{size + 0.4m:0.0} mm"));

    private static IEnumerable<ProductData> Anchors() =>
        from size in new[] { 6, 8, 10 }
        from kind in new[] { ("NY", "Kołek rozporowy nylonowy", "Nylon wall plug"), ("ST", "Kotwa stalowa tulejowa", "Steel sleeve anchor") }
        select Product(
            $"KT-{kind.Item1}-{size}",
            "anchors",
            $"{kind.Item2} {size} mm",
            $"{kind.Item3} {size} mm",
            "Do montażu w betonie i cegle pełnej.",
            "For fixing in concrete and solid brick.",
            "szt",
            ("diameter", "Średnica", "Diameter", $"{size} mm", $"{size} mm"));

    private static IEnumerable<ProductData> DrillBits() =>
        from diameter in new[] { 3m, 4m, 5m, 6m, 8m, 10m, 12m }
        from kind in new[] { ("HSS", "do metalu HSS", "HSS for metal"), ("SDS", "do betonu SDS-plus", "SDS-plus for concrete") }
        select Product(
            $"WR-{kind.Item1}-{(diameter * 10).ToString("0", Invariant)}",
            "drill-bits",
            $"Wiertło {kind.Item2} {diameter.ToString("0.#", Invariant)} mm",
            $"Drill bit {kind.Item3} {diameter.ToString("0.#", Invariant)} mm",
            kind.Item1 == "HSS" ? "Wiertło szlifowane, kąt wierzchołkowy 118°." : "Wiertło z węglikową płytką skrawającą do młotowiertarek.",
            kind.Item1 == "HSS" ? "Ground drill bit, 118° point angle." : "Carbide-tipped bit for rotary hammers.",
            "szt",
            ("diameter", "Średnica", "Diameter", $"{diameter.ToString("0.#", Invariant)} mm", $"{diameter.ToString("0.#", Invariant)} mm"),
            ("shank", "Chwyt", "Shank", kind.Item1 == "HSS" ? "Cylindryczny" : "SDS-plus", kind.Item1 == "HSS" ? "Cylindrical" : "SDS-plus"));

    private static IEnumerable<ProductData> HandTools() =>
    [
        Product("NR-KLUCZ-13", "hand-tools", "Klucz płasko-oczkowy 13 mm", "Combination spanner 13 mm", "Stal chromowo-wanadowa.", "Chrome vanadium steel.", "szt",
            ("size", "Rozmiar", "Size", "13 mm", "13 mm")),
        Product("NR-KLUCZ-17", "hand-tools", "Klucz płasko-oczkowy 17 mm", "Combination spanner 17 mm", "Stal chromowo-wanadowa.", "Chrome vanadium steel.", "szt",
            ("size", "Rozmiar", "Size", "17 mm", "17 mm")),
        Product("NR-MLOT-500", "hand-tools", "Młotek ślusarski 500 g", "Engineer's hammer 500 g", "Trzonek z włókna szklanego.", "Fibreglass handle.", "szt",
            ("weight", "Masa", "Weight", "500 g", "500 g")),
        Product("NR-MIARA-5", "hand-tools", "Miara zwijana 5 m", "Tape measure 5 m", "Taśma stalowa, klasa dokładności II.", "Steel tape, accuracy class II.", "szt",
            ("length", "Długość", "Length", "5 m", "5 m")),
    ];

    private static IEnumerable<ProductData> CableTies() =>
        from length in new[] { 100, 200, 300, 400 }
        from colour in new[] { ("CZ", "czarne", "black"), ("BI", "białe", "white") }
        select Product(
            $"OK-{length}-{colour.Item1}",
            "cable-ties",
            $"Opaski kablowe {length} mm {colour.Item2} (100 szt.)",
            $"Cable ties {length} mm {colour.Item3} (100 pcs)",
            "Poliamid 6.6, odporne na UV.",
            "Polyamide 6.6, UV resistant.",
            "opak",
            ("length", "Długość", "Length", $"{length} mm", $"{length} mm"),
            ("colour", "Kolor", "Colour", colour.Item2, colour.Item3));

    private static IEnumerable<ProductData> Safety() =>
    [
        Product("BHP-REK-9", "safety", "Rękawice robocze powlekane nitrylem, rozmiar 9", "Nitrile-coated work gloves, size 9", "Kategoria II, EN 388.", "Category II, EN 388.", "para",
            ("size", "Rozmiar", "Size", "9", "9")),
        Product("BHP-OKUL", "safety", "Okulary ochronne bezbarwne", "Clear safety glasses", "EN 166, powłoka przeciwparowa.", "EN 166, anti-fog coating.", "szt"),
        Product("BHP-KASK-BI", "safety", "Kask ochronny biały", "Safety helmet white", "EN 397, regulacja obwodu.", "EN 397, adjustable headband.", "szt"),
    ];

    private static CategoryData Category(string code, string? parent, string pl, string en, int sortOrder) =>
        new(code, parent, new Dictionary<string, string> { ["pl"] = pl, ["en"] = en }, Version: 1, sortOrder);

    private static ProductData Product(
        string sku,
        string category,
        string namePl,
        string nameEn,
        string descriptionPl,
        string descriptionEn,
        string unit,
        params (string Code, string NamePl, string NameEn, string ValuePl, string ValueEn)[] attributes) =>
        new(
            sku,
            Version: 2,
            new Dictionary<string, string> { ["pl"] = namePl, ["en"] = nameEn },
            new Dictionary<string, string> { ["pl"] = descriptionPl, ["en"] = descriptionEn },
            category,
            unit,
            Ean13(sku),
            ImageIds: [],
            [.. attributes.Select(a => new AttributeData(
                a.Code,
                new Dictionary<string, string> { ["pl"] = a.NamePl, ["en"] = a.NameEn },
                new Dictionary<string, string> { ["pl"] = a.ValuePl, ["en"] = a.ValueEn }))]);

    /// <summary>Stable EAN-13 in the Polish GS1 prefix (590), derived from the SKU, with a valid check digit.</summary>
    private static string Ean13(string sku)
    {
        var hash = sku.Aggregate(17L, (h, c) => ((h * 31) + c) % 1_000_000_000L);
        var data = $"590{hash:000000000}";
        var sum = data.Reverse().Select((c, i) => (c - '0') * (i % 2 == 0 ? 3 : 1)).Sum();
        return data + ((10 - (sum % 10)) % 10).ToString(Invariant);
    }
}
