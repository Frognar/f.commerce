using FCommerce.SharedKernel.Messaging;

namespace FCommerce.Documents.Contracts;

/// <summary>
/// A document was imported or its content changed. Published as "documents.document-changed.v1"; ordering links
/// documents to the orders they name.
/// </summary>
/// <param name="Type">"invoice", "correction-invoice", "delivery-note" or "proforma".</param>
/// <param name="OrderNumbers">The orders the document names (platform or ERP numbers).</param>
public sealed record DocumentChanged(string Id, string CustomerCode, string Type, string Number, DateOnly IssueDate, IReadOnlyList<string> OrderNumbers) : IIntegrationEvent;
