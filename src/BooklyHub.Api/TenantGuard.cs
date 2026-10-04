using BooklyHub.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;

namespace BooklyHub.Api;

/// <summary>
/// ENV-01: the one answer a request with no resolved tenant gets. Five controllers used to return
/// <c>BadRequest(new { message })</c> — a bare <c>application/json</c> body with one lowercase field, in three
/// different wordings, while every other failure on the same server answered <c>application/problem+json</c> with
/// a correlation id in the body. Three more threw <see cref="BadHttpRequestException"/>, a type the exception
/// boundary had never mapped, so a client mistake was answered <c>500</c> with
/// "An unexpected server error occurred. Please contact support with your Correlation ID."
///
/// It throws rather than returning a result because that is the shape these controllers already use: the guard sits
/// in a helper that hands back the <see cref="Guid"/> the action needs, and the alternative is every action in
/// these files testing for null before it can use its own tenant. <see cref="ExceptionHandlingMiddleware"/> maps
/// this type to the status the request was given, so the envelope and the wording are written in exactly one place.
/// </summary>
public static class TenantGuard
{
    /// <summary>
    /// The wording for the condition "nothing resolved a tenant for this request". Deliberately not "tenant id is
    /// required": the caller often sent one — as a header or a query parameter naming a tenant that does not exist,
    /// or as a token carrying no claim — and the honest statement is about what the server ended up with.
    /// </summary>
    public const string MissingTenant = "Active tenant context is required.";

    public static Guid RequireId(ITenantContext tenantContext) => RequireId(tenantContext.TenantId, MissingTenant);

    /// <summary>
    /// For the one call site whose condition is not the same: the public availability portal takes a tenant from the
    /// request itself as well as from the context, so it can truthfully name three places to put it.
    /// </summary>
    public static Guid RequireId(Guid? resolvedTenantId, string detail)
    {
        if (!resolvedTenantId.HasValue)
        {
            throw new BadHttpRequestException(detail, StatusCodes.Status400BadRequest);
        }

        return resolvedTenantId.Value;
    }
}
