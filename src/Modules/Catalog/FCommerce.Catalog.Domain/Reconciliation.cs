using FCommerce.SharedKernel.Collections;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Results;

namespace FCommerce.Catalog.Domain;

/// <summary>Catalog-specific parts of the import (see <see cref="ImportBatch"/> for the shared pattern).</summary>
public static class Reconciliation
{
    /// <summary>
    /// Contextual check: every referenced image must already be in the media store (<paramref name="storedImages"/>).
    /// Products referencing missing images move to the rejected items.
    /// </summary>
    public static ValidatedBatch<Product> RequireStoredImages(ValidatedBatch<Product> batch, IReadOnlySet<string> storedImages) =>
        batch.Require(
            product => product.Content.Images
                .Select((image, position) => (image, position))
                .Where(i => !storedImages.Contains(i.image.Value))
                .Select(i => Error.Validation("image.not-uploaded", $"Image '{i.image}' has not been uploaded.") with { Field = $"imageIds[{i.position}]" })
                .ToEquatableArray(),
            product => product.Sku.Value);

    public static Change<Product> Reconcile(Product? stored, Product incoming) =>
        ImportBatch.Reconcile(stored, incoming, p => p.Version, (a, b) => a.Content == b.Content && a.Active == b.Active);

    public static Change<Category> Reconcile(Category? stored, Category incoming) =>
        ImportBatch.Reconcile(stored, incoming, c => c.Version, (a, b) => a.Content == b.Content && a.Active == b.Active);
}
