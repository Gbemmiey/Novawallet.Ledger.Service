namespace NovaWallet.Application.Dto
{
    /// <summary>
    /// Encapsulates a paginated collection response along with pagination metadata.
    /// </summary>
    /// <typeparam name="T">The underlying payload entity type in the collection.</typeparam>
    public class PagedResponse<T>
    {
        /// <summary>
        /// Gets the collection of elements for the current page slice.
        /// </summary>
        public List<T> Items { get; }

        /// <summary>
        /// Gets the current active page number (1-indexed).
        /// </summary>
        public int PageNumber { get; }

        /// <summary>
        /// Gets the maximum item capacity per page.
        /// </summary>
        public int PageSize { get; }

        /// <summary>
        /// Gets total calculated page count based on total items and page size.
        /// </summary>
        public int TotalPages { get; }

        /// <summary>
        /// Gets overall dataset total record count.
        /// </summary>
        public int TotalCount { get; }

        /// <summary>
        /// Gets a value indicating whether a prior page exists.
        /// </summary>
        public bool HasPreviousPage => PageNumber > 1;

        /// <summary>
        /// Gets a value indicating whether a subsequent page exists.
        /// </summary>
        public bool HasNextPage => PageNumber < TotalPages;

        /// <summary>
        /// Initializes a new instance of the <see cref="PagedResponse{T}"/> class.
        /// Computes total pagination attributes while enforcing minimal valid boundaries.
        /// </summary>
        /// <param name="items">The items slice for this current page.</param>
        /// <param name="count">The overall total count across all pages.</param>
        /// <param name="pageNumber">The active page index requested.</param>
        /// <param name="pageSize">The item count capacity limit configured per page.</param>
        public PagedResponse(List<T> items, int count, int pageNumber, int pageSize)
        {
            PageNumber = pageNumber < 1 ? 1 : pageNumber;
            PageSize = pageSize < 1 ? 10 : pageSize;
            TotalCount = count;
            TotalPages = (int)Math.Ceiling(count / (double)PageSize);
            Items = items;
        }
    }
}