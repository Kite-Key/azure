using Azure.Core;
namespace KiteKey.AI.Azure.VoiceLive;

/// <summary>
/// Singleton wrapper around the <see cref="TokenCredential"/> used by the VoiceLive client.
/// Centralizing it lets us share the underlying credential cache across requests and pre-warm
/// it at startup so the first call doesn't pay the credential bootstrap cost.
/// </summary>
public sealed class VoiceLiveCredentialProvider
{
    /// <summary>The Microsoft Entra scope required by the Voice Live API.</summary>
    public const string TokenScope = "https://ai.azure.com/.default";
    private static readonly TokenRequestContext TokenRequestContext = new([TokenScope]);

    /// <summary>Uses the host's credential, sharing its token cache across sessions.</summary>
    public VoiceLiveCredentialProvider(TokenCredential credential)
        => Credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>The credential shared with Voice Live clients.</summary>
    public TokenCredential Credential { get; }

    /// <summary>Acquires a token for the Voice Live API.</summary>
    public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
        => Credential.GetTokenAsync(TokenRequestContext, cancellationToken);

}
