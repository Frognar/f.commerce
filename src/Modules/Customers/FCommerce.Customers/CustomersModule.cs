using FCommerce.Application.Messaging;
using FCommerce.Customers.Contracts;
using FCommerce.Customers.Import;
using FCommerce.Customers.Persistence;
using FCommerce.Customers.Queries;
using FCommerce.Infrastructure;
using FCommerce.Infrastructure.Outbox;
using FCommerce.Infrastructure.Persistence;
using FCommerce.SharedKernel.Imports;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Customers;

public static class CustomersModule
{
    public static IServiceCollection AddCustomersModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<CustomersDbContext>(CustomersDbContext.Schema);
        services.AddOutbox<CustomersDbContext>();
        services.AddMessageType<CustomerChanged>("customers.customer-changed.v1");
        services.AddMessageType<PortalContactsChanged>("customers.portal-contacts-changed.v1");

        services.AddScoped<ICommandHandler<ImportCustomers, Result<ImportReport, Error>>, ImportCustomersHandler>();
        services.AddScoped<IQueryHandler<GetCustomer, Result<CustomerDetails, Error>>, GetCustomerHandler>();
        services.AddScoped<IQueryHandler<GetCustomers, IReadOnlyList<CustomerSummary>>, GetCustomersHandler>();
        services.AddScoped<IQueryHandler<SearchCustomers, Result<CustomerPage, Error>>, SearchCustomersHandler>();
        services.AddScoped<IQueryHandler<FindCustomersByTaxId, Result<IReadOnlyList<CustomerSummary>, Error>>, FindCustomersByTaxIdHandler>();
        return services;
    }
}
