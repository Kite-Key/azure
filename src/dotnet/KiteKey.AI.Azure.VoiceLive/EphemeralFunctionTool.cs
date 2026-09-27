namespace KiteKey.AI.Azure.VoiceLive;

/// <summary>A function tool to expose to a VoiceLive session ephemerally (without going through a Foundry agent).</summary>
/// <param name="Name">Function name as the model will see it.</param>
/// <param name="Description">Human-readable description shown to the model.</param>
/// <param name="ParameterSchema">JSON Schema string describing the function arguments.</param>
public sealed record EphemeralFunctionTool(string Name, string Description, string ParameterSchema);
