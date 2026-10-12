using System.Text.Json;
using System.Threading.Channels;

namespace FuriXeus.Tuber.Native.Services;

public sealed class VoskKeywordRecognizer : IAsyncDisposable
{
    private readonly Channel<byte[]> _audioFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(240)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private Vosk.Model? _model;
    private Vosk.VoskRecognizer? _recognizer;
    private string _lastPartial = "";
    private long _framesQueued;
    private long _framesProcessed;
    private long _bytesProcessed;
    private long _droppedFrames;
    private long _partialCount;
    private long _finalCount;
    private long _emptyFinalCount;
    private long _flushCount;

    public bool IsReady => _recognizer is not null;
    public string Status { get; private set; } = "MODEL OFFLINE";
    public string HealthSummary =>
        $"ready={IsReady}; queued={Interlocked.Read(ref _framesQueued)}; processed={Interlocked.Read(ref _framesProcessed)}; bytes={Interlocked.Read(ref _bytesProcessed)}; partials={Interlocked.Read(ref _partialCount)}; finals={Interlocked.Read(ref _finalCount)}; emptyFinals={Interlocked.Read(ref _emptyFinalCount)}; flushes={Interlocked.Read(ref _flushCount)}; dropped={Interlocked.Read(ref _droppedFrames)}";

    public event Action<string, bool>? TextObserved;
    public event Action<string>? StatusChanged;
    public event Action<string, string>? DiagnosticObserved;

    public void Initialize(string modelPath, IEnumerable<string> phrases)
    {
        ShutdownRecognizer();
        ResetCounters();

        if (!Directory.Exists(modelPath))
        {
            Status = "MODEL MISSING";
            StatusChanged?.Invoke(Status);
            DiagnosticObserved?.Invoke("INIT_MISSING", $"path={modelPath}");
            return;
        }

        try
        {
            Vosk.Vosk.SetLogLevel(-1);
            _model = new Vosk.Model(modelPath);
            _recognizer = new Vosk.VoskRecognizer(_model, 16000.0f);
            _recognizer.SetWords(false);
            _recognizer.SetPartialWords(false);

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => ProcessLoopAsync(_cts.Token));
            var triggerCount = phrases.Count(p => !string.IsNullOrWhiteSpace(p));
            Status = $"WORDS ARMED • {triggerCount} TRIGGERS";
            StatusChanged?.Invoke(Status);
            DiagnosticObserved?.Invoke("INIT_OK", $"path={modelPath}; triggers={triggerCount}; {HealthSummary}");
        }
        catch (Exception ex)
        {
            Status = $"WORD CORE ERROR: {ex.Message}";
            StatusChanged?.Invoke(Status);
            DiagnosticObserved?.Invoke("INIT_ERROR", ex.ToString());
            ShutdownRecognizer();
        }
    }

    public void PushAudio(byte[] pcm16Frame)
    {
        if (pcm16Frame.Length == 0) return;
        if (!IsReady)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }

        if (_audioFrames.Writer.TryWrite(pcm16Frame))
            Interlocked.Increment(ref _framesQueued);
        else
            Interlocked.Increment(ref _droppedFrames);
    }

    /// <summary>
    /// Forces Vosk to flush the short utterance immediately when the mic gate closes.
    /// This is important for one/two-word triggers such as "bruh", "what", and "nani",
    /// which can otherwise end before Vosk decides to emit a partial result.
    /// </summary>
    public void FlushUtterance()
    {
        if (!IsReady) return;
        Interlocked.Increment(ref _flushCount);
        _audioFrames.Writer.TryWrite(Array.Empty<byte>());
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _audioFrames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_audioFrames.Reader.TryRead(out var frame))
                {
                    var recognizer = _recognizer;
                    if (recognizer is null) return;

                    if (frame.Length == 0)
                    {
                        FlushRecognizer(recognizer, "VOICE_END");
                        continue;
                    }

                    var frameNo = Interlocked.Increment(ref _framesProcessed);
                    Interlocked.Add(ref _bytesProcessed, frame.Length);

                    if (recognizer.AcceptWaveform(frame, frame.Length))
                    {
                        var text = ReadField(recognizer.Result(), "text");
                        EmitFinal(text, "ENDPOINT");
                        _lastPartial = "";
                    }
                    else
                    {
                        var partial = ReadField(recognizer.PartialResult(), "partial");
                        if (!string.IsNullOrWhiteSpace(partial) && !string.Equals(partial, _lastPartial, StringComparison.Ordinal))
                        {
                            _lastPartial = partial;
                            Interlocked.Increment(ref _partialCount);
                            TextObserved?.Invoke(partial, false);
                            DiagnosticObserved?.Invoke("PARTIAL", $"'{partial}' | {HealthSummary}");
                        }
                    }

                    if (frameNo % 25 == 0)
                        DiagnosticObserved?.Invoke("HEALTH", HealthSummary);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = $"KEYWORD ERROR: {ex.Message}";
            StatusChanged?.Invoke(Status);
            DiagnosticObserved?.Invoke("PROCESS_ERROR", ex.ToString());
        }
    }

    private void FlushRecognizer(Vosk.VoskRecognizer recognizer, string source)
    {
        try
        {
            var text = ReadField(recognizer.FinalResult(), "text");
            EmitFinal(text, source);
            recognizer.Reset();
            _lastPartial = "";
        }
        catch (Exception ex)
        {
            DiagnosticObserved?.Invoke("FLUSH_ERROR", ex.ToString());
        }
    }

    private void EmitFinal(string text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Interlocked.Increment(ref _emptyFinalCount);
            DiagnosticObserved?.Invoke("EMPTY_FINAL", $"source={source}; {HealthSummary}");
            return;
        }

        Interlocked.Increment(ref _finalCount);
        TextObserved?.Invoke(text, true);
        DiagnosticObserved?.Invoke("FINAL", $"source={source}; text='{text}'; {HealthSummary}");
    }

    private static string ReadField(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value)
                ? value.GetString()?.Trim().ToLowerInvariant() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _framesQueued, 0);
        Interlocked.Exchange(ref _framesProcessed, 0);
        Interlocked.Exchange(ref _bytesProcessed, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _partialCount, 0);
        Interlocked.Exchange(ref _finalCount, 0);
        Interlocked.Exchange(ref _emptyFinalCount, 0);
        Interlocked.Exchange(ref _flushCount, 0);
    }

    private void ShutdownRecognizer()
    {
        try { _cts?.Cancel(); } catch { }
        try { _worker?.Wait(500); } catch { }
        _cts?.Dispose();
        _cts = null;
        _worker = null;
        _recognizer?.Dispose();
        _recognizer = null;
        _model?.Dispose();
        _model = null;
        _lastPartial = "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync();
        ShutdownRecognizer();
    }
}
