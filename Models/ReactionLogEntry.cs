namespace FuriXeus.Tuber.Native.Models;

public sealed record ReactionLogEntry(DateTimeOffset Timestamp, string Label, string Detail);
