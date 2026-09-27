using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace KiteKey.AI.Azure;

/// <summary>Queries the Azure AI Services Speech endpoint for available TTS voices.</summary>
public sealed class AzureVoiceListingService
{
	private readonly HttpClient _client;
	private readonly Uri _endpoint;
	private readonly ILogger<AzureVoiceListingService> _logger;
	private readonly SemaphoreSlim _cacheLock = new(1, 1);
	private List<VoiceInfo>? _cachedVoices;

	/// <summary>Creates a voice listing client with caller-owned HTTP transport and an Azure Speech endpoint.</summary>
	public AzureVoiceListingService(HttpClient client, Uri endpoint, ILogger<AzureVoiceListingService> logger)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
			throw new ArgumentException("An absolute HTTPS Azure Speech endpoint is required.", nameof(endpoint));
		_endpoint = endpoint;
	}

	/// <summary>
	/// Known URI suffixes for the voices/list REST API across different Azure endpoint formats.
	/// Ordered by likelihood: .services.ai.azure.com requires the /tts prefix and api-version,
	/// while .cognitiveservices.azure.com uses the bare path.
	/// </summary>
	private static readonly string[] VoiceListSuffixes =
	[
		"/tts/cognitiveservices/voices/list?api-version=2024-11-15",
		"/cognitiveservices/voices/list",
	];

	/// <summary>List available TTS voice names, optionally filtered to a locale prefix (e.g. "en-US").</summary>
	public async Task<IReadOnlyList<VoiceInfo>> ListVoicesAsync(string? localePrefix = null, CancellationToken cancellation = default)
	{
		if (_cachedVoices is null)
		{
			await _cacheLock.WaitAsync(cancellation);
			try
			{
				if (_cachedVoices is null)
				{
					foreach (string suffix in VoiceListSuffixes)
					{
						Uri url = new(_endpoint.GetLeftPart(UriPartial.Authority) + _endpoint.AbsolutePath.TrimEnd('/') + suffix);
						using HttpResponseMessage response = await _client.GetAsync(url, cancellation);
						if (response.IsSuccessStatusCode)
						{
							_cachedVoices = await response.Content.ReadFromJsonAsync<List<VoiceInfo>>(cancellation) ?? [];
							_logger.LogDebug("Voices loaded from {Url}", url);
							break;
						}
						_logger.LogDebug("Voices endpoint {Url} returned {StatusCode}, trying next", url, response.StatusCode);
					}
				}
			}
			finally
			{
				_cacheLock.Release();
			}
		}

		IReadOnlyList<VoiceInfo> voices = _cachedVoices ?? [];
		if (string.IsNullOrEmpty(localePrefix))
			return voices;

		return voices
			.Where(v => v.Locale?.StartsWith(localePrefix, StringComparison.OrdinalIgnoreCase) == true)
			.ToList();
	}
}

/// <summary>Minimal representation of a voice from the Azure Speech voices/list API.</summary>
public sealed record VoiceInfo
{
	/// <summary>Azure's stable short name for the voice.</summary>
	[JsonPropertyName("ShortName")]
	public string ShortName { get; init; } = string.Empty;

	/// <summary>Display name of the voice.</summary>
	[JsonPropertyName("DisplayName")]
	public string DisplayName { get; init; } = string.Empty;

	/// <summary>BCP-47 locale identifier of the voice.</summary>
	[JsonPropertyName("Locale")]
	public string Locale { get; init; } = string.Empty;

	/// <summary>Gender classification provided by Azure Speech.</summary>
	[JsonPropertyName("Gender")]
	public string Gender { get; init; } = string.Empty;

	/// <summary>Azure Speech voice type.</summary>
	[JsonPropertyName("VoiceType")]
	public string VoiceType { get; init; } = string.Empty;
}
