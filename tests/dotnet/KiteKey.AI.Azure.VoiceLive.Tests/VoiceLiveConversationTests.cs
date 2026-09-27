using Azure.Core;
using Azure.AI.VoiceLive;
using KiteKey.AI.Azure.VoiceLive;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KiteKey.AI.Azure.VoiceLive.Tests;

public sealed class VoiceLiveConversationTests
{
    private static readonly Uri Endpoint = new("https://example.services.ai.azure.com/");
    private static readonly VoiceSessionSettings Defaults = new()
    {
        Voice = "en-US-AriaNeural",
        VadThreshold = 0.5f,
        PrefixPaddingMs = 300,
        SilenceDurationMs = 500
    };

    [Fact]
    public async Task CredentialProviderRequestsVoiceLiveScope()
    {
        TestCredential credential = new();
        VoiceLiveCredentialProvider provider = new(credential);

        AccessToken token = await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal("test-token", token.Token);
        Assert.Equal(VoiceLiveCredentialProvider.TokenScope, Assert.Single(credential.Scopes));
        Assert.Same(credential, provider.Credential);
    }

    [Fact]
    public void SessionOverridesKeepUnspecifiedDefaults()
    {
        VoiceSessionSettings resolved = new VoiceSessionSettings { Voice = "fr-FR-DeniseNeural" }.WithDefaults(Defaults);
        Assert.Equal("fr-FR-DeniseNeural", resolved.Voice);
        Assert.Equal(Defaults.VadThreshold, resolved.VadThreshold);
        Assert.Equal(Defaults.PrefixPaddingMs, resolved.PrefixPaddingMs);
    }

    [Fact]
    public async Task MissingAgentConfigurationFailsBeforeConnecting()
    {
        using VoiceLiveConversationService service = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync());
    }

    [Fact]
    public async Task DisposedConversationFailsBeforeConnecting()
    {
        VoiceLiveConversationService service = CreateService();
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync());
    }

    [Fact]
    public void InvalidVoiceDefaultsFailAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => CreateService(Defaults with { VadThreshold = 2 }));
        Assert.Throws<ArgumentException>(() => CreateService(Defaults with { Voice = null }));
    }

    [Fact]
    public void EphemeralOptionsIncludeInstructionsToolsAndVoiceOverrides()
    {
        using VoiceLiveConversationService service = CreateService();
        VoiceLiveSessionOptions options = service.CreateSessionOptions(new VoiceSessionSettings
        {
            VadThreshold = 0.8f,
            Ephemeral = new EphemeralAgent("model", "Be helpful",
                [new EphemeralFunctionTool("lookup", "Find a record", """{"type":"object"}""")])
        });

        Assert.Equal("Be helpful", options.Instructions);
        Assert.Single(options.Tools);
        Assert.Equal(0.8f, Assert.IsType<ServerVadTurnDetection>(options.TurnDetection).Threshold);
        Assert.IsType<AzureStandardVoice>(options.Voice);
        Assert.Contains(InteractionModality.Audio, options.Modalities);
        Assert.Contains(InteractionModality.Text, options.Modalities);
    }

    private static VoiceLiveConversationService CreateService(VoiceSessionSettings? defaults = null)
        => new(Endpoint, new VoiceLiveCredentialProvider(new TestCredential()),
            NullLogger<VoiceLiveConversationService>.Instance, defaults ?? Defaults);

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
