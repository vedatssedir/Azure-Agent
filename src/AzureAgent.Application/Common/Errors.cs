namespace AzureAgent.Application.Common;

/// <summary>Application errors mapped to HTTP problem details by the API.</summary>
public abstract class AppException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class NotFoundException(string what) : AppException($"{what} not found", 404);

public sealed class ForbiddenException(string message = "You do not have permission to do this") : AppException(message, 403);

public sealed class UnauthorizedException(string message = "Authentication required") : AppException(message, 401);

public sealed class ConflictException(string message) : AppException(message, 409);

public sealed class BusinessRuleException(string message) : AppException(message, 422);

/// <summary>An Azure DevOps request that failed; <see cref="Status"/> is Azure's HTTP status.</summary>
public class AzureRequestException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>Azure rejected the credential: expired, revoked or missing a scope.</summary>
    public bool IsAuthFailure => Status is 401 or 403;
}
