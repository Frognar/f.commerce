using FCommerce.Application.Messaging;
using FCommerce.Infrastructure.Persistence;
using FCommerce.Media.Contracts;
using FCommerce.Media.Persistence;
using FCommerce.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FCommerce.Media;

/// <summary>Image storage. Requires an <see cref="Application.Storage.IBlobStore"/> registration.</summary>
public static class MediaModule
{
    public static IServiceCollection AddMediaModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<MediaDbContext>(MediaDbContext.Schema);
        services.AddScoped<ICommandHandler<UploadImage, Result<UploadedImage, Error>>, UploadImageHandler>();
        services.AddScoped<IQueryHandler<FindStoredImages, IReadOnlySet<string>>, FindStoredImagesHandler>();
        services.AddScoped<IQueryHandler<OpenImage, Result<ImageContent, Error>>, OpenImageHandler>();
        return services;
    }
}
