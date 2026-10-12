using FuriXeus.Tuber.Native.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FuriXeus.Tuber.Native.Services;

public sealed class AudioEngine : IDisposable
{
    private const int SampleRate = 16000;
    private readonly object _sync = new();
    private readonly float[] _sampleRing = new float[SampleRate * 4];
    private readonly Queue<(DateTimeOffset Time, double Pitch, double Clarity, double Rms)> _pitchHistory = new();
    private readonly Queue<(DateTimeOffset Time, double Rms, double Zcr)> _energyHistory = new();
    private readonly Queue<(DateTimeOffset End, double DurationMs, double PeakDb)> _laughBursts = new();

    private WasapiCapture? _capture;
    private MMDevice? _captureDevice;
    private int _ringWrite;
    private int _ringCount;
    private bool _voiceActive;
    private DateTimeOffset _voiceStarted;
    private DateTimeOffset _lastVoiceSeen;
    private double _voicePeakDb = -120;
    private DateTimeOffset? _humCandidateSince;
    private DateTimeOffset? _lastHumGood;
    private DateTimeOffset? _yawnCandidateSince;
    private DateTimeOffset? _lastYawnGood;
    private DateTimeOffset? _lastLaughGood;
    private bool _humDetected;
    private bool _yawnDetected;
    private bool _laughDetected;
    private double _humConfidence;
    private double _yawnConfidence;
    private double _laughConfidence;

    public double TalkThresholdDb { get; set; } = -42;
    public int TalkReleaseMs { get; set; } = 130;
    public int HumAttackMs { get; set; } = 360;
    public int YawnAttackMs { get; set; } = 1100;

    public bool IsRunning => _capture is not null;

