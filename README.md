# KiteKey.AI.Azure

Two independently packable .NET 10 libraries for Azure AI integrations, licensed under MIT:

| Package | Purpose |
| --- | --- |
| `KiteKey.AI.Azure` | Azure Speech voice discovery and credential-based HTTP bearer authentication |
| `KiteKey.AI.Azure.VoiceLive` | Voice Live bidirectional assistant, injectable audio/function transport, credential reuse/warmup, and agent or ephemeral-model sessions |

The host owns credentials and configuration; neither library reads app settings, creates credentials, nor depends on Delphinium domain entities. For example:

```csharp
using Azure.Identity;
using KiteKey.AI.Azure;
using KiteKey.AI.Azure.VoiceLive;
using Microsoft.Extensions.Logging.Abstractions;

var credential = new DefaultAzureCredential();
using var client = new HttpClient(new BearerTokenHandler(credential)
{
    InnerHandler = new HttpClientHandler()
});
var endpoint = new Uri("https://YOUR_RESOURCE.services.ai.azure.com/");
var voices = new AzureVoiceListingService(client, endpoint, NullLogger<AzureVoiceListingService>.Instance);
var available = await voices.ListVoicesAsync("en-US");

var provider = new VoiceLiveCredentialProvider(credential);
using var conversation = new VoiceLiveConversationService(
    endpoint, provider, NullLogger<VoiceLiveConversationService>.Instance,
    new VoiceSessionSettings
    {
        Voice = "en-US-AriaNeural",
        VadThreshold = 0.5f,
        PrefixPaddingMs = 300,
        SilenceDurationMs = 500
    },
    projectName: "YOUR_PROJECT",
    defaultAgentId: "YOUR_AGENT_ID");
using var assistant = new VoiceLiveVoiceAssistant(
    conversation,
    NullLogger<VoiceLiveVoiceAssistant>.Instance,
    async (name, arguments, conversationId, cancellation) =>
    {
        // Route to your own function executor. Return null only for unhandled calls.
        string json = await ExecuteFunctionAsync(name, arguments, conversationId, cancellation);
        return new VoiceFunctionResult(json, EndConversation: name == "stop_conversation");
    });
// await assistant.StartConversationAsync(yourAudioClient, allowInterrupts: true, cancellationToken: cancellationToken);
```

`yourAudioClient` implements `IVoiceAudioClient`, supplying a readable microphone stream, output audio, playback clearing, status and transcript delivery. The sample's `ExecuteFunctionAsync` is a host function, not part of these packages. The host owns and disposes its audio transport; the assistant owns its single conversation service. The host can register `VoiceLiveCredentialWarmupService` after registering its `VoiceLiveCredentialProvider` singleton. Ephemeral sessions instead pass `VoiceSessionSettings` with an `EphemeralAgent` containing model ID, instructions and optional function tools; an agent ID/project is then unnecessary.

Run `dotnet test KiteKey.AI.Azure.sln` and `dotnet pack KiteKey.AI.Azure.sln --configuration Release`; only `src/dotnet` projects are packable. See [architecture and extraction scope](docs/architecture.md).
