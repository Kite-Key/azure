namespace KiteKey.AI.Azure.VoiceLive;

/// <summary>Function result returned by a host handler; optionally ends the conversation after a farewell.</summary>
public sealed record VoiceFunctionResult(string Output, bool EndConversation = false);

/// <summary>Processes a Voice Live function call without requiring a domain-specific function executor.</summary>
public delegate Task<VoiceFunctionResult?> VoiceFunctionHandler(
    string functionName, string arguments, string? conversationId, CancellationToken cancellationToken);
