using FCommerce.Customers.Contracts;

namespace FCommerce.DemoSeeder;

/// <summary>
/// Demo customers of the wholesaler. Contacts with portal access get invitation e-mails (Mailpit); one person acts
/// for two companies, one customer is blocked and one has nobody on the platform yet.
/// </summary>
internal static class DemoCustomers
{
    public const string Salesperson = "anna.handlowiec@fcommerce.local";
    public const string OtherSalesperson = "tomasz.sprzedawca@fcommerce.local";

    private static readonly SalespersonData Anna = new("Anna Handlowiec", Salesperson, "+48 600 100 200");
    private static readonly SalespersonData Tomasz = new("Tomasz Sprzedawca", OtherSalesperson, "+48 600 300 400");

    public static IReadOnlyList<CustomerData> All { get; } =
    [
        new(
            "K-1001",
            1,
            "Budmax Sp. z o.o.",
            "5273014506",
            new AddressData("ul. Okopowa 58", "01-042", "Warszawa", "PL"),
            [
                new DeliveryAddressData("MAG-WAW", "Magazyn Warszawa", new AddressData("ul. Annopol 4", "03-236", "Warszawa", "PL"), IsDefault: true),
                new DeliveryAddressData("BUD-WOLA", "Budowa Wola Park II", new AddressData("ul. Górczewska 124", "01-460", "Warszawa", "PL")),
            ],
            [
                new ContactData("C-1", "Jan Kowalski", "jan.kowalski@budmax.example", "+48 501 111 222", PortalAccess: true, "administrator"),
                new ContactData("C-2", "Ewa Nowak", "ewa.nowak@budmax.example", null, PortalAccess: true, "accounting"),
                new ContactData("C-3", "Piotr Wiśniewski", "piotr.wisniewski@budmax.example", null, PortalAccess: true, "buyer"),
                new ContactData("C-4", "Marek Zieliński", null, "+48 501 333 444"),
            ],
            Anna,
            PriceListCode: "HURT-A",
            PaymentTermDays: 30),
        new(
            "K-1002",
            1,
            "Elektro-Hurt Kraków s.c.",
            "6772213816",
            new AddressData("ul. Wielicka 28", "30-552", "Kraków", "PL"),
            [new DeliveryAddressData("SKLEP", "Sklep firmowy", new AddressData("ul. Wielicka 28", "30-552", "Kraków", "PL"), IsDefault: true)],
            [
                new ContactData("C-1", "Katarzyna Wójcik", "katarzyna.wojcik@elektrohurt.example", null, PortalAccess: true, "administrator"),
                new ContactData("C-2", "Jan Kowalski", "jan.kowalski@budmax.example", null, PortalAccess: true, "buyer"),
            ],
            Tomasz,
            PaymentTermDays: 14),
        new(
            "K-1003",
            1,
            "Metal-Serwis Poznań Sp. z o.o.",
            "7781349208",
            new AddressData("ul. Głogowska 210", "60-104", "Poznań", "PL"),
            null,
            [new ContactData("C-1", "Andrzej Lewandowski", "andrzej.lewandowski@metalserwis.example", null, PortalAccess: true, "administrator")],
            Anna,
            PaymentTermDays: 21,
            Blocked: true),
        new(
            "K-1004",
            1,
            "Stal-Bud Gdańsk Sp. j.",
            "5833021106",
            new AddressData("ul. Elbląska 135", "80-718", "Gdańsk", "PL"),
            null,
            [new ContactData("C-1", "Barbara Kamińska", "barbara.kaminska@stalbud.example", null)],
            Tomasz,
            PaymentTermDays: 14),
    ];
}
