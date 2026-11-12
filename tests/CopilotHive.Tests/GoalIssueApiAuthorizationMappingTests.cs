using System.Net;
using System.Reflection;

using CopilotHive.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// THE HTTP MAPPING CONTRACT of the goal/issue AUTHORIZATION answer at the API boundary: a
/// <see cref="FacadeErrorKind.Forbidden"/> from either facade is rendered as HTTP 403 with the
/// SAME <c>{ error }</c> JSON body shape the other failure kinds use — never 400, never 500 — so an
/// authenticated-but-insufficient / demoted / disabled caller sees an authorization answer, while an
/// ANONYMOUS caller is still challenged with 401 by the fallback policy before any handler runs
/// (covered by <see cref="AuthenticatedModeTests"/>).
/// <para>
/// The two mappers are declared <c>private static</c> on <see cref="ApiEndpoints"/>; they are
/// exercised through reflection and EXECUTED against a real <see cref="DefaultHttpContext"/>, so the
/// assertion reads the actual status code and body the route would produce instead of a source
/// fragment.
/// </para>
/// </summary>
public sealed class GoalIssueApiAuthorizationMappingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The single private mapper declared by <see cref="ApiEndpoints"/> under that name.</summary>
    private static MethodInfo Mapper(string name)
    {
        var method = typeof(ApiEndpoints).GetMethod(
            name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(typeof(IResult), method!.ReturnType);
        return method;
    }

    /// <summary>
    /// The per-request service provider the JSON result types need (they resolve the HTTP JSON
    /// options from <see cref="HttpContext.RequestServices"/>). ONE provider is shared by every
    /// executed result in this class.
    /// </summary>
    private static readonly IServiceProvider RequestServices = BuildRequestServices();

    /// <summary>Builds the minimal provider the JSON result types resolve their options from.</summary>
    private static IServiceProvider BuildRequestServices()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new Microsoft.AspNetCore.Http.Json.JsonOptions()));
        return services.BuildServiceProvider();
    }

    /// <summary>Executes <paramref name="result"/> and returns the status code it wrote.</summary>
    private static async Task<int> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = RequestServices };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    /// <summary>
    /// THE AUTHORIZATION ANSWER IS 403 — for BOTH the goal and the issue facade — carrying the
    /// sanitized denial text in the same <c>{ error }</c> body, so it is never confused with the
    /// validation answer (400) or with an unexpected failure (500).
    /// </summary>
    [Fact]
    public async Task Forbidden_MapsTo403_WithTheSanitizedErrorBody()
    {
        foreach (var mapperName in new[] { "MapGoalFacadeError", "MapIssueFacadeError" })
        {
            var mapper = Mapper(mapperName);

            var forbidden = Assert.IsAssignableFrom<IResult>(
                mapper.Invoke(null, [FacadeErrorKind.Forbidden, "The acting account is not authorized to perform this operation."]));
            Assert.Equal(StatusCodes.Status403Forbidden, await ExecuteAsync(forbidden));

            // DISCRIMINATING PAIR: the same mapper still answers 400 for the validation kind — so the
            // 403 above comes from the authorization kind and not from a mapper that returns 403 for
            // everything.
            var badRequest = Assert.IsAssignableFrom<IResult>(
                mapper.Invoke(null, [FacadeErrorKind.BadRequest, "Invalid status 'x'."]));
            Assert.Equal(StatusCodes.Status400BadRequest, await ExecuteAsync(badRequest));

            // …and 404 / 409 for the existing kinds, unchanged.
            Assert.Equal(
                StatusCodes.Status404NotFound,
                await ExecuteAsync(Assert.IsAssignableFrom<IResult>(
                    mapper.Invoke(null, [FacadeErrorKind.NotFound, "Goal 'x' not found."]))));
            Assert.Equal(
                StatusCodes.Status409Conflict,
                await ExecuteAsync(Assert.IsAssignableFrom<IResult>(
                    mapper.Invoke(null, [FacadeErrorKind.Conflict, "A review is already in progress."]))));
        }
    }

    /// <summary>
    /// THE MAPPERS THROW ON A SUCCESS OR ON A KIND THESE ROUTES NEVER PRODUCE: a mapping that silently
    /// fell back would turn a programming error into a plausible-looking response.
    /// </summary>
    [Fact]
    public void NoneAndUnexpectedKinds_Throw()
    {
        foreach (var mapperName in new[] { "MapGoalFacadeError", "MapIssueFacadeError" })
        {
            var mapper = Mapper(mapperName);

            var none = Assert.Throws<TargetInvocationException>(
                () => mapper.Invoke(null, [FacadeErrorKind.None, null]));
            Assert.IsType<InvalidOperationException>(none.InnerException);

            var unavailable = Assert.Throws<TargetInvocationException>(
                () => mapper.Invoke(null, [FacadeErrorKind.ServiceUnavailable, "unused"]));
            Assert.IsType<InvalidOperationException>(unavailable.InnerException);
        }
    }
}
