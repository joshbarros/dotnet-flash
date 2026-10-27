using System.Text.Json;
using Flash.Shared;
using Flash.Shared.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flash.UnitTests;

public class ExceptionMiddlewareTests
{
    private static async Task<(DefaultHttpContext Context, JsonElement Body)> RunAsync(RequestDelegate next, Action<DefaultHttpContext>? setup = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        setup?.Invoke(context);
        await new ExceptionMiddleware(next, NullLogger<ExceptionMiddleware>.Instance).InvokeAsync(context);
        context.Response.Body.Position = 0;
        var body = context.Response.Body.Length == 0 ? default : (await JsonDocument.ParseAsync(context.Response.Body)).RootElement.Clone();
        return (context, body);
    }

    [Fact]
    public async Task DomainException_BecomesItsStatusAndMessage()
    {
        var (context, body) = await RunAsync(_ => throw new DomainException(409, "Already booked."));
        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal("Already booked.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnexpectedException_Is500_AndDoesNotLeakItsMessage()
    {
        var (context, body) = await RunAsync(_ => throw new InvalidOperationException("password=hunter2 at 10.0.0.5"));
        Assert.Equal(500, context.Response.StatusCode);
        Assert.DoesNotContain("hunter2", body.GetRawText());
        Assert.DoesNotContain("10.0.0.5", body.GetRawText());
    }

    [Fact]
    public async Task Problem_CarriesTheCorrelationId()
    {
        var (_, body) = await RunAsync(_ => throw new DomainException(400, "Bad."), c => c.Items[CorrelationId.Header] = "abc-123");
        Assert.Equal("abc-123", body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task NoException_PassesThroughUntouched()
    {
        var (context, _) = await RunAsync(c => { c.Response.StatusCode = 204; return Task.CompletedTask; });
        Assert.Equal(204, context.Response.StatusCode);
    }

    [Fact]
    public async Task AfterTheResponseHasStarted_TheExceptionIsRethrown()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartedResponseFeature());
        var middleware = new ExceptionMiddleware(_ => throw new DomainException(400, "late"), NullLogger<ExceptionMiddleware>.Instance);
        await Assert.ThrowsAsync<DomainException>(() => middleware.InvokeAsync(context));
    }

    private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}

public class CorrelationIdMiddlewareTests
{
    private static async Task<DefaultHttpContext> RunAsync(string? incoming)
    {
        var context = new DefaultHttpContext();
        if (incoming is not null)
            context.Request.Headers[CorrelationId.Header] = incoming;
        await new CorrelationIdMiddleware(_ => Task.CompletedTask, NullLogger<CorrelationIdMiddleware>.Instance).InvokeAsync(context);
        return context;
    }

    [Fact]
    public async Task AcceptableId_IsKeptAndEchoed()
    {
        var context = await RunAsync("req-42_A");
        Assert.Equal("req-42_A", context.Response.Headers[CorrelationId.Header]);
        Assert.Equal("req-42_A", context.Items[CorrelationId.Header]);
    }

    [Fact]
    public async Task MissingId_IsGenerated()
    {
        var context = await RunAsync(null);
        Assert.False(string.IsNullOrWhiteSpace(context.Response.Headers[CorrelationId.Header]));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("new\nline")]
    [InlineData("<script>")]
    public async Task UnsafeId_IsReplaced(string incoming)
    {
        var context = await RunAsync(incoming);
        Assert.NotEqual(incoming, context.Response.Headers[CorrelationId.Header].ToString());
    }

    [Fact]
    public async Task OverlongId_IsReplaced()
    {
        var context = await RunAsync(new string('a', 65));
        Assert.NotEqual(new string('a', 65), context.Response.Headers[CorrelationId.Header].ToString());
    }
}

public class SecurityHeadersMiddlewareTests
{
    private static async Task<IHeaderDictionary> RunAsync(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        await new SecurityHeadersMiddleware(_ => Task.CompletedTask).InvokeAsync(context);
        return context.Response.Headers;
    }

    [Fact]
    public async Task EveryResponse_GetsTheBasicHeaders()
    {
        var headers = await RunAsync("/anything");
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
    }

    [Fact]
    public async Task ApiResponses_GetAStrictContentSecurityPolicy() =>
        Assert.Contains("default-src 'none'", await GetCsp("/api/orders"));

    [Fact]
    public async Task NonApiPages_DoNotGetThePolicy_SoTheDocsPageStillRenders() =>
        Assert.Equal("", await GetCsp("/scalar/v1"));

    private static async Task<string> GetCsp(string path) => (await RunAsync(path))["Content-Security-Policy"].ToString();
}
