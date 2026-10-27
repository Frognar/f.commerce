using FCommerce.SharedKernel.Imports;
using FCommerce.Catalog.Domain;
using FCommerce.Testing;
using static FCommerce.Catalog.Tests.TestProducts;

namespace FCommerce.Catalog.Tests;

public sealed class ReconciliationTests
{
    [Fact]
    public void New_product_is_created()
    {
        var incoming = Valid();

        Assert.Equal(new Change<Product>.Created(incoming), Reconciliation.Reconcile(default, incoming));
    }

    [Fact]
    public void Newer_version_with_different_content_is_an_update()
    {
        var stored = Valid(version: 1);
        var incoming = Product.Validate(Input(version: 2) with { Unit = "kg" }).ShouldBeValid();

        Assert.Equal(new Change<Product>.Updated(stored, incoming), Reconciliation.Reconcile(stored, incoming));
    }

    [Fact]
    public void Discontinuing_a_product_is_an_update()
    {
        var stored = Valid(version: 1);
        var incoming = Product.Validate(Input(version: 2) with { Discontinued = true }).ShouldBeValid();

        Assert.Equal(ProductStatus.Discontinued, incoming.Status);
        Assert.Equal(new Change<Product>.Updated(stored, incoming), Reconciliation.Reconcile(stored, incoming));
    }

    [Fact]
    public void Deactivation_is_an_update()
    {
        var stored = Valid(version: 1);
        var incoming = Valid(version: 2) with { Status = ProductStatus.Inactive };

        Assert.IsType<Change<Product>.Updated>(Reconciliation.Reconcile(stored, incoming));
    }

    [Theory]
    [InlineData(true, false, ProductStatus.Active)]
    [InlineData(true, true, ProductStatus.Discontinued)]
    [InlineData(false, false, ProductStatus.Inactive)]
    [InlineData(false, true, ProductStatus.Inactive)]
    public void An_inactive_product_is_inactive_discontinued_or_not(bool active, bool discontinued, ProductStatus expected)
    {
        Assert.Equal(expected, Product.Validate(Input() with { Active = active, Discontinued = discontinued }).ShouldBeValid().Status);
    }

    [Fact]
    public void Newer_version_with_the_same_content_is_unchanged()
    {
        var incoming = Valid(version: 2);

        Assert.Equal(new Change<Product>.Unchanged(incoming), Reconciliation.Reconcile(Valid(version: 1), incoming));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Not_newer_version_is_stale(long incomingVersion)
    {
        var stored = Valid(version: 2);
        var incoming = Product.Validate(Input(version: incomingVersion) with { Unit = "kg" }).ShouldBeValid();

        Assert.IsType<Change<Product>.Stale>(Reconciliation.Reconcile(stored, incoming));
    }

    [Fact]
    public void Batch_validation_keeps_valid_items_and_reports_invalid_ones_by_index()
    {
        UnvalidatedProduct[] inputs = [Input("A"), Input("B") with { Unit = "" }, Input("C")];

        var batch = ImportBatch.Validate(inputs, i => i.Sku, Product.Validate);

        Assert.Equal([0, 2], batch.Valid.Select(v => v.Index));
        var rejected = Assert.Single(batch.Rejected);
        Assert.Equal((1, "B"), (rejected.Index, rejected.Key));
        Assert.Equal("unit", Assert.Single(rejected.Errors).Field);
    }

    [Fact]
    public void Products_referencing_images_that_are_not_stored_are_rejected()
    {
        UnvalidatedProduct[] inputs = [Input("A"), Input("B") with { ImageIds = [ImageB] }];
        var batch = ImportBatch.Validate(inputs, i => i.Sku, Product.Validate);

        var checkedBatch = Reconciliation.RequireStoredImages(batch, new HashSet<string> { ImageB });

        Assert.Equal([1], checkedBatch.Valid.Select(v => v.Index));
        var rejected = Assert.Single(checkedBatch.Rejected);
        Assert.Equal((0, "A"), (rejected.Index, rejected.Key));
        Assert.Equal("imageIds[0]", Assert.Single(rejected.Errors).Field);
    }

    [Fact]
    public void Batch_validation_rejects_every_occurrence_of_a_duplicated_key()
    {
        UnvalidatedProduct[] inputs = [Input("A"), Input(" A "), Input("B")];

        var batch = ImportBatch.Validate(inputs, i => i.Sku, Product.Validate);

        Assert.Equal([2], batch.Valid.Select(v => v.Index));
        Assert.Equal([0, 1], batch.Rejected.Select(r => r.Index));
        Assert.All(batch.Rejected, r => Assert.Equal("batch.duplicate-key", Assert.Single(r.Errors).Code));
    }

    [Fact]
    public void A_missing_item_of_a_batch_is_rejected()
    {
        UnvalidatedProduct[] inputs = [Input("A"), null!];

        var batch = ImportBatch.Validate(inputs, i => i.Sku, Product.Validate);

        Assert.Equal([0], batch.Valid.Select(v => v.Index));
        var rejected = Assert.Single(batch.Rejected);
        Assert.Equal(1, rejected.Index);
        rejected.Key.ShouldBeNone();
        Assert.Equal("batch.item-missing", Assert.Single(rejected.Errors).Code);
    }
}
