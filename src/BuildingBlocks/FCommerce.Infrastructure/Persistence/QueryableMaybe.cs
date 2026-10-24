using System.Linq.Expressions;
using FCommerce.SharedKernel.Optional;
using Microsoft.EntityFrameworkCore;

namespace FCommerce.Infrastructure.Persistence;

/// <summary>EF Core lookups that return <see cref="Maybe{T}"/> instead of <c>null</c> when nothing matches.</summary>
public static class QueryableMaybe
{
    public static async Task<Maybe<T>> FirstOrNoneAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default)
        where T : class =>
        (await query.FirstOrDefaultAsync(cancellationToken)).ToMaybe();

    public static async Task<Maybe<T>> FirstOrNoneAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        where T : class =>
        (await query.FirstOrDefaultAsync(predicate, cancellationToken)).ToMaybe();

    public static async Task<Maybe<T>> SingleOrNoneAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        where T : class =>
        (await query.SingleOrDefaultAsync(predicate, cancellationToken)).ToMaybe();

    public static async Task<Maybe<T>> FindOrNoneAsync<T>(this DbSet<T> set, object key, CancellationToken cancellationToken = default)
        where T : class =>
        (await set.FindAsync([key], cancellationToken)).ToMaybe();
}
