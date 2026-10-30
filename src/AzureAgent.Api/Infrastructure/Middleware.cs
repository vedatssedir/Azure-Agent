using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Identity;
using AzureAgent.Infrastructure.Persistence;
using AzureAgent.Infrastructure.Security;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Api.Infrastructure;

/// <summary>Maps application, validation and domain errors to RFC 7807 problem details.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, errors) = exception switch
        {
            ValidationException v => (StatusCodes.Status400BadRequest, "Validation failed",
                v.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())),
            AppException a => (a.StatusCode, a.Message, null),
            Domain.Common.DomainException d => (StatusCodes.Status409Conflict, d.Message, null),
            BadHttpRequestException b => (b.StatusCode, "Bad request", null),
            AzureRequestException { IsAuthFailure: true } z => (StatusCodes.Status502BadGateway,
                $"Azure DevOps rejected the credential (HTTP {z.Status}). Check that the PAT is valid and has the Work Items (Read & write) scope", null),
            AzureRequestException z => (StatusCodes.Status502BadGateway, z.Message, null),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", (Dictionary<string, string[]>?)null),
        };
        if (status >= 500) logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        else logger.LogInformation("Request failed with {Status}: {Message}", status, exception.Message);

        context.Response.StatusCode = status;
        var details = errors is null ? new ProblemDetails() : new ValidationProblemDetails(errors);
        details.Status = status;
        details.Title = title;
        details.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = details, Exception = exception });
    }
}

/// <summary>
/// Validates the X-Organization-Id header against the caller's memberships and exposes the
/// verified organization to the tenant context.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string Header = "X-Organization-Id";

    public async Task InvokeAsync(HttpContext context, AppDbContext db, ICurrentUser user, ICacheService cache)
    {
        if (context.Request.Headers.TryGetValue(Header, out var value) && user.UserId is { } userId)
        {
            if (!Guid.TryParse(value, out var organizationId))
            {
                await Results.Problem(statusCode: 400, title: $"{Header} must be a GUID").ExecuteAsync(context);
                return;
            }
            var cacheKey = $"membership:{userId:N}:{organizationId:N}";
            var allowed = await cache.GetAsync<bool?>(cacheKey, context.RequestAborted);
            if (allowed is null)
            {
                allowed = user.IsSystemAdmin
                    ? await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Id == organizationId, context.RequestAborted)
                    : await db.OrganizationMembers.IgnoreQueryFilters().AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId, context.RequestAborted);
                await cache.SetAsync(cacheKey, allowed, TimeSpan.FromMinutes(1), context.RequestAborted);
            }
            if (allowed != true)
            {
                await Results.Problem(statusCode: 403, title: "Not a member of this organization").ExecuteAsync(context);
                return;
            }
            context.Items[TenantContext.HttpItemKey] = organizationId;
        }
        using (context.RequestServices.GetRequiredService<ILogger<TenantResolutionMiddleware>>()
                   .BeginScope(new Dictionary<string, object?> { ["UserId"] = user.UserId, ["OrganizationId"] = context.Items[TenantContext.HttpItemKey] }))
        {
            await next(context);
        }
    }
}

public static class HttpContextExtensions
{
    public static string? ClientIp(this HttpContext context) => context.Connection.RemoteIpAddress?.ToString();
}

public static class OrganizationRoleExtensions
{
    public static bool IsAdmin(this OrganizationRole role) => role is OrganizationRole.Admin or OrganizationRole.Owner;
}
