using System.IO.Compression;

namespace FuriXeus.Tuber.Native.Services;

public sealed class ModelInstaller
{
    public const string ModelFolderName = "vosk-model-small-en-us-0.15";
    public const string ModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";

    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FuriXeus",
        "Tuber");

    public static string ModelsRoot => Path.Combine(AppDataRoot, "Models");
    public static string ModelPath => Path.Combine(ModelsRoot, ModelFolderName);
    public static bool IsInstalled =>
        Directory.Exists(ModelPath) &&
        File.Exists(Path.Combine(ModelPath, "conf", "model.conf")) &&
        Directory.Exists(Path.Combine(ModelPath, "am")) &&
        Directory.Exists(Path.Combine(ModelPath, "graph"));

    public static string InstallationDetail
    {
        get
        {
            if (!Directory.Exists(ModelPath)) return $"model folder missing: {ModelPath}";
            if (!File.Exists(Path.Combine(ModelPath, "conf", "model.conf"))) return "model.conf missing";
            if (!Directory.Exists(Path.Combine(ModelPath, "am"))) return "acoustic model folder missing";
            if (!Directory.Exists(Path.Combine(ModelPath, "graph"))) return "graph folder missing";
            return "model files present";
        }
    }

    public async Task InstallAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModelsRoot);
        var zipPath = Path.Combine(ModelsRoot, ModelFolderName + ".zip");
        var tempExtract = Path.Combine(ModelsRoot, ".extracting");

        if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1L;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(zipPath))
            {
                var buffer = new byte[128 * 1024];
                long readTotal = 0;

                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    if (total > 0) progress?.Report(readTotal / (double)total);
                }

                await output.FlushAsync(cancellationToken);
            }

            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(zipPath, tempExtract, true);

            var extractedModel = Path.Combine(tempExtract, ModelFolderName);
            if (!Directory.Exists(extractedModel))
                extractedModel = Directory.GetDirectories(tempExtract).FirstOrDefault()
                    ?? throw new InvalidDataException("The downloaded model archive did not contain a model folder.");

            if (Directory.Exists(ModelPath)) Directory.Delete(ModelPath, true);
            Directory.Move(extractedModel, ModelPath);
            progress?.Report(1.0);
        }
        finally
        {
            if (Directory.Exists(tempExtract))
            {
                try { Directory.Delete(tempExtract, true); } catch { }
            }

            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { }
            }
        }
    }
}
