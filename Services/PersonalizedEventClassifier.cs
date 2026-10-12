using FuriXeus.Tuber.Native.Models;
using NAudio.Wave;

namespace FuriXeus.Tuber.Native.Services;

/// <summary>
/// Lightweight local personalized classifier built from the user's labeled WAVs.
/// It intentionally avoids cloud inference and a heavyweight model: each training WAV is
/// converted into acoustic feature windows, standardized, and classified with weighted k-NN.
/// The goal is not speech recognition; it is to separate this user's Laugh / Yawn / Hmm from
/// Normal speech and room/keyboard transients.
/// </summary>
public sealed class PersonalizedEventClassifier
{
    private const int SampleRate = 16000;
    private const int FrameSamples = 640;   // 40 ms
    private const int WindowFrames = 30;    // 1.2 s
    private const int MaxLiveFrames = 40;   // 1.6 s
    private const int ClassifyEveryFrames = 3; // ~120 ms

    private readonly object _sync = new();
    private readonly Queue<FrameStats> _liveFrames = new();
    private readonly List<float> _pendingLiveSamples = new(FrameSamples * 2);
    private readonly List<TrainingVector> _training = [];
    private double[] _mean = [];
    private double[] _std = [];
    private int _frameCounter;
    private int _laughStreak;
    private int _yawnStreak;
    private int _hmmStreak;
    private long _inputPackets;
    private long _inputSamples;
    private long _analysisFrames;
    private long _classificationCount;

    public bool IsReady { get; private set; }
    public double NoiseFloorDb { get; set; } = -60;
    public string Status { get; private set; } = "personal classifier • not loaded";
    public PersonalizedEventClassification? LastClassification { get; private set; }
    public string HealthSummary
    {
        get
        {
            lock (_sync)
                return $"ready={IsReady}; packets={_inputPackets}; samples={_inputSamples}; pending={_pendingLiveSamples.Count}; frames={_analysisFrames}; classifications={_classificationCount}; trainingWindows={_training.Count}";
        }
    }

    public event Action<PersonalizedEventClassification>? ClassificationAvailable;
    public event Action<string>? StatusChanged;

    public void Reload()
    {
        lock (_sync)
        {
            _training.Clear();
            _mean = [];
            _std = [];
            ResetLiveStateUnsafe();

            var root = Path.Combine(ModelInstaller.AppDataRoot, "Training");
            var labels = new[] { "Normal", "Noise", "Keyboard", "Laugh", "Yawn", "Hmm" };
            var fileCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var label in labels)
            {
                var folder = Path.Combine(root, label);
                var files = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*.wav", SearchOption.TopDirectoryOnly).ToArray()
                    : Array.Empty<string>();
                fileCounts[label] = files.Length;

                foreach (var file in files)
                {
                    try
                    {
                        foreach (var vector in ExtractTrainingVectors(file, label))
                            _training.Add(new TrainingVector(label, vector));
                    }
                    catch
                    {
                        // One bad sample should never prevent the rest of the profile from loading.
                    }
                }
            }

            var hasTargets = fileCounts.GetValueOrDefault("Laugh") >= 2 &&
                             fileCounts.GetValueOrDefault("Yawn") >= 2 &&
                             fileCounts.GetValueOrDefault("Hmm") >= 2;
            var hasSpeechNegative = fileCounts.GetValueOrDefault("Normal") >= 2;
            var hasNoiseNegative = fileCounts.GetValueOrDefault("Noise") + fileCounts.GetValueOrDefault("Keyboard") >= 1;

            IsReady = hasTargets && hasSpeechNegative && hasNoiseNegative && _training.Count >= 10;
            if (IsReady)
                BuildStandardization();

            var keyboardNote = fileCounts.GetValueOrDefault("Keyboard") == 0 ? " • add Keyboard samples" : "";
            Status = IsReady
                ? $"personal classifier • READY • {_training.Count} windows{keyboardNote}"
                : $"personal classifier • NEEDS TRAINING • N{fileCounts.GetValueOrDefault("Normal")} L{fileCounts.GetValueOrDefault("Laugh")} Y{fileCounts.GetValueOrDefault("Yawn")} H{fileCounts.GetValueOrDefault("Hmm")} K{fileCounts.GetValueOrDefault("Keyboard")}";
        }

