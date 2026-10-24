using FCommerce.Application.Messaging;
using FCommerce.Documents.Contracts;
using FCommerce.Documents.Persistence;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using FCommerce.SharedKernel.Contracts;

namespace FCommerce.Documents;

public sealed class DocumentsOptions
{
    /// <summary>IANA time zone of the seller: a document becomes overdue at midnight there.</summary>
    public string TimeZone { get; set; } = "Europe/Warsaw";
}

/// <summary>Customer documents from the ERP. Files are private blobs; requires an <see cref="Application.Storage.IBlobStore"/> registration.</summary>
public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services, DocumentsOptions options)
    {
        services.AddModuleDbContext<DocumentsDbContext>(DocumentsDbContext.Schema);
        services.AddSingleton(options);
        services.AddSingleton<DocumentsClock>();

        services.AddScoped<ICommandHandler<ImportDocuments, Result<ImportReport, Error>>, ImportDocumentsHandler>();
        services.AddScoped<ICommandHandler<StoreDocumentFile, Result<Unit, Error>>, StoreDocumentFileHandler>();
        services.AddScoped<IQueryHandler<GetCustomerDocuments, Result<DocumentPage, Error>>, GetCustomerDocumentsHandler>();
        services.AddScoped<IQueryHandler<GetReceivables, IReadOnlyList<ReceivableView>>, GetReceivablesHandler>();
        services.AddScoped<IQueryHandler<OpenDocumentFile, Result<DocumentFile, Error>>, OpenDocumentFileHandler>();
        return services;
    }
}
