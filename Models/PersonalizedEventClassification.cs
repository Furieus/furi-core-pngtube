namespace FuriXeus.Tuber.Native.Models;

public sealed record PersonalizedEventClassification(
    DateTimeOffset Timestamp,
    string TopLabel,
    double TopScore,
    double NormalScore,
    double NoiseScore,
    double KeyboardScore,
    double LaughScore,
    double YawnScore,
    double HmmScore,
    bool LaughDetected,
    bool YawnDetected,
    bool HmmDetected,
    string Detail);
