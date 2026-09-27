using Azure.Core;
namespace KiteKey.AI.Azure;

/// <summary>Attaches a bearer token from an Azure credential to outgoing HTTP requests.</summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
	private readonly TokenCredential _credential;
	private readonly string _scope;

	/// <summary>Creates an auth handler using a host-supplied credential and Azure Speech scope.</summary>
	public BearerTokenHandler(TokenCredential credential, string scope = "https://cognitiveservices.azure.com/.default")
	{
		_credential = credential ?? throw new ArgumentNullException(nameof(credential));
		_scope = !string.IsNullOrWhiteSpace(scope) ? scope : throw new ArgumentException("A token scope is required.", nameof(scope));
	}

	/// <inheritdoc />
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		AccessToken token = await _credential.GetTokenAsync(new TokenRequestContext([_scope]), cancellationToken);
		request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
		return await base.SendAsync(request, cancellationToken);
	}
}
