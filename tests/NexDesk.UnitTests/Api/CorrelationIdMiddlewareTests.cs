using Microsoft.AspNetCore.Http;
using NexDesk.Api.Middleware;

namespace NexDesk.UnitTests.Api;

public sealed class CorrelationIdMiddlewareTests
{
    [Theory]
    [InlineData("abc-123_DEF")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public async Task Keeps_a_well_formed_incoming_id(string incoming)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = incoming;

        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        context.TraceIdentifier.ShouldBe(incoming);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("line\nbreak")]
    [InlineData("<script>")]
    public async Task Replaces_a_missing_or_unsafe_id(string incoming)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = incoming;

        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        context.TraceIdentifier.ShouldNotBe(incoming);
        Guid.TryParseExact(context.TraceIdentifier, "N", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Replaces_an_overlong_id()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = new string('a', 65);

        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        context.TraceIdentifier.Length.ShouldBe(32);
    }
}
