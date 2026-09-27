namespace KiteKey.AI.Azure.VoiceLive;

/// <summary>Host-provided bidirectional PCM16 audio and transcript transport for a Voice Live conversation.</summary>
public interface IVoiceAudioClient
{
    /// <summary>Optional host conversation identifier passed to the function handler.</summary>
    string? ConversationId { get; }

    /// <summary>Whether the caller is currently speaking.</summary>
    bool IsHumanSpeaking { get; set; }

    /// <summary>Whether the assistant is currently playing audio.</summary>
    bool IsModelSpeaking { get; set; }

    /// <summary>Returns a readable microphone audio stream, owned by the host.</summary>
    Stream ReceiveAudioStream();

    /// <summary>Forwards synthesized PCM16 audio to the caller.</summary>
    Task SendAudioAsync(byte[] audio, CancellationToken cancellationToken);

    /// <summary>Clears queued playback when the caller interrupts the assistant.</summary>
    Task ClearPlaybackAsync(CancellationToken cancellationToken);

    /// <summary>Publishes a provider-neutral transcript event.</summary>
    Task SendTranscriptAsync(VoiceTranscript transcript, CancellationToken cancellationToken);

    /// <summary>Publishes a connection status such as connecting or ready.</summary>
    Task SendStatusAsync(string status, CancellationToken cancellationToken);
}

/// <summary>Transcript event produced by a Voice Live conversation, independent of host persistence entities.</summary>
public sealed record VoiceTranscript(
    string MessageId,
    string Speaker,
    string Text,
    bool IsFinal,
    DateTimeOffset Timestamp,
    VoiceToolCall? ToolCall = null);

/// <summary>Optional details of a completed function call surfaced to the host UI.</summary>
public sealed record VoiceToolCall(string Name, string Arguments, string? Output);

/// <summary>Function result returned by a host handler; optionally ends the conversation after a farewell.</summary>
public sealed record VoiceFunctionResult(string Output, bool EndConversation = false);

/// <summary>Processes a Voice Live function call without requiring a domain-specific function executor.</summary>
public delegate Task<VoiceFunctionResult?> VoiceFunctionHandler(
    string functionName, string arguments, string? conversationId, CancellationToken cancellationToken);
