using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Minicloud.Common.Data;

public static class DateTimeOffsetQuerying
{
    public static async Task<List<T>> ToDateTimeOffsetOrderedListAsync<T>(
        this IQueryable<T> query,
        DbContext db,
        Expression<Func<T, DateTimeOffset>> serverKeySelector,
        Func<T, DateTimeOffset> clientKeySelector,
        bool descending,
        int? take,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlite())
        {
            var items = await query.ToListAsync(cancellationToken);
            var orderedItems = descending
                ? items.OrderByDescending(clientKeySelector)
                : items.OrderBy(clientKeySelector);

            return take is null ? orderedItems.ToList() : orderedItems.Take(take.Value).ToList();
        }

        var orderedQuery = descending
            ? query.OrderByDescending(serverKeySelector)
            : query.OrderBy(serverKeySelector);

        if (take is not null)
        {
            orderedQuery = (IOrderedQueryable<T>)orderedQuery.Take(take.Value);
        }

        return await orderedQuery.ToListAsync(cancellationToken);
    }
}
