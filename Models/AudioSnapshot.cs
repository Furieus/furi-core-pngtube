namespace FuriXeus.Tuber.Native.Models;

public sealed record AudioSnapshot(
    DateTimeOffset Timestamp,
    double Rms,
    double Db,
    double Peak,
    double PitchHz,
    double PitchClarity,
    double ZeroCrossingRate,
    bool VoiceActive,
    double HumConfidence,
    double YawnConfidence,
    double LaughConfidence,
    bool HumDetected,
    bool YawnDetected,
    bool LaughDetected,
    double[] Waveform);
