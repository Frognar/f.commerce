using FCommerce.Documents.Domain;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Monetary;
using FCommerce.SharedKernel.Optional;
using FCommerce.Testing;

namespace FCommerce.Documents.Tests;

public sealed class DocumentTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    private static UnvalidatedDocument Invoice(long version = 1) =>
        new("FV-1", version, " K-001 ", "Invoice", "FV/2026/10/001", Today.AddDays(-20), Today.AddDays(-20), Today.AddDays(-6), "pln", 1000m, 1230m, 0m, "ZAM/7");

    private static Document Valid(UnvalidatedDocument input) => Domain.Documents.Validate(input).ShouldBeValid();

    private static Currency Pln => Currency.Parse("PLN").ShouldSucceed();

    [Fact]
    public void A_valid_invoice_is_normalized()
    {
        var document = Valid(Invoice());

        Assert.Equal(("FV-1", "K-001", DocumentType.Invoice), (document.Id.Value, document.Content.CustomerCode, document.Content.Type));
        Assert.Equal(new DocumentAmounts(Pln, 1000m, 1230m, 0m), document.Content.Amounts);
        Assert.Equal(["ZAM/7"], document.Content.OrderNumbers);
    }

    [Fact]
    public void A_document_of_several_orders_lists_each_number_once()
    {
        var document = Valid(Invoice() with { OrderNumbers = [" WWW/2026/000002 ", "ZAM/7", "", "WWW/2026/000002"] });
        var none = Valid(Invoice() with { OrderNumber = default });

        Assert.Equal(["ZAM/7", "WWW/2026/000002"], document.Content.OrderNumbers);
        Assert.Empty(none.Content.OrderNumbers);
    }

    [Fact]
    public void Order_numbers_are_bounded()
    {
        var tooMany = Invoice() with { OrderNumbers = [.. Enumerable.Range(1, 51).Select(i => $"Z/{i}")] };
        var tooLong = Invoice() with { OrderNumbers = [new string('Z', 65)] };

        Assert.Equal("order-numbers.too-many", Assert.Single(Domain.Documents.Validate(tooMany).ShouldBeInvalid()).Code);
        Assert.Equal("orderNumbers[0]", Assert.Single(Domain.Documents.Validate(tooLong).ShouldBeInvalid()).Field);
    }

    [Fact]
    public void Delivery_notes_need_no_amounts_and_paid_defaults_to_zero()
    {
        var note = Valid(Invoice() with { Type = "delivery-note", Currency = default, Net = default, Gross = default, Paid = default, DueDate = default });
        var proforma = Valid(Invoice() with { Type = "proforma", Paid = default });

        note.Content.Amounts.ShouldBeNone();
        Assert.Equal(0m, proforma.Content.Amounts.ShouldBeSome().Paid);
    }

    [Fact]
    public void Every_problem_is_reported_with_its_path()
    {
        var input = Invoice() with
        {
            Id = " ",
            Version = -1,
            Type = "receipt",
            Number = "",
            IssueDate = default,
            Currency = "ZLOTY",
            Net = default,
            Gross = 10.001m,
        };

        var errors = Domain.Documents.Validate(input).ShouldBeInvalid();

        Assert.Equal(
            ["id", "version", "type", "number", "issueDate", "currency", "net", "gross"],
            errors.Select(e => e.Field));
    }

    [Theory]
    [InlineData("Invoice", null, null, "gross", "amounts.missing")]
    [InlineData("invoice", -100, -123, "gross", "amount.negative")]
    public void Invoices_need_non_negative_amounts(string type, int? net, int? gross, string field, string code)
    {
        var input = Invoice() with
        {
            Type = type,
            Net = net.ToMaybe().Map(value => (decimal)value),
            Gross = gross.ToMaybe().Map(value => (decimal)value),
            Currency = net.ToMaybe().Map(_ => "PLN"),
            Paid = default,
        };

        var error = Assert.Single(Domain.Documents.Validate(input).ShouldBeInvalid());

        Assert.Equal((field, code), (error.Field, error.Code));
    }

    [Fact]
    public void A_correction_may_lower_the_amount_and_then_nothing_is_due()
    {
        var correction = Valid(Invoice() with { Type = "correction-invoice", Net = -100m, Gross = -123m });

        Assert.Equal(0m, Domain.Documents.Outstanding(correction.Content));
        Assert.Equal(PaymentStatus.Paid, Domain.Documents.StatusOn(correction.Content, Today));
    }

    [Fact]
    public void The_due_date_must_not_precede_the_issue_date()
    {
        var error = Assert.Single(Domain.Documents.Validate(Invoice() with { DueDate = Today.AddDays(-21) }).ShouldBeInvalid());

        Assert.Equal(("dueDate", "due-date.before-issue"), (error.Field, error.Code));
    }

    [Theory]
    [InlineData(0, -1, "Overdue")]
    [InlineData(0, 0, "Unpaid")] // the due date itself is on time
    [InlineData(1000, -1, "Overdue")] // partly paid
    [InlineData(1230, -1, "Paid")]
    [InlineData(1500, -1, "Paid")] // overpaid
    [InlineData(0, null, "Unpaid")] // no due date
    public void Payment_status_follows_what_is_paid_and_the_due_date(int paid, int? dueInDays, string expected)
    {
        var document = Valid(Invoice() with { Paid = paid, DueDate = dueInDays.ToMaybe().Map(days => Today.AddDays(days)) });

        Assert.Equal(Enum.Parse<PaymentStatus>(expected), Domain.Documents.StatusOn(document.Content, Today));
    }

    [Fact]
    public void Documents_that_are_not_paid_have_no_payment_status()
    {
        var proforma = Valid(Invoice() with { Type = "proforma" });

        Assert.Equal(PaymentStatus.NotApplicable, Domain.Documents.StatusOn(proforma.Content, Today));
        Assert.Equal(0m, Domain.Documents.Outstanding(proforma.Content));
    }

    [Fact]
    public void Receivables_are_summed_per_currency()
    {
        var eur = Currency.Parse("EUR").ShouldSucceed();
        OpenItem[] items =
        [
            new(Pln, 100m, Today.AddDays(-1)),
            new(Pln, 50m, Today),
            new(Pln, 0m, Today.AddDays(-30)),
            new(eur, 20m, default),
        ];

        var receivables = Domain.Documents.Receivables(items, Today);

        Assert.Equal([new Receivable(eur, 20m, 0m, 0), new Receivable(Pln, 150m, 100m, 1)], receivables);
    }

    [Fact]
    public void Older_versions_are_ignored_and_same_content_only_moves_the_version()
    {
        var stored = Valid(Invoice(version: 5));

        Assert.IsType<Change<Document>.Stale>(Domain.Documents.Reconcile(stored, Valid(Invoice(version: 5))));
        Assert.IsType<Change<Document>.Unchanged>(Domain.Documents.Reconcile(stored, Valid(Invoice(version: 6))));
        Assert.IsType<Change<Document>.Updated>(Domain.Documents.Reconcile(stored, Valid(Invoice(version: 6) with { Paid = 1230m })));
    }

    [Theory]
    [InlineData("%PDF-1.7\n...", null)]
    [InlineData("", "file.empty")]
    [InlineData("<html>", "file.not-pdf")]
    public void Only_pdf_files_are_accepted(string content, string? error)
    {
        var result = Domain.Documents.CheckFile(System.Text.Encoding.ASCII.GetBytes(content));

        Assert.Equal(error, result.Match(_ => Maybe<string>.None, e => e.Code).OrNull());
    }

    [Fact]
    public void Files_are_bounded()
    {
        var content = new byte[Domain.Documents.MaxFileBytes + 1];
        "%PDF-"u8.CopyTo(content);

        Assert.Equal("file.too-large", Domain.Documents.CheckFile(content).ShouldFail().Code);
    }

    private static UnvalidatedDocumentLine Line(int number, string? sku, decimal quantity, string name = "Śruba M8") =>
        new(number, sku is null ? Maybe<string>.None : sku, name, quantity, "szt", 1.25m, Math.Round(quantity * 1.25m, 2));

    [Fact]
    public void Lines_are_validated_with_their_paths()
    {
        var valid = Valid(Invoice() with { Lines = [Line(1, " SR-M8 ", 10), Line(2, null, 1, "Transport")] });
        var errors = Domain.Documents.Validate(Invoice() with { Lines = [Line(0, "A", 1.0001m, " ") with { Unit = "", NetPrice = -1m }] }).ShouldBeInvalid();

        Assert.Equal(["SR-M8", ""], valid.Content.Lines.Select(l => l.Sku.ValueOr("")));
        Assert.Equal(["lines[0].number", "lines[0].name", "lines[0].quantity", "lines[0].unit", "lines[0].netPrice"], errors.Select(e => e.Field.ValueOr("")));
    }

    [Fact]
    public void Reordering_takes_products_with_a_positive_quantity_summed_per_sku()
    {
        var document = Valid(Invoice() with { Lines = [Line(1, "A", 10), Line(2, "B", 2.5m), Line(3, "A", 5), Line(4, null, 1, "Transport"), Line(5, "C", -3)] });

        var reorderable = Domain.Documents.Reorderable(document.Content);

        Assert.Equal([("A", 15m), ("B", 2.5m)], reorderable.Items);
        Assert.Equal(1, reorderable.Skipped);
    }

    [Fact]
    public void Changed_lines_make_an_update()
    {
        var stored = Valid(Invoice() with { Lines = [Line(1, "A", 10)] });
        var incoming = Valid(Invoice(version: 2) with { Lines = [Line(1, "A", 12)] });

        Assert.IsType<Change<Document>.Updated>(Domain.Documents.Reconcile(stored, incoming));
    }
}
