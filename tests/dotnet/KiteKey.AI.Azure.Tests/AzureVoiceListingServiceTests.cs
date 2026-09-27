using System.Net;
using Azure.Core;
using KiteKey.AI.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KiteKey.AI.Azure.Tests;

public sealed class AzureVoiceListingServiceTests
{
    [Fact]
    public async Task ListsFiltersAndCachesVoices()
    {
        int calls = 0;
        using HttpClient client = new(new TestHandler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("/tts/cognitiveservices/voices/list?api-version=2024-11-15", request.RequestUri!.PathAndQuery);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [{"ShortName":"en-US-AriaNeural","Locale":"en-US"},{"ShortName":"fr-FR-DeniseNeural","Locale":"fr-FR"}]
                    """)
            };
        }));
        AzureVoiceListingService service = new(client, new Uri("https://example.services.ai.azure.com/"), NullLogger<AzureVoiceListingService>.Instance);

        IReadOnlyList<VoiceInfo>[] results = await Task.WhenAll(
            service.ListVoicesAsync("EN-us"),
            service.ListVoicesAsync("fr-FR"),
            service.ListVoicesAsync());

        Assert.Equal(1, calls);
        Assert.Equal("en-US-AriaNeural", Assert.Single(results[0]).ShortName);
        Assert.Equal("fr-FR-DeniseNeural", Assert.Single(results[1]).ShortName);
        Assert.Equal(2, results[2].Count);
    }

    [Fact]
    public async Task FallsBackToLegacyEndpointAndRetriesFailedRequests()
    {
        int calls = 0;
        using HttpClient client = new(new TestHandler((_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            return call switch
            {
                1 or 2 => new(HttpStatusCode.NotFound),
                3 => new(HttpStatusCode.NotFound),
                _ => new(HttpStatusCode.OK) { Content = new StringContent("""[{"ShortName":"voice"}]""") }
            };
        }));
        AzureVoiceListingService service = new(client, new Uri("https://example.cognitiveservices.azure.com"), NullLogger<AzureVoiceListingService>.Instance);

        Assert.Empty(await service.ListVoicesAsync());
        Assert.Equal("voice", Assert.Single(await service.ListVoicesAsync()).ShortName);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task BearerHandlerUsesInjectedCredentialAndScope()
    {
        TestCredential credential = new();
        using HttpClient client = new(new BearerTokenHandler(credential, "https://example/.default")
        {
            InnerHandler = new TestHandler((request, _) =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                return new(HttpStatusCode.OK);
            })
        });

        using HttpResponseMessage response = await client.GetAsync("https://example.com");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://example/.default", Assert.Single(credential.Scopes));
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request, cancellationToken));
    }

    private sealed class TestCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }
}
