namespace FuriXeus.Tuber.Native.Models;

public sealed record ReactionRenderState(
    string StateId,
    string StateName,
    bool IsTalking,
    string AssetPath,
    string Reason,
    DateTimeOffset Timestamp);
