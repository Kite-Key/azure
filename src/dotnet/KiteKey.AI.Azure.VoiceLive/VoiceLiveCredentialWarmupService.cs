using Azure.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KiteKey.AI.Azure.VoiceLive;

/// <summary>
/// Pre-warms the VoiceLive <see cref="TokenCredential"/> at startup so the first inbound call
/// doesn't incur the credential bootstrap + token-acquisition latency.
/// </summary>
public sealed class VoiceLiveCredentialWarmupService(
    VoiceLiveCredentialProvider credentialProvider,
    ILogger<VoiceLiveCredentialWarmupService> logger) : BackgroundService
{
    private readonly VoiceLiveCredentialProvider _credentialProvider = credentialProvider;
    private readonly ILogger<VoiceLiveCredentialWarmupService> _logger = logger;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            AccessToken token = await _credentialProvider.GetTokenAsync(stoppingToken);
            _logger.LogInformation(
                "VoiceLive credential pre-warmed; token expires at {ExpiresOn:O}",
                token.ExpiresOn);
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
        {
            // Shutdown during warmup - nothing to do.
        }
        catch(Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-warm VoiceLive credential; first call will pay the cost instead");
        }
    }
}
