namespace BooklyHub.Application.Common.Models;

/// <summary>
/// PAG-01: the paging bounds one reader may ask for, stated once. Four routes page over live tables and two of
/// them used to pass the caller's numbers straight into `Skip((page - 1) * pageSize)`, so `?page=0` sent
/// `OFFSET -20` to SQL Server and answered 500 for a mistake the caller made, while `?pageSize=2147483647` was
/// honoured as a request to load the whole table.
///
/// The offset is the product, so it is the site of the third defect: with `page` and `pageSize` both valid
/// individually, `(page - 1) * pageSize` overflows `int` before EF ever sees it, and the wrap lands on a negative
/// offset — which is why the two routes that already clamped still answered 500 at `?page=2147483647`. Clamping
/// the offset to `int.MaxValue` keeps the answer a page, and an empty one is the honest content of a page past
/// the end of a table.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static int Offset(int page, int pageSize)
    {
        var offset = (long)(NormalizePage(page) - 1) * NormalizePageSize(pageSize);
        return offset > int.MaxValue ? int.MaxValue : (int)offset;
    }

    /// <summary>A page below the first is the first, so a caller that lost count lands on real content.</summary>
    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    /// <summary>
    /// An out-of-range size falls back to the default rather than to the ceiling: `20` is what the two routes that
    /// already clamped have always returned for `?pageSize=5000`, and a doc that promises a ceiling of 100 is a
    /// promise about the maximum, not about what an illegal ask becomes.
    /// </summary>
    public static int NormalizePageSize(int pageSize) => pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize;
}
