using Microsoft.EntityFrameworkCore;

namespace SentinelOps.Api.Common;

public record PagedQuery(int Page = 1, int PageSize = 20);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public static class PaginationExtensions
{
    private const int MaxPageSize = 100;

    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query, PagedQuery paging, CancellationToken ct)
    {
        var page = Math.Max(paging.Page, 1);
        var pageSize = Math.Clamp(paging.PageSize, 1, MaxPageSize);

        var totalCount = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        return new PagedResult<T>(items, page, pageSize, totalCount);
    }
}
