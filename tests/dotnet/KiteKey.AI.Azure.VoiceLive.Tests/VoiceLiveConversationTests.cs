using Azure.Core;
using Azure.AI.VoiceLive;
using KiteKey.AI.Abstractions.Voice;
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

    [Fact]
    public async Task AssistantPublishesProviderNeutralTranscriptsAndFunctionResults()
    {
        TestAudioClient audio = new() { ConversationId = "conversation-1" };
        using VoiceLiveConversationService service = CreateService();
        using VoiceLiveVoiceAssistant assistant = new(service,
            NullLogger<VoiceLiveVoiceAssistant>.Instance,
            (name, arguments, id, _) =>
            {
                Assert.Equal("lookup", name);
                Assert.Equal("""{"key":1}""", arguments);
                Assert.Equal("conversation-1", id);
                return Task.FromResult<VoiceFunctionResult?>(new("""{"found":true}""", EndConversation: true));
            });

        await VoiceLiveVoiceAssistant.PublishTranscriptAsync(audio, "user", "msg-1", "Hello", false, CancellationToken.None);
        VoiceFunctionResult result = await assistant.ResolveFunctionAsync(
            "call-1", "lookup", """{"key":1}""", audio, CancellationToken.None);

        Assert.True(result.EndConversation);
        Assert.Equal("msg-1", audio.Transcripts[0].MessageId);
        Assert.Equal("user", audio.Transcripts[0].Speaker);
        Assert.Equal("call-1", audio.Transcripts[1].MessageId);
        Assert.Equal("lookup", audio.Transcripts[1].ToolCall?.Name);
        Assert.Equal("""{"found":true}""", audio.Transcripts[1].ToolCall?.Output);
    }

    [Fact]
    public async Task AssistantRejectsMissingFunctionOutputRatherThanLeavingToolCallPending()
    {
        TestAudioClient audio = new();
        using VoiceLiveVoiceAssistant assistant = new(CreateService(),
            NullLogger<VoiceLiveVoiceAssistant>.Instance,
            (_, _, _, _) => Task.FromResult<VoiceFunctionResult?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => assistant.ResolveFunctionAsync("call", "lookup", "{}", audio, CancellationToken.None));
        Assert.Null(Assert.Single(audio.Transcripts).ToolCall?.Output);
    }

    [Fact]
    public async Task AssistantSuppressesPlaybackOnBargeIn()
    {
        TestAudioClient audio = new() { IsHumanSpeaking = true };
        await VoiceLiveVoiceAssistant.ForwardAudioAsync(audio, [1, 2], true, CancellationToken.None);
        Assert.Empty(audio.Audio);

        await VoiceLiveVoiceAssistant.ForwardAudioAsync(audio, [3], false, CancellationToken.None);
        Assert.Equal([3], Assert.Single(audio.Audio));
        Assert.True(audio.IsModelSpeaking);
    }

    [Fact]
    public async Task AssistantValidatesHostAudioBeforeConnecting()
    {
        using VoiceLiveVoiceAssistant assistant = new(CreateService(), NullLogger<VoiceLiveVoiceAssistant>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => assistant.StartConversationAsync(null!, true));
    }

    [Fact]
    public async Task AssistantSurfacesMissingAgentBeforeOpeningAudioStream()
    {
        TestAudioClient audio = new();
        using VoiceLiveVoiceAssistant assistant = new(CreateService(), NullLogger<VoiceLiveVoiceAssistant>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => assistant.StartConversationAsync(audio, true));
        Assert.Equal("connecting", Assert.Single(audio.Statuses));
        Assert.False(audio.StreamOpened);
    }

    private static VoiceLiveConversationService CreateService(VoiceSessionSettings? defaults = null)
        => new(Endpoint, new VoiceLiveCredentialProvider(new TestCredential()),
            NullLogger<VoiceLiveConversationService>.Instance, defaults ?? Defaults);

    private sealed class TestAudioClient : IVoiceAudioClient
    {
        public string? ConversationId { get; set; }
        public bool IsHumanSpeaking { get; set; }
        public bool IsModelSpeaking { get; set; }
        public List<VoiceTranscript> Transcripts { get; } = [];
        public List<byte[]> Audio { get; } = [];
        public List<string> Statuses { get; } = [];
        public bool StreamOpened { get; private set; }
        public Stream ReceiveAudioStream()
        {
            StreamOpened = true;
            return Stream.Null;
        }
        public Task SendAudioAsync(byte[] audio, CancellationToken cancellationToken)
        {
            Audio.Add(audio);
            return Task.CompletedTask;
        }
        public Task ClearPlaybackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendTranscriptAsync(VoiceTranscript transcript, CancellationToken cancellationToken)
        {
            Transcripts.Add(transcript);
            return Task.CompletedTask;
        }
        public Task SendStatusAsync(string status, CancellationToken cancellationToken)
        {
            Statuses.Add(status);
            return Task.CompletedTask;
        }
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
