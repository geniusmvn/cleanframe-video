using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erasa.Video2.Core.Models;
using Erasa.Video2.Core.Protocol;

namespace Erasa.Video2.Worker.Services;

public sealed class RuntimeManifest
{
    public string Version { get; set; } = "1";
    public DownloadItem Python { get; set; } = new();
    public DownloadItem GetPip { get; set; } = new();
    public DownloadItem LamaSource { get; set; } = new();
    public DownloadItem Model { get; set; } = new();
    public string[] BasePackages { get; set; } = [];
    public string TorchCpu { get; set; } = string.Empty;
    public string TorchCuda { get; set; } = string.Empty;
    public string TorchFindLinks { get; set; } = string.Empty;
}

public sealed class DownloadItem
{
    public string Url { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
}

public sealed class RuntimeInstaller(Action<WorkerMessage> emit)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(2) };

    public RuntimeStatus GetStatus(string runtimeDirectory)
    {
        var marker = Path.Combine(runtimeDirectory, "runtime.ready.json");
        var python = FindPython(runtimeDirectory);
        var sourceFile = Path.Combine(runtimeDirectory, "lama-source", "saicinpainting", "training", "modules", "ffc.py");
        var modelConfig = Path.Combine(runtimeDirectory, "model", "config.yaml");
        var modelCheckpoint = Path.Combine(runtimeDirectory, "model", "models", "best.ckpt");
        var status = new RuntimeStatus
        {
            RuntimeDirectory = runtimeDirectory,
            FfmpegReady = File.Exists(ToolPaths.FindFfmpeg("ffmpeg")),
            PythonReady = File.Exists(python),
            OriginalSourceReady = File.Exists(sourceFile),
            ModelReady = File.Exists(modelConfig) && File.Exists(modelCheckpoint)
        };
        if (File.Exists(marker) && status.PythonReady && status.OriginalSourceReady && status.ModelReady)
        {
            status.State = RuntimeState.Ready;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker, Encoding.UTF8));
                status.Profile = document.RootElement.TryGetProperty("profile", out var profile) ? profile.GetString() ?? "unknown" : "unknown";
                status.Message = "Bộ xử lý LaMa gốc đã sẵn sàng.";
            }
            catch (JsonException)
            {
                status.Profile = "unknown";
            }
        }
        else
        {
            status.State = Directory.Exists(runtimeDirectory) ? RuntimeState.Broken : RuntimeState.Missing;
            status.Message = status.State == RuntimeState.Missing
                ? "Chưa cài bộ xử lý LaMa."
                : "Bộ xử lý LaMa chưa đầy đủ; có thể cài lại.";
        }
        return status;
    }

    public async Task<RuntimeStatus> InstallAsync(string runtimeDirectory, string requestedProfile, CancellationToken cancellationToken)
    {
        var existing = GetStatus(runtimeDirectory);
        if (existing.IsReady && (string.Equals(requestedProfile, "auto", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(requestedProfile, existing.Profile, StringComparison.OrdinalIgnoreCase)))
        {
            emit(new WorkerMessage { Kind = "progress", Progress = 1, Message = "Runtime đã có sẵn trong cache.", Runtime = existing });
            return existing;
        }
        var manifest = JsonSerializer.Deserialize<RuntimeManifest>(await File.ReadAllTextAsync(ToolPaths.ManifestPath, cancellationToken), WorkerJson.Options)
                       ?? throw new InvalidOperationException("Runtime manifest không hợp lệ.");
        var profile = ResolveProfile(requestedProfile);
        Directory.CreateDirectory(runtimeDirectory);
        var downloadDirectory = Path.Combine(runtimeDirectory, "downloads");
        Directory.CreateDirectory(downloadDirectory);

        emit(new WorkerMessage { Kind = "progress", Progress = 0.01, Message = $"Đang chuẩn bị runtime {profile.ToUpperInvariant()}…" });

        var pythonZip = await DownloadAsync(manifest.Python, Path.Combine(downloadDirectory, "python.zip"), 0.02, 0.12, cancellationToken);
        var pythonDirectory = Path.Combine(runtimeDirectory, "python");
        RecreateDirectory(pythonDirectory);
        ZipFile.ExtractToDirectory(pythonZip, pythonDirectory, overwriteFiles: true);
        EnablePythonSitePackages(pythonDirectory);

        var getPipPath = await DownloadAsync(manifest.GetPip, Path.Combine(downloadDirectory, "get-pip.py"), 0.12, 0.14, cancellationToken);
        var pythonExe = FindPython(runtimeDirectory);
        await RunPythonAsync(pythonExe, [getPipPath, "--no-warn-script-location"], "Không cài được pip.", cancellationToken);

        emit(new WorkerMessage { Kind = "progress", Progress = 0.16, Message = profile == "cuda" ? "Đang cài PyTorch CUDA cho NVIDIA…" : "Đang cài PyTorch CPU…" });
        var torchPackage = profile == "cuda" ? manifest.TorchCuda : manifest.TorchCpu;
        await RunPythonAsync(
            pythonExe,
            ["-m", "pip", "install", "--no-cache-dir", "--disable-pip-version-check", torchPackage, "-f", manifest.TorchFindLinks],
            "Không cài được PyTorch.",
            cancellationToken);

        emit(new WorkerMessage { Kind = "progress", Progress = 0.36, Message = "Đang cài thư viện xử lý ảnh…" });
        var packages = new List<string> { "-m", "pip", "install", "--no-cache-dir", "--disable-pip-version-check", "--no-warn-script-location" };
        packages.AddRange(manifest.BasePackages);
        await RunPythonAsync(pythonExe, packages, "Không cài được thư viện Python.", cancellationToken);

        var sourceZip = await DownloadAsync(manifest.LamaSource, Path.Combine(downloadDirectory, "lama-source.zip"), 0.58, 0.68, cancellationToken);
        var sourceDirectory = Path.Combine(runtimeDirectory, "lama-source");
        ExtractSingleRootArchive(sourceZip, sourceDirectory);
        VerifyOriginalSource(sourceDirectory);

        var modelZip = await DownloadAsync(manifest.Model, Path.Combine(downloadDirectory, "big-lama.zip"), 0.69, 0.9, cancellationToken);
        var modelDirectory = Path.Combine(runtimeDirectory, "model");
        ExtractModelArchive(modelZip, modelDirectory);

        emit(new WorkerMessage { Kind = "progress", Progress = 0.92, Message = "Đang kiểm tra runtime LaMa gốc…" });
        var bridge = ToolPaths.BridgePath;
        var selfTest = await ProcessRunner.RunAsync(
            pythonExe,
            ["-X", "utf8", "-I", bridge, "selftest", "--runtime", runtimeDirectory, "--device", profile == "cuda" ? "cuda" : "cpu"],
            onOutput: line => ForwardPythonLine(line),
            cancellationToken: cancellationToken);
        if (selfTest.ExitCode != 0)
            throw new InvalidOperationException($"LaMa self-test thất bại. {selfTest.StandardError}".Trim());

        var marker = new
        {
            version = manifest.Version,
            profile,
            upstream = "advimman/lama",
            commit = "786f5936b27fb3dacd2b1ad799e4de968ea697e7",
            installedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, "runtime.ready.json"),
            JsonSerializer.Serialize(marker, WorkerJson.Options),
            Encoding.UTF8,
            cancellationToken);

        var status = GetStatus(runtimeDirectory);
        emit(new WorkerMessage { Kind = "progress", Progress = 1, Message = "Bộ xử lý LaMa gốc đã sẵn sàng.", Runtime = status });
        return status;
    }

    public static string FindPython(string runtimeDirectory)
        => Path.Combine(runtimeDirectory, "python", "python.exe");

    private async Task<string> DownloadAsync(DownloadItem item, string target, double progressStart, double progressEnd, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Url)) throw new InvalidOperationException("Thiếu URL trong runtime manifest.");
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        if (File.Exists(target) && await VerifyHashAsync(target, item.Sha256, cancellationToken)) return target;

        var temporary = target + ".partial";
        if (File.Exists(temporary)) File.Delete(temporary);
        using var response = await Http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        var buffer = new byte[1024 * 1024];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (total is > 0)
            {
                var fraction = received / (double)total.Value;
                emit(new WorkerMessage
                {
                    Kind = "progress",
                    Progress = progressStart + (progressEnd - progressStart) * fraction,
                    Message = $"Đang tải {Path.GetFileName(target)}… {fraction:P0}"
                });
            }
        }
        await output.FlushAsync(cancellationToken);
        if (!await VerifyHashAsync(temporary, item.Sha256, cancellationToken))
            throw new InvalidOperationException($"Checksum không khớp: {Path.GetFileName(target)}");
        File.Move(temporary, target, overwrite: true);
        return target;
    }

    private static async Task<bool> VerifyHashAsync(string path, string? expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        if (string.IsNullOrWhiteSpace(expected)) return new FileInfo(path).Length > 0;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase);
    }

    private void ForwardPythonLine(string line)
    {
        var message = WorkerJson.Deserialize<WorkerMessage>(line);
        if (message is not null) emit(message);
    }

    private static async Task RunPythonAsync(string pythonExe, IEnumerable<string> arguments, string failureMessage, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(pythonExe, arguments, cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{failureMessage} {result.StandardError}".Trim());
    }

    private static string ResolveProfile(string requested)
    {
        if (string.Equals(requested, "cpu", StringComparison.OrdinalIgnoreCase)) return "cpu";
        if (string.Equals(requested, "cuda", StringComparison.OrdinalIgnoreCase)) return "cuda";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi.exe",
                Arguments = "--query-gpu=name --format=csv,noheader",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (process is not null && process.WaitForExit(4000) && process.ExitCode == 0 && !string.IsNullOrWhiteSpace(process.StandardOutput.ReadToEnd()))
                return "cuda";
        }
        catch (Exception) { }
        return "cpu";
    }

    private static void EnablePythonSitePackages(string pythonDirectory)
    {
        var pth = Directory.EnumerateFiles(pythonDirectory, "python*._pth").FirstOrDefault()
                  ?? throw new FileNotFoundException("Không tìm thấy file cấu hình Python embedded.");
        var lines = File.ReadAllLines(pth).ToList();
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].TrimStart().StartsWith("#import site", StringComparison.Ordinal)) lines[index] = "import site";
        }
        if (!lines.Any(line => line.Trim() == "import site")) lines.Add("import site");
        File.WriteAllLines(pth, lines, Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(pythonDirectory, "Lib", "site-packages"));
    }

    private static void ExtractSingleRootArchive(string archive, string destination)
    {
        RecreateDirectory(destination);
        var temporary = destination + ".extract";
        RecreateDirectory(temporary);
        ZipFile.ExtractToDirectory(archive, temporary, overwriteFiles: true);
        var root = Directory.EnumerateDirectories(temporary).SingleOrDefault() ?? temporary;
        CopyDirectory(root, destination);
        Directory.Delete(temporary, recursive: true);
    }

    private static void ExtractModelArchive(string archive, string destination)
    {
        RecreateDirectory(destination);
        var temporary = destination + ".extract";
        RecreateDirectory(temporary);
        ZipFile.ExtractToDirectory(archive, temporary, overwriteFiles: true);
        var config = Directory.EnumerateFiles(temporary, "config.yaml", SearchOption.AllDirectories).FirstOrDefault()
                     ?? throw new InvalidOperationException("Model archive không có config.yaml.");
        var checkpoint = Directory.EnumerateFiles(temporary, "best.ckpt", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidOperationException("Model archive không có models/best.ckpt.");
        File.Copy(config, Path.Combine(destination, "config.yaml"), overwrite: true);
        Directory.CreateDirectory(Path.Combine(destination, "models"));
        File.Copy(checkpoint, Path.Combine(destination, "models", "best.ckpt"), overwrite: true);
        Directory.Delete(temporary, recursive: true);
    }

    private static void VerifyOriginalSource(string sourceDirectory)
    {
        var ffc = Path.Combine(sourceDirectory, "saicinpainting", "training", "modules", "ffc.py");
        var license = Path.Combine(sourceDirectory, "LICENSE");
        if (!File.Exists(ffc) || !File.ReadAllText(ffc).Contains("class FFCResNetGenerator", StringComparison.Ordinal))
            throw new InvalidOperationException("Source tải về không đúng cấu trúc advimman/lama.");
        if (!File.Exists(license)) throw new InvalidOperationException("Source LaMa thiếu LICENSE.");
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Copy(file, target, overwrite: true);
        }
    }
}
