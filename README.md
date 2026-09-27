# KiteKey.AI.Azure

Two independently packable .NET 10 libraries for Azure AI integrations, licensed under MIT:

| Package | Purpose |
| --- | --- |
| `KiteKey.AI.Azure` | Azure Speech voice discovery and credential-based HTTP bearer authentication |
| `KiteKey.AI.Azure.VoiceLive` | Voice Live credential reuse/warmup, agent or ephemeral-model sessions, and voice/VAD overrides |

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
// await conversation.StartAsync(cancellationToken: cancellationToken);
```

The host can register `VoiceLiveCredentialWarmupService` as a hosted service after registering its `VoiceLiveCredentialProvider` singleton. Ephemeral sessions instead pass `VoiceSessionSettings` with an `EphemeralAgent` containing model ID, instructions and optional function tools; an agent ID/project is then unnecessary. One conversation service instance manages one Voice Live session and must be disposed when that session ends.

Run `dotnet test KiteKey.AI.Azure.sln` and `dotnet pack KiteKey.AI.Azure.sln --configuration Release`; only `src/dotnet` projects are packable. See [architecture and extraction scope](docs/architecture.md).
