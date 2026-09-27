using System.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Azure.AI.VoiceLive;
using Microsoft.Extensions.Logging;

namespace KiteKey.AI.Azure.VoiceLive;

/// <summary>Runs a bidirectional Azure Voice Live session against a host-owned audio transport.</summary>
public sealed class VoiceLiveVoiceAssistant(
	VoiceLiveConversationService conversationService,
	ILogger<VoiceLiveVoiceAssistant> logger,
	VoiceFunctionHandler? functionHandler = null) : IDisposable
{
	private readonly VoiceLiveConversationService _conversationService = conversationService ?? throw new ArgumentNullException(nameof(conversationService));
	private readonly ILogger<VoiceLiveVoiceAssistant> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
	private readonly VoiceFunctionHandler? _functionHandler = functionHandler;
	private readonly StringBuilder _agentTranscriptBuilder = new();
	private readonly StringBuilder _userTranscriptBuilder = new();
	private const string AgentSpeaker = "agent";
	private const string HumanSpeaker = "user";
	private const string ToolSpeaker = "tool";

	private IVoiceAudioClient? _audioClient;
	private VoiceLiveSession? _session;
	private bool _responseActive;
	private bool _canCancelResponse;
	private bool _conversationStarted;
	private bool _allowInterrupts;
	private CancellationTokenSource? _conversationCts;
	private readonly Stopwatch _startupStopwatch = new();
	private bool _disposed;
	private int _started;

	/// <summary>Disposes the owned conversation, not the host-provided audio transport.</summary>
	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_conversationCts?.Cancel();
		_conversationService.Dispose();
	}

	/// <summary>Connects a host audio transport to one Voice Live agent or ephemeral-model session.</summary>
	/// <remarks>Each assistant instance can start only one session. The caller retains ownership of the audio transport.</remarks>
	public async Task StartConversationAsync(
		IVoiceAudioClient audioClient,
		bool allowInterrupts,
		string? assistantId = null,
		VoiceSessionSettings? voiceSettings = null,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(audioClient);
		if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
			throw new InvalidOperationException("This assistant already started a conversation.");

		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_conversationCts = linked;
		_audioClient = audioClient;
		_allowInterrupts = allowInterrupts;
		_startupStopwatch.Restart();
		try
		{
			await audioClient.SendStatusAsync("connecting", linked.Token);
			_session = await _conversationService.StartAsync(assistantId, voiceSettings, linked.Token);
			_logger.LogInformation("VoiceLive startup: session started after {ElapsedMilliseconds} ms", _startupStopwatch.ElapsedMilliseconds);

			await audioClient.SendStatusAsync("ready", linked.Token);
			_logger.LogInformation("VoiceLive startup: browser/call marked ready after {ElapsedMilliseconds} ms", _startupStopwatch.ElapsedMilliseconds);

			Task sendAudioTask = _session.SendInputAudioAsync(audioClient.ReceiveAudioStream(), linked.Token);
			Task eventLoopTask = RunConversation(linked.Token);
			Task firstCompleted = await Task.WhenAny(sendAudioTask, eventLoopTask);
			try
			{
				await firstCompleted;
			}
			finally
			{
				await linked.CancelAsync();
				try
				{
					await Task.WhenAll(sendAudioTask, eventLoopTask);
				}
				catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
			}
		}
		finally
		{
			_conversationCts = null;
			_conversationService.Dispose();
		}
	}

	/// <summary>
	/// Process events from the VoiceLive session.
	/// </summary>
	private async Task RunConversation(CancellationToken cancellationToken)
	{
		try
		{
			await foreach(SessionUpdate serverEvent in _session!.GetUpdatesAsync(cancellationToken))
				await HandleSessionUpdateAsync(serverEvent, cancellationToken);
		}
		catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
		{
			_logger.LogInformation("Event processing cancelled");
		}
		catch(ObjectDisposedException ex) when(cancellationToken.IsCancellationRequested)
		{
			// Underlying VoiceLive websocket was disposed because the human side hung up
			// and we tore the session down. Nothing to recover from.
			_logger.LogDebug(ex, "VoiceLive websocket disposed during shutdown");
		}
		catch(Exception ex)
		{
			_logger.LogError(ex, "Error processing events");
			throw;
		}
	}

	/// <summary>Handle different types of server events from VoiceLive.</summary>
	private async Task HandleSessionUpdateAsync(SessionUpdate serverEvent, CancellationToken cancellationToken)
	{
		_logger.LogDebug("Received event: {EventType}", serverEvent.GetType().Name);

		await (serverEvent switch
		{
			SessionUpdateSessionCreated sessionCreated => HandleSessionCreatedAsync(sessionCreated, cancellationToken),
			SessionUpdateSessionUpdated => HandleSessionUpdatedAsync(cancellationToken),
			SessionUpdateInputAudioBufferSpeechStarted => HandleSpeechStartedAsync(),
			SessionUpdateInputAudioBufferSpeechStopped => HandleSpeechStoppedAsync(),
			SessionUpdateResponseCreated => HandleResponseCreatedAsync(),
			SessionUpdateResponseAudioDelta audioDelta => HandleResponseAudioDeltaAsync(audioDelta),
			SessionUpdateResponseAudioDone => HandleResponseAudioDoneAsync(),
			SessionUpdateResponseDone => HandleResponseDoneAsync(),
			SessionUpdateResponseTextDelta textDelta => HandleResponseTextDeltaAsync(textDelta),
			SessionUpdateResponseTextDone textDone => HandleResponseTextDoneAsync(textDone),
			SessionUpdateResponseAudioTranscriptDelta audioTranscriptDelta => HandleResponseAudioTranscriptDeltaAsync(audioTranscriptDelta),
			SessionUpdateResponseAudioTranscriptDone audioTranscriptDone => HandleResponseAudioTranscriptDoneAsync(audioTranscriptDone),
			SessionUpdateResponseFunctionCallArgumentsDelta functionCallDelta => HandleFunctionCallArgumentsDeltaAsync(functionCallDelta),
			SessionUpdateResponseFunctionCallArgumentsDone functionCallDone => HandleFunctionCallArgumentsDoneAsync(functionCallDone, cancellationToken),
			SessionUpdateConversationItemInputAudioTranscriptionDelta transcriptionDelta => HandleUserTranscriptionDeltaAsync(transcriptionDelta),
			SessionUpdateConversationItemInputAudioTranscriptionCompleted transcriptionCompleted => HandleUserTranscriptionCompletedAsync(transcriptionCompleted),
			SessionUpdateError errorEvent => HandleErrorAsync(errorEvent),

			_ => HandleUnhandledAsync(serverEvent)
		});
	}

	private async Task HandleSessionUpdatedAsync(CancellationToken cancellation)
	{
		_logger.LogDebug("Session updated successfully");

		if(!_conversationStarted)
		{
			_logger.LogInformation("Sending proactive greeting request");
			try
			{
				await _session!.StartResponseAsync(cancellation);
				_conversationStarted = true;
			}
			catch(Exception ex)
			{
				_logger.LogError(ex, "Failed to send proactive greeting request");
				throw;
			}
		}
	}

	private async Task HandleSpeechStartedAsync()
	{
		_logger.LogDebug("User started speaking; stopping playback");

		if(_audioClient != null)
		{
			_audioClient.IsHumanSpeaking = true;
			_audioClient.IsModelSpeaking = false;
		}

		if(_allowInterrupts && _audioClient != null)
			await _audioClient.ClearPlaybackAsync(_conversationCts?.Token ?? CancellationToken.None);

		if(_allowInterrupts && _responseActive && _canCancelResponse)
		{
			try
			{
				await _session!.CancelResponseAsync();
				_logger.LogInformation("Active response cancelled due to user barge-in");
			}
			catch(Exception ex) when (ex.Message.Contains("no active response", StringComparison.OrdinalIgnoreCase))
			{
				_logger.LogDebug(ex, "Cancellation benign: response already completed");
			}

			try
			{
				await _session!.ClearStreamingAudioAsync();
				_logger.LogDebug("Cleared streaming audio after cancellation");
			}
			catch(Exception ex)
			{
				_logger.LogWarning(ex, "ClearStreamingAudio failed during barge-in");
			}
		}
		else
		{
			_logger.LogDebug("No active response to cancel during barge-in");
		}
	}

	private async Task HandleSpeechStoppedAsync()
	{
		_logger.LogDebug("User stopped speaking");

		_audioClient?.IsHumanSpeaking = false;
	}

	private async Task HandleResponseCreatedAsync()
	{
		_logger.LogDebug("Assistant response created");
		_responseActive = true;
		_canCancelResponse = true;
	}

	private async Task HandleResponseAudioDeltaAsync(SessionUpdateResponseAudioDelta audioDelta)
	{
		_logger.LogDebug("Received audio delta");

		if(audioDelta.Delta == null || _audioClient == null)
			return;

		await ForwardAudioAsync(_audioClient, audioDelta.Delta.ToArray(), _allowInterrupts,
			_conversationCts?.Token ?? CancellationToken.None);
	}

	internal static async Task ForwardAudioAsync(IVoiceAudioClient audioClient, byte[] audio, bool allowInterrupts,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(audioClient);
		if (allowInterrupts && audioClient.IsHumanSpeaking)
			return;
		await audioClient.SendAudioAsync(audio, cancellationToken);
		audioClient.IsModelSpeaking = true;
	}

	private async Task HandleResponseAudioDoneAsync()
	{
		_logger.LogDebug("Assistant finished speaking");
		_audioClient?.IsModelSpeaking = false;
	}

	private async Task HandleResponseDoneAsync()
	{
		_logger.LogDebug("Response complete");
		_responseActive = false;
		_canCancelResponse = false;
	}

	private async Task HandleResponseTextDeltaAsync(SessionUpdateResponseTextDelta textDelta)
	{
		AppendAgentTranscriptDelta(textDelta.Delta);
		if(!string.IsNullOrWhiteSpace(textDelta.Delta))
			await SendTranscriptAsync(AgentSpeaker, GetMessageId(textDelta.ItemId, textDelta.ResponseId), textDelta.Delta, false);
	}

	private async Task HandleResponseTextDoneAsync(SessionUpdateResponseTextDone textDone)
	{
		string? transcript = LogAgentTranscript(textDone.Text);
		if(!string.IsNullOrWhiteSpace(transcript))
			await SendTranscriptAsync(AgentSpeaker, GetMessageId(textDone.ItemId, textDone.ResponseId), transcript!, true);
	}

	private async Task HandleResponseAudioTranscriptDeltaAsync(SessionUpdateResponseAudioTranscriptDelta transcriptDelta)
	{
		AppendAgentTranscriptDelta(transcriptDelta.Delta);
		if(!string.IsNullOrWhiteSpace(transcriptDelta.Delta))
			await SendTranscriptAsync(AgentSpeaker, GetMessageId(transcriptDelta.ItemId, transcriptDelta.ResponseId), transcriptDelta.Delta, false);
	}

	private async Task HandleResponseAudioTranscriptDoneAsync(SessionUpdateResponseAudioTranscriptDone transcriptDone)
	{
		string? transcript = LogAgentTranscript(transcriptDone.Transcript);
		if(!string.IsNullOrWhiteSpace(transcript))
			await SendTranscriptAsync(AgentSpeaker, GetMessageId(transcriptDone.ItemId, transcriptDone.ResponseId), transcript!, true);
	}

	private async Task HandleUserTranscriptionDeltaAsync(SessionUpdateConversationItemInputAudioTranscriptionDelta transcriptionDelta)
	{
		if(!string.IsNullOrWhiteSpace(transcriptionDelta.Delta))
		{
			_userTranscriptBuilder.Append(transcriptionDelta.Delta);
			_logger.LogDebug("User transcript delta: {Delta}", transcriptionDelta.Delta);
			await SendTranscriptAsync(HumanSpeaker, GetMessageId(transcriptionDelta.ItemId), transcriptionDelta.Delta, false);
		}
	}

	private async Task HandleUserTranscriptionCompletedAsync(SessionUpdateConversationItemInputAudioTranscriptionCompleted transcriptionCompleted)
	{
		string transcript = !string.IsNullOrWhiteSpace(transcriptionCompleted.Transcript)
			? transcriptionCompleted.Transcript
			: _userTranscriptBuilder.ToString();

		if(!string.IsNullOrWhiteSpace(transcript))
		{
			_logger.LogInformation("User transcript: {Transcript}", transcript.Trim());
			await SendTranscriptAsync(HumanSpeaker, GetMessageId(transcriptionCompleted.ItemId), transcript.Trim(), true);
		}

		_userTranscriptBuilder.Clear();
	}

	private Task HandleErrorAsync(SessionUpdateError errorEvent)
	{
		_logger.LogError("Voice Live error: {ErrorMessage}", errorEvent.Error?.Message);
		_responseActive = false;
		_canCancelResponse = false;
		throw new InvalidOperationException($"Voice Live error: {errorEvent.Error?.Message}");
	}

	private Task HandleFunctionCallArgumentsDeltaAsync(SessionUpdateResponseFunctionCallArgumentsDelta deltaEvent)
	{
		if(deltaEvent == null)
			return Task.CompletedTask;

		if(!string.IsNullOrWhiteSpace(deltaEvent.Delta))
			_logger.LogDebug("Function {FunctionCallId} arguments delta received", deltaEvent.CallId);

		return Task.CompletedTask;
	}

	private async Task HandleFunctionCallArgumentsDoneAsync(SessionUpdateResponseFunctionCallArgumentsDone functionCallDone, CancellationToken cancellationToken)
	{
		if(_session is null)
			throw new InvalidOperationException("A Voice Live session is required to handle function calls.");

		if(functionCallDone == null || string.IsNullOrWhiteSpace(functionCallDone.Name) || string.IsNullOrWhiteSpace(functionCallDone.CallId))
		{
			throw new InvalidOperationException("Voice Live function call is missing a name or call ID.");
		}

		string arguments = string.IsNullOrWhiteSpace(functionCallDone.Arguments) ? "{}" : functionCallDone.Arguments;
		VoiceFunctionResult result = await ResolveFunctionAsync(
			functionCallDone.CallId, functionCallDone.Name, arguments,
			_audioClient ?? throw new InvalidOperationException("The audio transport is not initialized."), cancellationToken);

		FunctionCallOutputItem functionResult = new(functionCallDone.CallId, result.Output);
		if(!string.IsNullOrWhiteSpace(functionCallDone.ItemId))
			await _session.AddItemAsync(functionResult, functionCallDone.ItemId, cancellationToken);
		else
			await _session.AddItemAsync(functionResult, cancellationToken);

		await _session.StartResponseAsync(cancellationToken);
		if (result.EndConversation)
			_conversationCts?.CancelAfter(TimeSpan.FromSeconds(8));
		_logger.LogInformation("Submitted output for function {FunctionName}", functionCallDone.Name);
	}

	internal async Task<VoiceFunctionResult> ResolveFunctionAsync(
		string callId, string functionName, string arguments, IVoiceAudioClient audioClient, CancellationToken cancellationToken)
	{
		if (_functionHandler is null)
			throw new InvalidOperationException("A function handler is required when Voice Live requests a tool call.");

		VoiceFunctionResult? result = await _functionHandler(functionName, arguments, audioClient.ConversationId, cancellationToken);
		VoiceTranscript transcript = new(GetMessageId(callId), ToolSpeaker, functionName, true,
			DateTimeOffset.UtcNow, new VoiceToolCall(functionName, arguments, result?.Output));
		await audioClient.SendTranscriptAsync(transcript, cancellationToken);

		if (result is null || string.IsNullOrWhiteSpace(result.Output))
			throw new InvalidOperationException($"No output was returned for Voice Live function '{functionName}'.");
		return result;
	}

	private void AppendAgentTranscriptDelta(string? delta)
	{
		if(string.IsNullOrWhiteSpace(delta))
			return;

		_agentTranscriptBuilder.Append(delta);
		_logger.LogDebug("Agent transcript delta: {Delta}", delta);
	}

	private string? LogAgentTranscript(string? transcript)
	{
		string finalTranscript = !string.IsNullOrWhiteSpace(transcript)
			? transcript.Trim()
			: _agentTranscriptBuilder.ToString().Trim();

		if(!string.IsNullOrWhiteSpace(finalTranscript))
			_logger.LogInformation("Agent transcript: {Transcript}", finalTranscript);

		_agentTranscriptBuilder.Clear();
		return string.IsNullOrWhiteSpace(finalTranscript) ? null : finalTranscript;
	}

	private async Task HandleUnhandledAsync(object evt)
	{
		_logger.LogDebug("Unhandled event type: {EventType}", evt.GetType().Name);
	}

	internal Task SendTranscriptAsync(string speaker, string? messageId, string text, bool isFinal)
	{
		if(_audioClient == null || string.IsNullOrWhiteSpace(text))
			return Task.CompletedTask;

		return PublishTranscriptAsync(_audioClient, speaker, messageId, text, isFinal,
			_conversationCts?.Token ?? CancellationToken.None);
	}

	internal static Task PublishTranscriptAsync(IVoiceAudioClient audioClient, string speaker, string? messageId,
		string text, bool isFinal, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(audioClient);
		VoiceTranscript transcript = new(GetMessageId(messageId), speaker, text, isFinal, DateTimeOffset.UtcNow);
		return audioClient.SendTranscriptAsync(transcript, cancellationToken);
	}

	private static string GetMessageId(string? primaryId, string? fallbackId = null)
		=> !string.IsNullOrWhiteSpace(primaryId)
			? primaryId
			: !string.IsNullOrWhiteSpace(fallbackId)
				? fallbackId
				: Guid.NewGuid().ToString("N");

	/// <summary>
	/// Handle session created event.
	/// </summary>
	private async Task HandleSessionCreatedAsync(SessionUpdateSessionCreated sessionCreated, CancellationToken cancellationToken)
	{
		Verify();

		_logger.LogTrace(" <<< Connected: session started");
		_logger.LogInformation("Session ready: {SessionId}", sessionCreated.Session?.Id);

		// Kick off the proactive greeting as soon as the session is created rather
		// than waiting for the subsequent SessionUpdated round-trip — this shaves
		// a noticeable amount of perceived spin-up latency off every call.
		if(!_conversationStarted)
		{
			try
			{
				_logger.LogInformation("Sending proactive greeting request");
				await _session!.StartResponseAsync(cancellationToken);
				_conversationStarted = true;
			}
			catch(Exception ex)
			{
				_logger.LogWarning(ex, "Greeting on session creation failed; retrying on session update");
			}
		}
	}

	[MemberNotNull(nameof(_audioClient))]
	private void Verify()
	{
		if(_audioClient is null)
			throw new InvalidOperationException("Human client not initialized");
	}
}
