namespace KiteKey.AI.Azure.VoiceLive;

/// <summary>Per-session voice settings, with optional defaults supplied by the caller.</summary>
public sealed record VoiceSessionSettings
{
	/// <summary>The Azure TTS voice name (e.g. "en-US-Aria:DragonHDLatestNeural").</summary>
	public string? Voice { get; init; }

	/// <summary>VAD activation threshold (0.0–1.0).</summary>
	public float? VadThreshold { get; init; }

	/// <summary>How long silence must last before the turn is considered finished (ms).</summary>
	public int? SilenceDurationMs { get; init; }

	/// <summary>How much audio before the speech onset to include (ms).</summary>
	public int? PrefixPaddingMs { get; init; }

	/// <summary>
	/// When set, the session bypasses any preconfigured Foundry agent and runs against a raw Foundry model
	/// with the supplied system instructions and tool definitions. Used for single-call personas like the
	/// warm-transfer briefing.
	/// </summary>
	public EphemeralAgent? Ephemeral { get; init; }

	/// <summary>Returns a new instance with caller-supplied defaults used for any property that is <c>null</c>.</summary>
	public VoiceSessionSettings WithDefaults(VoiceSessionSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);
		return this with
		{
			Voice = Voice ?? defaults.Voice,
			VadThreshold = VadThreshold ?? defaults.VadThreshold,
			SilenceDurationMs = SilenceDurationMs ?? defaults.SilenceDurationMs,
			PrefixPaddingMs = PrefixPaddingMs ?? defaults.PrefixPaddingMs
		};
	}
}

/// <summary>
/// Configures an ephemeral, agent-less VoiceLive session. All three pieces — the model id,
/// the system instructions, and the tools — are scoped to a single conversation.
/// </summary>
/// <param name="ModelId">The Foundry model id to run the session against (e.g. <c>gpt-4o-realtime-preview</c>).</param>
/// <param name="Instructions">System instructions (system prompt) for the session.</param>
/// <param name="Tools">Function tools the model may invoke during the session.</param>
public sealed record EphemeralAgent(
	string ModelId,
	string Instructions,
	IReadOnlyList<EphemeralFunctionTool> Tools);
