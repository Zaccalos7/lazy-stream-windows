using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Orbis.Stream.Core.Data;

public sealed record SortOrder(string Property, bool Descending);

/// <summary>Port of Spring's <c>Pageable</c> resolution (page, size, sort).</summary>
public sealed record PageRequest(int Page, int Size, IReadOnlyList<SortOrder> Sorts)
{
    public const int MaxSize = 2000;

    public static PageRequest Default(int size, string sortProperty, bool descending) =>
        new(0, size, [new SortOrder(sortProperty, descending)]);

    public static PageRequest Parse(IQueryCollection query, int defaultSize, string defaultSortProperty, bool defaultDescending)
    {
        var page = 0;
        var size = defaultSize;
        var sorts = new List<SortOrder>();

        if (query.TryGetValue("page", out var pageValues) && int.TryParse(First(pageValues), out var parsedPage))
        {
            page = Math.Max(parsedPage, 0);
        }

        if (query.TryGetValue("size", out var sizeValues) && int.TryParse(First(sizeValues), out var parsedSize))
        {
            size = Math.Clamp(parsedSize, 1, MaxSize);
        }

        if (query.TryGetValue("sort", out var sortValues))
        {
            string? pending = null;
            foreach (var value in sortValues)
            {
                if (value is null)
                {
                    continue;
                }

                foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (token.Equals("asc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pending is not null)
                        {
                            sorts.Add(new SortOrder(pending, false));
                            pending = null;
                        }

                        continue;
                    }

                    if (token.Equals("desc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pending is not null)
                        {
                            sorts.Add(new SortOrder(pending, true));
                            pending = null;
                        }

                        continue;
                    }

                    if (pending is not null)
                    {
                        sorts.Add(new SortOrder(pending, false));
                    }

                    pending = token;
                }
            }

            if (pending is not null)
            {
                sorts.Add(new SortOrder(pending, false));
            }
        }

        if (sorts.Count == 0)
        {
            sorts.Add(new SortOrder(defaultSortProperty, defaultDescending));
        }

        return new PageRequest(page, size, sorts);
    }

    private static string? First(IEnumerable<string>? values) => values?.FirstOrDefault();
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, long TotalElements)
{
    public int TotalPages => Size <= 0 ? 0 : (int)Math.Ceiling(TotalElements / (double)Size);
}

/// <summary>Serialization shape of <c>org.springframework.data.domain.PageImpl</c> with
/// <c>PageSerializationMode.VIA_DTO</c>: the React build reads <c>content</c>, <c>page.number</c>
/// and <c>page.totalPages</c>.</summary>
public sealed record SpringPage<T>(IReadOnlyList<T> Content, SpringPageMetadata Page);

public sealed record SpringPageMetadata(
    int Size,
    int Number,
    long TotalElements,
    int TotalPages,
    int NumberOfElements,
    bool First,
    bool Last,
    bool Empty,
    SpringPageSort Sort);

public sealed record SpringPageSort(bool Sorted, bool Unsorted, bool Empty, IReadOnlyList<SpringSortDescriptor> Sort);

public sealed record SpringSortDescriptor(string Property, string Direction, bool IgnoreCase);

public static class SpringPageFactory
{
    public static SpringPage<T> Create<T>(PagedResult<T> result, IReadOnlyList<SortOrder> sorts)
    {
        var totalPages = result.TotalPages;
        var numberOfElements = result.Items.Count;

        return new SpringPage<T>(
            result.Items,
            new SpringPageMetadata(
                result.Size,
                result.Page,
                result.TotalElements,
                totalPages,
                numberOfElements,
                result.Page == 0,
                totalPages <= 0 || result.Page >= totalPages - 1,
                numberOfElements == 0,
                new SpringPageSort(
                    sorts.Count > 0,
                    sorts.Count == 0,
                    sorts.Count == 0,
                    sorts.Select(sort => new SpringSortDescriptor(
                        sort.Property,
                        sort.Descending ? "DESC" : "ASC",
                        false)).ToList())));
    }
}