    public event Action<AudioSnapshot>? SnapshotAvailable;
    public event Action<byte[]>? Pcm16FrameAvailable;
    public event Action<string>? Faulted;

    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID;
        }
        catch
        {
            // A machine can legitimately have no default capture endpoint.
        }

        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new AudioDeviceInfo(
                device.ID,
                device.FriendlyName,
                string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Start(string deviceId)
    {
        Stop();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(candidate => string.Equals(candidate.ID, deviceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected microphone is no longer available.");

            try { device.AudioEndpointVolume.Mute = false; } catch { /* best effort */ }

#pragma warning disable CS0618
            // 40 ms event-driven WASAPI capture gives the event classifiers enough temporal detail
            // to distinguish a sustained hum from ordinary syllables while keeping latency low.
            var capture = new WasapiCapture(device, true, 40)
            {
                WaveFormat = new WaveFormat(SampleRate, 16, 1)
            };
#pragma warning restore CS0618

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            _captureDevice = device;
            _capture = capture;
        }
        catch (Exception ex)
        {
            Faulted?.Invoke($"Mic start failed: {ex.Message}");
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        var capture = _capture;
        var device = _captureDevice;
        _capture = null;
        _captureDevice = null;

        if (capture is not null)
        {
            try { capture.StopRecording(); } catch { }
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }

        device?.Dispose();

        lock (_sync)
        {
            _voiceActive = false;
            _ringWrite = 0;
            _ringCount = 0;
            _humConfidence = 0;
            _yawnConfidence = 0;
            _laughConfidence = 0;
            _humDetected = false;
            _yawnDetected = false;
            _laughDetected = false;
            _humCandidateSince = null;
            _lastHumGood = null;
            _yawnCandidateSince = null;
            _lastYawnGood = null;
            _lastLaughGood = null;
            _pitchHistory.Clear();
            _energyHistory.Clear();
            _laughBursts.Clear();
            Array.Clear(_sampleRing, 0, _sampleRing.Length);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Faulted?.Invoke($"Mic stopped: {e.Exception.Message}");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded <= 1) return;

            var bytes = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, bytes, 0, e.BytesRecorded);
            Pcm16FrameAvailable?.Invoke(bytes);

            var sampleCount = e.BytesRecorded / 2;
            if (sampleCount <= 0) return;

            var samples = new float[sampleCount];
            double sumSquares = 0;
            double peak = 0;
            var zeroCrossings = 0;
            float previous = 0;

            lock (_sync)
            {
                for (var i = 0; i < sampleCount; i++)
                {
                    var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                    samples[i] = sample;
                    sumSquares += sample * sample;
                    peak = Math.Max(peak, Math.Abs(sample));
                    if (i > 0 && ((sample >= 0 && previous < 0) || (sample < 0 && previous >= 0)))
                        zeroCrossings++;
                    previous = sample;

                    _sampleRing[_ringWrite] = sample;
                    _ringWrite = (_ringWrite + 1) % _sampleRing.Length;
                    _ringCount = Math.Min(_sampleRing.Length, _ringCount + 1);
                }
            }

            var rms = Math.Sqrt(sumSquares / sampleCount);
            var db = 20.0 * Math.Log10(Math.Max(rms, 1e-9));
            var zcr = sampleCount > 1 ? (double)zeroCrossings / (sampleCount - 1) : 0;
            var now = DateTimeOffset.UtcNow;

            AudioSnapshot snapshot;
            lock (_sync)
            {
                UpdateVoiceState(now, db);

                var pitchWindow = GetRecentSamplesUnsafe(Math.Min(1280, _ringCount));
                var (pitch, clarity) = EstimatePitch(pitchWindow, SampleRate);
                UpdateHum(now, pitch, clarity, rms);
                UpdateEnergy(now, rms, zcr);
                UpdateYawn(now);
                UpdateLaugh(now);

                snapshot = new AudioSnapshot(
                    now,
                    rms,
                    db,
                    peak,
                    pitch,
                    clarity,
                    zcr,
                    _voiceActive,
                    _humConfidence,
                    _yawnConfidence,
                    _laughConfidence,
                    _humDetected,
                    _yawnDetected,
                    _laughDetected,
                    BuildWaveform(samples, 64));
            }

            SnapshotAvailable?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Faulted?.Invoke($"Audio processing error: {ex.Message}");
        }
    }

    private void UpdateVoiceState(DateTimeOffset now, double db)
    {
        var thresholdHit = db >= TalkThresholdDb;
        if (thresholdHit)
        {
            _lastVoiceSeen = now;
            if (!_voiceActive)
            {
                _voiceActive = true;
                _voiceStarted = now;
                _voicePeakDb = db;
                _pitchHistory.Clear();
                _energyHistory.Clear();
                _humCandidateSince = null;
                _yawnCandidateSince = null;
            }
            else
            {
                _voicePeakDb = Math.Max(_voicePeakDb, db);
            }
            return;
        }

        if (!_voiceActive || (now - _lastVoiceSeen).TotalMilliseconds <= TalkReleaseMs)
            return;

        var duration = (now - _voiceStarted).TotalMilliseconds;
        _voiceActive = false;

        // Laughs are not always a chain of tiny 100-300 ms "ha" bursts. On a real mic the
        // 130 ms talk-release gate often joins two or three chuckles into a 700-1200 ms voiced
        // segment. Keep moderately long segments too, then let UpdateLaugh judge cadence,
        // impulse length and strength instead of throwing them away at the gate.
        if (duration is >= 80 and <= 1400)
            _laughBursts.Enqueue((now, duration, _voicePeakDb));

        _voicePeakDb = -120;
    }

    private void UpdateHum(DateTimeOffset now, double pitch, double clarity, double rms)
    {
        _pitchHistory.Enqueue((now, pitch, clarity, rms));
        while (_pitchHistory.TryPeek(out var oldest) && (now - oldest.Time).TotalMilliseconds > 760)
            _pitchHistory.Dequeue();

        if (!_voiceActive)
        {
            _humCandidateSince = null;

            // Never PROMOTE a merely-good candidate into a detected hum after the voice gate
            // closes. v0.6.4 could flip HumDetected=true on release even when the confidence
            // had never reached the lock threshold. Only preserve a lock that already existed.
            _humDetected = _humDetected &&
                           _lastHumGood.HasValue &&
                           (now - _lastHumGood.Value).TotalMilliseconds <= 260;

            _humConfidence *= 0.72;
            if (_humConfidence < 0.02) _humConfidence = 0;
            return;
        }

        var voiceDurationMs = (now - _voiceStarted).TotalMilliseconds;
        var allRecent = _pitchHistory.ToArray();
        var voiced = allRecent
            .Where(p => p.Clarity >= 0.50 && p.Pitch is >= 65 and <= 360 && p.Rms > 0.0015)
            .ToArray();

        var score = 0.0;
        if (voiceDurationMs >= 280 && allRecent.Length >= 7 && voiced.Length >= 6)
        {
            var voicedCoverage = (double)voiced.Length / allRecent.Length;
            var meanPitch = voiced.Average(p => p.Pitch);
            var pitchStd = Math.Sqrt(voiced.Average(p => Math.Pow(p.Pitch - meanPitch, 2)));
            var pitchCv = meanPitch <= 0 ? 1 : pitchStd / meanPitch;
            var pitchMoves = voiced
                .Zip(voiced.Skip(1), (a, b) => Math.Abs(b.Pitch - a.Pitch) / Math.Max(1, a.Pitch))
                .ToArray();
            var meanPitchMove = pitchMoves.Length == 0 ? 1 : pitchMoves.Average();
            var meanRms = voiced.Average(p => p.Rms);
            var rmsStd = Math.Sqrt(voiced.Average(p => Math.Pow(p.Rms - meanRms, 2)));
            var rmsCv = meanRms <= 0 ? 1 : rmsStd / meanRms;
            var clarityAvg = voiced.Average(p => p.Clarity);

            // A normal "hmm" is often only 500-800 ms. The v0.5.5 detector waited for
            // 420 ms before it even opened a candidate, then required another 650 ms of
            // attack time. Start evaluating earlier, but make the tonal stability tests
            // stricter so ordinary speech does not regain the old false-positive problem.
            var coverageScore = Clamp01((voicedCoverage - 0.74) / 0.20);
            var pitchScore = Clamp01((0.062 - pitchCv) / 0.040);
            var movementScore = Clamp01((0.040 - meanPitchMove) / 0.027);
            var energyScore = Clamp01((0.30 - rmsCv) / 0.20);
            var clarityScore = Clamp01((clarityAvg - 0.60) / 0.22);
            score = coverageScore * 0.20 + pitchScore * 0.27 + movementScore * 0.27 + energyScore * 0.11 + clarityScore * 0.15;
        }

        if (score >= 0.70)
        {
            _humCandidateSince ??= now;
            _lastHumGood = now;
            var attack = Math.Max(340, HumAttackMs);
            var attackProgress = Clamp01((now - _humCandidateSince.Value).TotalMilliseconds / attack);
            _humConfidence = Math.Max(_humConfidence * 0.92, score * attackProgress);
            if ((now - _humCandidateSince.Value).TotalMilliseconds >= attack && _humConfidence >= 0.74)
                _humDetected = true;
        }
        else
        {
            // One ugly 40 ms buffer should not erase a good hum candidate. Raw microphones,
            // fans and breath noise can briefly disturb pitch clarity in the middle of a hum.
            var inHumGrace = _lastHumGood.HasValue && (now - _lastHumGood.Value).TotalMilliseconds <= 150;
            if (!inHumGrace)
            {
                _humCandidateSince = null;
                _humDetected = false;
            }
            _humConfidence *= inHumGrace ? 0.92 : 0.76;
            if (_humConfidence < 0.02) _humConfidence = 0;
        }
    }

    private void UpdateEnergy(DateTimeOffset now, double rms, double zcr)
    {
        _energyHistory.Enqueue((now, rms, zcr));
        while (_energyHistory.TryPeek(out var oldest) && (now - oldest.Time).TotalSeconds > 4.2)
            _energyHistory.Dequeue();
    }

    private void UpdateYawn(DateTimeOffset now)
    {
        if (!_voiceActive)
        {
            _yawnCandidateSince = null;

            // Same release rule as hum: preserve a yawn that was already locked, but never
            // convert an unfinished candidate into Sleepy just because speech ended.
            _yawnDetected = _yawnDetected &&
                            _lastYawnGood.HasValue &&
                            (now - _lastYawnGood.Value).TotalMilliseconds <= 220;

            _yawnConfidence *= 0.80;
            if (_yawnConfidence < 0.02) _yawnConfidence = 0;
            return;
        }

        var durationMs = (now - _voiceStarted).TotalMilliseconds;
        if (durationMs < 1000 || durationMs > 7000)
        {
            _yawnConfidence *= 0.88;
            if (durationMs > 7000) _yawnDetected = false;
            return;
        }

        var recent = _energyHistory
            .Where(x => x.Time >= _voiceStarted && (now - x.Time).TotalMilliseconds <= 3600)
            .ToArray();

        if (recent.Length < 18)
        {
            _yawnConfidence *= 0.90;
            return;
        }

        var activeFloor = Math.Pow(10.0, (TalkThresholdDb + 3.0) / 20.0);
        var continuity = recent.Count(x => x.Rms >= activeFloor) / (double)recent.Length;
        var lowZcrCoverage = recent.Count(x => x.Zcr <= 0.095) / (double)recent.Length;

        var smoothed = Smooth(recent.Select(x => x.Rms).ToArray(), 4);
        var third = Math.Max(2, smoothed.Length / 3);
        var first = smoothed.Take(third).Average();
        var middle = smoothed.Skip(third).Take(third).DefaultIfEmpty(first).Average();
        var last = smoothed.Skip(third * 2).DefaultIfEmpty(middle).Average();
        var peak = smoothed.Max();
        var avg = smoothed.Average();

        var riseRatio = first <= 1e-7 ? 0 : peak / first;
        var broadRatio = avg <= 1e-7 ? 0 : middle / avg;
        var fallOrSustain = peak <= 1e-7 ? 0 : last / peak;
        var signChanges = CountSlopeSignChanges(smoothed);
        var maxReasonableSignChanges = Math.Max(4, smoothed.Length / 8);

        var durationScore = Clamp01((durationMs - 1100) / 1200);
        var continuityScore = Clamp01((continuity - 0.70) / 0.24);
        var vowelScore = Clamp01((lowZcrCoverage - 0.58) / 0.28);
        var riseScore = Clamp01((riseRatio - 1.18) / 0.90);
        var broadScore = Clamp01((broadRatio - 0.95) / 0.28);
        var slowEnvelopeScore = Clamp01((maxReasonableSignChanges - signChanges + 1) / (double)Math.Max(1, maxReasonableSignChanges));
        var sustainScore = Clamp01((fallOrSustain - 0.38) / 0.48);
        var notHumScore = Clamp01((0.58 - _humConfidence) / 0.58);

        var score = durationScore * 0.16 + continuityScore * 0.20 + vowelScore * 0.20 + riseScore * 0.13 +
                    broadScore * 0.10 + slowEnvelopeScore * 0.09 + sustainScore * 0.06 + notHumScore * 0.06;

        if (score >= 0.66)
        {
            _yawnCandidateSince ??= now;
            _lastYawnGood = now;
            var attack = Math.Max(700, YawnAttackMs);
            var attackProgress = Clamp01((now - _yawnCandidateSince.Value).TotalMilliseconds / attack);
            _yawnConfidence = Math.Max(_yawnConfidence * 0.91, score * attackProgress);
            if ((now - _yawnCandidateSince.Value).TotalMilliseconds >= attack && _yawnConfidence >= 0.72)
                _yawnDetected = true;
        }
        else
        {
            _yawnCandidateSince = null;
            if (!_lastYawnGood.HasValue || (now - _lastYawnGood.Value).TotalMilliseconds > 220)
                _yawnDetected = false;
            _yawnConfidence *= 0.82;
            if (_yawnConfidence < 0.02) _yawnConfidence = 0;
        }
    }

    private void UpdateLaugh(DateTimeOffset now)
    {
        // v0.6.2 only counted voice segments <= 620 ms and required four of them inside
        // 1.7 seconds. The debug capture showed a real laugh being split into roughly
        // 240 ms, 559 ms and 1100 ms segments, so the confidence meter could never even
        // move off zero. Keep a slightly wider 2.45 s rhythm window and score three or
        // more burst-like segments. A quick impulse is still required to avoid treating
        // ordinary conversational phrases as laughter.
        while (_laughBursts.TryPeek(out var oldest) && (now - oldest.End).TotalMilliseconds > 2450)
            _laughBursts.Dequeue();

        var bursts = _laughBursts.ToArray();
        var score = 0.0;
        var lastBurstAgeMs = bursts.Length == 0 ? double.PositiveInfinity : (now - bursts[^1].End).TotalMilliseconds;

        if (bursts.Length >= 2)
        {
            var intervals = bursts
                .Zip(bursts.Skip(1), (a, b) => (b.End - a.End).TotalMilliseconds)
                .Where(ms => ms > 0)
                .ToArray();

            var mean = intervals.Length == 0 ? 9999 : intervals.Average();
            var std = intervals.Length == 0 ? 9999 : Math.Sqrt(intervals.Average(v => Math.Pow(v - mean, 2)));
            var cv = mean <= 0 ? 1 : std / mean;
            var quickest = bursts.Min(b => b.DurationMs);
            var shortRatio = bursts.Count(b => b.DurationMs <= 650) / (double)bursts.Length;
            var strongRatio = bursts.Count(b => b.PeakDb >= TalkThresholdDb + 10.0) / (double)bursts.Length;

            var cadenceScore = Clamp01((0.68 - cv) / 0.50);
            var intervalScore = mean is >= 140 and <= 1150
                ? 1.0
                : mean < 140
                    ? Clamp01(mean / 140.0)
                    : Clamp01((1500 - mean) / 350.0);
            var densityScore = Clamp01((bursts.Length - 1) / 3.0);
            var shortnessScore = Clamp01((shortRatio - 0.25) / 0.60);
            var quickImpulseScore = Clamp01((560 - quickest) / 300.0);
            var strengthScore = Clamp01((strongRatio - 0.25) / 0.65);

            score = cadenceScore * 0.24 + intervalScore * 0.16 + densityScore * 0.15 +
                    shortnessScore * 0.13 + quickImpulseScore * 0.22 + strengthScore * 0.10;

            // Old burst patterns should not remain a half-full Laugh candidate while the user
            // has gone quiet or moved on to normal speech. Keep the score fully alive briefly,
            // then fade it out over the next ~700 ms. ReactionEngine separately owns the visual
            // hold time after a confirmed laugh, so the classifier itself can reset quickly.
            var recencyScore = lastBurstAgeMs <= 220
                ? 1.0
                : Clamp01((920 - lastBurstAgeMs) / 700.0);
            score *= recencyScore;

            // With only two bursts, show candidate confidence but do not lock yet.
            if (bursts.Length < 3)
                score *= 0.72;
        }

        // Confidence should be useful diagnostically even before the final lock, but stale
        // burst history must decay instead of plateauing around 0.5 for seconds.
        var carry = lastBurstAgeMs > 900 ? 0.52 : lastBurstAgeMs > 500 ? 0.68 : 0.84;
        _laughConfidence = Math.Max(_laughConfidence * carry, score);

        if (bursts.Length >= 3 && lastBurstAgeMs <= 500 && score >= 0.70)
        {
            _lastLaughGood = now;
            if (_laughConfidence >= 0.72)
                _laughDetected = true;
        }
        else
        {
            if (!_lastLaughGood.HasValue || (now - _lastLaughGood.Value).TotalMilliseconds > 320)
                _laughDetected = false;
            if (lastBurstAgeMs > 900)
                _laughConfidence *= 0.45;
            else if (score < 0.20)
                _laughConfidence *= 0.72;
            if (_laughConfidence < 0.02) _laughConfidence = 0;
        }
    }

    private float[] GetRecentSamplesUnsafe(int count)
    {
        count = Math.Min(count, _ringCount);
        var result = new float[count];
        var start = (_ringWrite - count + _sampleRing.Length) % _sampleRing.Length;
        for (var i = 0; i < count; i++)
            result[i] = _sampleRing[(start + i) % _sampleRing.Length];
        return result;
    }

    private static (double Pitch, double Clarity) EstimatePitch(float[] samples, int sampleRate)
    {
        if (samples.Length < 400) return (0, 0);

        var mean = samples.Average(x => (double)x);
        var centered = new double[samples.Length];
        for (var i = 0; i < samples.Length; i++) centered[i] = samples[i] - mean;

        var minLag = sampleRate / 350;
        var maxLag = Math.Min(sampleRate / 65, samples.Length / 2);
        var bestLag = 0;
        var best = 0.0;

        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double cross = 0, e1 = 0, e2 = 0;
            var limit = centered.Length - lag;
            for (var i = 0; i < limit; i++)
            {
                var a = centered[i];
                var b = centered[i + lag];
                cross += a * b;
                e1 += a * a;
                e2 += b * b;
            }

            var denom = Math.Sqrt(e1 * e2);
            var corr = denom <= 1e-12 ? 0 : cross / denom;
            if (corr > best)
            {
                best = corr;
                bestLag = lag;
            }
        }

        return bestLag == 0 ? (0, 0) : ((double)sampleRate / bestLag, Clamp01(best));
    }

    private static double[] Smooth(double[] values, int window)
    {
        if (values.Length == 0) return [];
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var start = Math.Max(0, i - window + 1);
            double sum = 0;
            for (var j = start; j <= i; j++) sum += values[j];
            result[i] = sum / (i - start + 1);
        }
        return result;
    }

    private static int CountSlopeSignChanges(double[] values)
    {
        if (values.Length < 3) return 0;
        var changes = 0;
        var lastSign = 0;
        for (var i = 1; i < values.Length; i++)
        {
            var delta = values[i] - values[i - 1];
            if (Math.Abs(delta) < 1e-6) continue;
            var sign = Math.Sign(delta);
            if (lastSign != 0 && sign != lastSign) changes++;
            lastSign = sign;
        }
        return changes;
    }

    private static double[] BuildWaveform(float[] samples, int points)
    {
        if (samples.Length == 0) return new double[points];
        var result = new double[points];
        var step = Math.Max(1, samples.Length / points);
        for (var p = 0; p < points; p++)
        {
            var start = Math.Min(samples.Length - 1, p * step);
            var end = Math.Min(samples.Length, start + step);
            double peak = 0;
            for (var i = start; i < end; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
            result[p] = peak;
        }
        return result;
    }

    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

    public void Dispose() => Stop();
}

public sealed record AudioDeviceInfo(string DeviceId, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name}  •  DEFAULT" : Name;
}
