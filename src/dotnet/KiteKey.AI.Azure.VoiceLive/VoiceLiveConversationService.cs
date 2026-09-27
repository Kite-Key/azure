using Azure.AI.VoiceLive;
using Azure.Core;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace KiteKey.AI.Azure.VoiceLive;

/// <summary>Creates and owns a single Azure Voice Live conversation session.</summary>
public sealed class VoiceLiveConversationService : IDisposable
{
	private readonly VoiceSessionSettings _defaults;
	private readonly string? _projectName;
	private readonly string? _defaultAgentId;
	private readonly TokenCredential _credential;
	private readonly ILogger<VoiceLiveConversationService> _logger;
	private readonly Lazy<VoiceLiveClient> _clientFactory;

	private VoiceLiveSession? _session;
	private bool _disposed;

	/// <summary>Creates a conversation from an HTTPS endpoint, host-managed credential, and voice defaults.</summary>
	public VoiceLiveConversationService(
		Uri endpoint,
		VoiceLiveCredentialProvider credentialProvider,
		ILogger<VoiceLiveConversationService> logger,
		VoiceSessionSettings defaults,
		string? projectName = null,
		string? defaultAgentId = null)
	{
		if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
			throw new ArgumentException("An absolute HTTPS Voice Live endpoint is required.", nameof(endpoint));
		_defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
		_credential = (credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider))).Credential;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_projectName = projectName;
		_defaultAgentId = defaultAgentId;
		_ = CreateSessionOptions(defaults);

		_clientFactory = new Lazy<VoiceLiveClient>(
			() => new VoiceLiveClient(endpoint, _credential),
			LazyThreadSafetyMode.ExecutionAndPublication);
	}

	/// <summary>Start the voice assistant session.</summary>
	/// <param name="assistantId">Optional override for the default agent identifier. Ignored for ephemeral model sessions.</param>
	/// <param name="voiceSettings">Optional per-session voice/VAD/instruction/tool overrides.</param>
	/// <param name="cancellationToken">Cancellation token for stopping the session.</param>
	public async Task<VoiceLiveSession> StartAsync(string? assistantId = null, VoiceSessionSettings? voiceSettings = null, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_session is not null)
			throw new InvalidOperationException("This conversation already has an active session.");
		Stopwatch stopwatch = Stopwatch.StartNew();

		bool useModelSession = voiceSettings?.Ephemeral is not null;
		SessionTarget target;
		string targetDescription;

		if(useModelSession)
		{
			EphemeralAgent ephemeral = voiceSettings!.Ephemeral!;
			if (string.IsNullOrWhiteSpace(ephemeral.ModelId))
				throw new ArgumentException("An ephemeral model ID is required.", nameof(voiceSettings));
			target = SessionTarget.FromModel(ephemeral.ModelId);
			targetDescription = $"model {ephemeral.ModelId}";
		}
		else
		{
			string? targetAgentId = string.IsNullOrWhiteSpace(assistantId) ? _defaultAgentId : assistantId;
			if (string.IsNullOrWhiteSpace(targetAgentId) || string.IsNullOrWhiteSpace(_projectName))
				throw new ArgumentException("Agent sessions require an agent ID and project name.");
			AgentSessionConfig agentConfig = new(targetAgentId, _projectName);
			target = SessionTarget.FromAgent(agentConfig);
			targetDescription = $"agent {targetAgentId} from project {_projectName}";
		}

		_logger.LogInformation("Connecting to VoiceLive API with {Target}", targetDescription);

		VoiceLiveClient client = _clientFactory.Value;
		_logger.LogInformation("VoiceLive startup: client ready after {ElapsedMilliseconds} ms", stopwatch.ElapsedMilliseconds);

		VoiceLiveSessionOptions sessionOptions = CreateSessionOptions(voiceSettings);
		_logger.LogInformation("VoiceLive startup: session options built after {ElapsedMilliseconds} ms", stopwatch.ElapsedMilliseconds);

		_session = await client.StartSessionAsync(target, sessionOptions, cancellationToken);
		_logger.LogInformation("VoiceLive startup: StartSessionAsync completed after {ElapsedMilliseconds} ms", stopwatch.ElapsedMilliseconds);

		_logger.LogInformation("Voice assistant ready! Start speaking...");
		_logger.LogInformation("🎙 VOICE ASSISTANT READY");
		_logger.LogInformation("Start speaking to begin conversation");

		return _session;
	}

	/// <summary>Create session options for agent-based voice conversation.</summary>
	internal VoiceLiveSessionOptions CreateSessionOptions(VoiceSessionSettings? settings = null)
	{
		settings = (settings ?? new VoiceSessionSettings()).WithDefaults(_defaults);
		if (string.IsNullOrWhiteSpace(settings.Voice) || settings.VadThreshold is not >= 0 or > 1
			|| settings.PrefixPaddingMs is null or < 0 || settings.SilenceDurationMs is null or < 0)
			throw new ArgumentException("Voice, VAD threshold (0 to 1), padding, and silence duration must be valid.", nameof(settings));

		AzureStandardVoice azureVoice = new(settings.Voice!);

		ServerVadTurnDetection turnDetectionConfig = new()
		{
			Threshold = settings.VadThreshold!.Value,
			PrefixPadding = TimeSpan.FromMilliseconds(settings.PrefixPaddingMs!.Value),
			SilenceDuration = TimeSpan.FromMilliseconds(settings.SilenceDurationMs!.Value)
		};

		VoiceLiveSessionOptions sessionOptions = new()
		{
			InputAudioEchoCancellation = new AudioEchoCancellation(),
			Voice = azureVoice,
			InputAudioFormat = InputAudioFormat.Pcm16,
			OutputAudioFormat = OutputAudioFormat.Pcm16,
			TurnDetection = turnDetectionConfig
		};

		if(!string.IsNullOrWhiteSpace(settings.Ephemeral?.Instructions))
			sessionOptions.Instructions = settings.Ephemeral.Instructions;

		if(settings.Ephemeral?.Tools is { Count: > 0 } tools)
		{
			foreach(EphemeralFunctionTool tool in tools)
			{
				sessionOptions.Tools.Add(new VoiceLiveFunctionDefinition(tool.Name)
				{
					Description = tool.Description,
					Parameters = BinaryData.FromString(string.IsNullOrWhiteSpace(tool.ParameterSchema) ? "{}" : tool.ParameterSchema)
				});
			}
		}

		sessionOptions.Modalities.Clear();
		sessionOptions.Modalities.Add(InteractionModality.Text);
		sessionOptions.Modalities.Add(InteractionModality.Audio);

		return sessionOptions;
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if(_disposed)
			return;

		_session?.Dispose();
		_disposed = true;
	}
}
