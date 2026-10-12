using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FuriXeus.Tuber.Native.Services;

public sealed class TrainingRecorder : IDisposable
{
    private const int SampleRate = 16000;
    private readonly object _sync = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private WaveFileWriter? _writer;

    public string? CurrentPath { get; private set; }
    public bool IsRecording => _capture is not null;

    public event Action<string>? Faulted;

    public string Start(string deviceId, string category)
    {
        Stop();

        var safeCategory = string.Concat(category.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeCategory))
            safeCategory = "Sample";

        var folder = Path.Combine(ModelInstaller.AppDataRoot, "Training", safeCategory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{safeCategory.ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(candidate => string.Equals(candidate.ID, deviceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected Event Sense microphone is no longer available.");

#pragma warning disable CS0618
            var capture = new WasapiCapture(device, true, 40)
            {
                WaveFormat = new WaveFormat(SampleRate, 16, 1)
            };
#pragma warning restore CS0618

            var writer = new WaveFileWriter(path, capture.WaveFormat);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            _device = device;
            _writer = writer;
            _capture = capture;
            CurrentPath = path;
            return path;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public string? Stop()
    {
        var path = CurrentPath;
        var capture = _capture;
        var device = _device;
        _capture = null;
        _device = null;

        if (capture is not null)
        {
            try { capture.StopRecording(); } catch { }
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }

        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }

        device?.Dispose();
        CurrentPath = null;
        return path;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            lock (_sync)
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                _writer?.Flush();
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke($"Training capture failed: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Faulted?.Invoke($"Training capture stopped: {e.Exception.Message}");
    }

    public void Dispose() => Stop();
}