        StatusChanged?.Invoke(Status);
    }

    public void ResetLiveState()
    {
        lock (_sync)
            ResetLiveStateUnsafe();
    }

    private void ResetLiveStateUnsafe()
    {
        _liveFrames.Clear();
        _pendingLiveSamples.Clear();
        _frameCounter = 0;
        _laughStreak = 0;
        _yawnStreak = 0;
        _hmmStreak = 0;
        _inputPackets = 0;
        _inputSamples = 0;
        _analysisFrames = 0;
        _classificationCount = 0;
        LastClassification = null;
    }

    public void PushAudio(byte[] pcm16)
    {
        if (!IsReady || pcm16.Length < 2)
            return;

        PersonalizedEventClassification? result = null;
        lock (_sync)
        {
            var samples = Pcm16ToFloat(pcm16);
            if (samples.Length == 0) return;
            _inputPackets++;
            _inputSamples += samples.Length;

            // WASAPI event-driven capture commonly delivers ~10 ms packets even when the
            // requested capture buffer is 40 ms. v0.7.2 assumed every callback already
            // contained a complete 640-sample / 40 ms analysis frame and silently dropped
            // shorter packets. Accumulate the stream here so no audio is discarded.
            _pendingLiveSamples.AddRange(samples);

            while (_pendingLiveSamples.Count >= FrameSamples)
            {
                var frame = _pendingLiveSamples.GetRange(0, FrameSamples).ToArray();
                _pendingLiveSamples.RemoveRange(0, FrameSamples);

                _liveFrames.Enqueue(AnalyzeFrame(frame));
                _analysisFrames++;
                while (_liveFrames.Count > MaxLiveFrames)
                    _liveFrames.Dequeue();

                _frameCounter++;
                if (_frameCounter % ClassifyEveryFrames != 0 || _liveFrames.Count < 12)
                    continue;

                var recent = _liveFrames.TakeLast(Math.Min(WindowFrames, _liveFrames.Count)).ToArray();
                result = ClassifyUnsafe(BuildVector(recent), DateTimeOffset.UtcNow);
                _classificationCount++;
                LastClassification = result;
            }
        }

        if (result is not null)
            ClassificationAvailable?.Invoke(result);
    }

    private PersonalizedEventClassification ClassifyUnsafe(double[] vector, DateTimeOffset now)
    {
        var z = Standardize(vector);
        var nearest = _training
            .Select(t => new { t.Label, Distance = Distance(z, Standardize(t.Vector)) })
            .OrderBy(x => x.Distance)
            .Take(Math.Min(9, _training.Count))
            .ToArray();

        var votes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in nearest)
        {
            var weight = 1.0 / (0.10 + item.Distance);
            votes[item.Label] = votes.GetValueOrDefault(item.Label) + weight;
        }

        var total = Math.Max(1e-9, votes.Values.Sum());
        double Score(string label) => votes.GetValueOrDefault(label) / total;

        var normal = Score("Normal");
        var noise = Score("Noise");
        var keyboard = Score("Keyboard");
        var laugh = Score("Laugh");
        var yawn = Score("Yawn");
        var hmm = Score("Hmm");

        var allScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["Normal"] = normal,
            ["Noise"] = noise,
            ["Keyboard"] = keyboard,
            ["Laugh"] = laugh,
            ["Yawn"] = yawn,
            ["Hmm"] = hmm
        };
        var top = allScores.OrderByDescending(kv => kv.Value).First();
        var negative = Math.Max(normal, Math.Max(noise, keyboard));

        // The most important protection for the bug that motivated v0.7.2: sharp, mostly
        // unvoiced input should not be eligible for Laugh even if its rhythm resembles chuckles.
        var voicedRatio = vector[5];
        var transientRatio = vector[9];
        var meanDb = vector[0];
        var activeEnough = meanDb > Math.Max(-72, NoiseFloorDb + 6.0);
        var keyboardLike = (keyboard >= 0.26 && keyboard >= laugh * 0.82) ||
                           (voicedRatio < 0.18 && transientRatio > 0.18);

        var laughPass = activeEnough && !keyboardLike && top.Key.Equals("Laugh", StringComparison.OrdinalIgnoreCase) &&
                        laugh >= 0.46 && laugh - negative >= 0.08;
        var yawnPass = activeEnough && voicedRatio >= 0.24 && top.Key.Equals("Yawn", StringComparison.OrdinalIgnoreCase) &&
                       yawn >= 0.44 && yawn - negative >= 0.06;
        var hmmPass = activeEnough && voicedRatio >= 0.42 && top.Key.Equals("Hmm", StringComparison.OrdinalIgnoreCase) &&
                      hmm >= 0.48 && hmm - negative >= 0.08;

        _laughStreak = laughPass ? _laughStreak + 1 : 0;
        _yawnStreak = yawnPass ? _yawnStreak + 1 : 0;
        _hmmStreak = hmmPass ? _hmmStreak + 1 : 0;

        var laughDetected = _laughStreak >= 2;
        var yawnDetected = _yawnStreak >= 4;
        var hmmDetected = _hmmStreak >= 3;

        var detail = $"top={top.Key}:{top.Value:F3}; normal={normal:F3}; noise={noise:F3}; keyboard={keyboard:F3}; laugh={laugh:F3}; yawn={yawn:F3}; hmm={hmm:F3}; voiced={voicedRatio:F2}; transient={transientRatio:F2}; keyboardLike={keyboardLike}";

        return new PersonalizedEventClassification(
            now,
            top.Key,
            top.Value,
            normal,
            noise,
            keyboard,
            laugh,
            yawn,
            hmm,
            laughDetected,
            yawnDetected,
            hmmDetected,
            detail);
    }

    private void BuildStandardization()
    {
        var count = _training[0].Vector.Length;
        _mean = new double[count];
        _std = new double[count];

        for (var i = 0; i < count; i++)
        {
            _mean[i] = _training.Average(v => v.Vector[i]);
            var variance = _training.Average(v => Math.Pow(v.Vector[i] - _mean[i], 2));
            _std[i] = Math.Max(Math.Sqrt(variance), FeatureMinimumStd(i));
        }
    }

    private double[] Standardize(double[] vector)
    {
        if (_mean.Length != vector.Length || _std.Length != vector.Length)
            return vector.ToArray();
        var result = new double[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            result[i] = (vector[i] - _mean[i]) / _std[i];
        return result;
    }

    private static double FeatureMinimumStd(int index) => index switch
    {
        0 => 2.0,   // mean dB
        1 => 0.8,   // dB spread
        2 => 1.0,   // dynamic range
        3 => 0.01,  // zcr
        4 => 0.01,
        5 => 0.05,  // voiced ratio
        6 => 0.05,  // clarity
        7 => 0.03,  // pitch cv
        8 => 0.05,  // energy cv
        9 => 0.04,  // transients
        10 => 0.10, // burst rate
        11 => 0.05, // crest
        _ => 0.05
    };

    private static double Distance(double[] a, double[] b)
    {
        var count = Math.Min(a.Length, b.Length);
        if (count == 0) return 999;
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum / count);
    }

    private IEnumerable<double[]> ExtractTrainingVectors(string path, string label)
    {
        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var samples = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.Take(read));

        if (samples.Count < FrameSamples * 4)
            yield break;

        var frames = SplitFrames(samples.ToArray()).Select(AnalyzeFrame).ToArray();
        if (frames.Length < 8)
            yield break;

        bool Keep(double[] vector) =>
            label.Equals("Noise", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
            vector[0] > -66;

        if (frames.Length <= WindowFrames)
        {
            var vector = BuildVector(frames);
            if (Keep(vector)) yield return vector;
            yield break;
        }

        const int stride = 10; // 400 ms
        for (var start = 0; start + 12 <= frames.Length; start += stride)
        {
            var count = Math.Min(WindowFrames, frames.Length - start);
            if (count < 12) break;
            var vector = BuildVector(frames.Skip(start).Take(count).ToArray());
            if (Keep(vector)) yield return vector;
        }
    }

    private static IEnumerable<float[]> SplitFrames(float[] samples)
    {
        for (var offset = 0; offset < samples.Length; offset += FrameSamples)
        {
            var count = Math.Min(FrameSamples, samples.Length - offset);
            if (count < FrameSamples / 2) yield break;
            var frame = new float[count];
            Array.Copy(samples, offset, frame, 0, count);
            yield return frame;
        }
    }

    private static FrameStats AnalyzeFrame(float[] samples)
    {
        double sumSquares = 0;
        double peak = 0;
        var zeroCrossings = 0;
        float previous = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            sumSquares += sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
            if (i > 0 && ((sample >= 0 && previous < 0) || (sample < 0 && previous >= 0)))
                zeroCrossings++;
            previous = sample;
        }

        var rms = Math.Sqrt(sumSquares / Math.Max(1, samples.Length));
        var db = 20.0 * Math.Log10(Math.Max(rms, 1e-9));
        var zcr = samples.Length > 1 ? (double)zeroCrossings / (samples.Length - 1) : 0;
        var (pitch, clarity) = EstimatePitch(samples, SampleRate);
        return new FrameStats(db, rms, peak, zcr, pitch, clarity);
    }

    // Vector layout:
    // 0 mean dB, 1 dB std, 2 dynamic range, 3 mean ZCR, 4 ZCR std,
    // 5 voiced ratio, 6 mean pitch clarity, 7 pitch CV, 8 energy CV,
    // 9 transient ratio, 10 burst rate, 11 crest factor.
    private static double[] BuildVector(FrameStats[] frames)
    {
        var dbs = frames.Select(f => f.Db).ToArray();
        var rms = frames.Select(f => f.Rms).ToArray();
        var zcr = frames.Select(f => f.Zcr).ToArray();
        var activeCut = Math.Max(-62, dbs.Max() - 18);
        var active = frames.Where(f => f.Db >= activeCut).ToArray();
        if (active.Length == 0) active = frames;

        var voiced = active.Where(f => f.Clarity >= 0.42 && f.Pitch is >= 65 and <= 360).ToArray();
        var voicedRatio = voiced.Length / (double)Math.Max(1, active.Length);
        var clarityMean = active.Average(f => f.Clarity);

        var pitchCv = 1.0;
        if (voiced.Length >= 2)
        {
            var meanPitch = voiced.Average(f => f.Pitch);
            var stdPitch = Math.Sqrt(voiced.Average(f => Math.Pow(f.Pitch - meanPitch, 2)));
            pitchCv = meanPitch <= 1 ? 1 : Math.Min(2, stdPitch / meanPitch);
        }

        var meanRms = rms.Average();
        var rmsStd = Math.Sqrt(rms.Average(v => Math.Pow(v - meanRms, 2)));
        var energyCv = meanRms <= 1e-8 ? 2 : Math.Min(3, rmsStd / meanRms);

        var transientCount = 0;
        var burstCount = 0;
        var burstThreshold = Math.Max(-58, dbs.Average() + 4.0);
        var wasHigh = false;
        for (var i = 1; i < frames.Length; i++)
        {
            if (Math.Abs(dbs[i] - dbs[i - 1]) >= 7.0) transientCount++;
            var high = dbs[i] >= burstThreshold;
            if (high && !wasHigh) burstCount++;
            wasHigh = high;
        }

        var durationSeconds = frames.Length * 0.04;
        var meanPeak = active.Average(f => f.Peak);
        var activeMeanRms = active.Average(f => f.Rms);
        var crest = activeMeanRms <= 1e-8 ? 0 : Math.Min(12, meanPeak / activeMeanRms);

        return
        [
            dbs.Average(),
            StdDev(dbs),
            dbs.Max() - dbs.Min(),
            zcr.Average(),
            StdDev(zcr),
            voicedRatio,
            clarityMean,
            pitchCv,
            energyCv,
            transientCount / (double)Math.Max(1, frames.Length - 1),
            burstCount / Math.Max(0.2, durationSeconds),
            crest
        ];
    }

    private static double StdDev(double[] values)
    {
        if (values.Length <= 1) return 0;
        var mean = values.Average();
        return Math.Sqrt(values.Average(v => Math.Pow(v - mean, 2)));
    }

    private static float[] Pcm16ToFloat(byte[] pcm16)
    {
        var count = pcm16.Length / 2;
        var result = new float[count];
        for (var i = 0; i < count; i++)
            result[i] = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
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

        return bestLag == 0 ? (0, 0) : ((double)sampleRate / bestLag, Math.Clamp(best, 0, 1));
    }

    private sealed record TrainingVector(string Label, double[] Vector);
    private readonly record struct FrameStats(double Db, double Rms, double Peak, double Zcr, double Pitch, double Clarity);
}
