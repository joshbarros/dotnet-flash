using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Flash.Shared.Auth;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Staff = "Staff";
    public const string Customer = "Customer";
}

public static class Policies
{
    /// <summary>Create and change catalog data (deals).</summary>
    public const string ManageCatalog = nameof(ManageCatalog);

    /// <summary>See everyone's data and the event log.</summary>
    public const string ViewAll = nameof(ViewAll);
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? Id { get; }
    bool IsStaff { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? Id => Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;

    public bool IsStaff => Principal is not null && (Principal.IsInRole(Roles.Admin) || Principal.IsInRole(Roles.Staff));
}

/// <summary>Owner-or-staff access: the rule every per-user resource follows.</summary>
public static class OwnerAccess
{
    public static bool CanAccess(ICurrentUser user, Guid ownerId) =>
        user.IsAuthenticated && (user.IsStaff || user.Id == ownerId);
}

public static class AuthorizationExtensions
{
    /// <summary>Everything requires a signed-in user unless it says otherwise. Catalog changes and the all-data views need staff.</summary>
    public static IServiceCollection AddFlashAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.ManageCatalog, p => p.RequireRole(Roles.Admin, Roles.Staff))
            .AddPolicy(Policies.ViewAll, p => p.RequireRole(Roles.Admin, Roles.Staff));
        return services;
    }
}
