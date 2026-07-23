using Erasa.Video2.Core.Protocol;

namespace Erasa.Video2.Worker.Services;

public sealed class WorkerHost
{
    private readonly Action<WorkerMessage> _emit;
    private readonly FfmpegService _ffmpeg;
    private readonly RuntimeInstaller _runtime;
    private readonly PythonBridgeService _bridge;
    private readonly VideoPipelineService _pipeline;

    public WorkerHost(Action<WorkerMessage> emit)
    {
        _emit = emit;
        _ffmpeg = new FfmpegService();
        _runtime = new RuntimeInstaller(emit);
        _bridge = new PythonBridgeService(emit);
        _pipeline = new VideoPipelineService(emit, _ffmpeg, _bridge);
    }

    public async Task ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        WorkerMessage result = request.Command switch
        {
            WorkerCommands.Probe => await ProbeAsync(request, cancellationToken),
            WorkerCommands.Thumbnail => await ThumbnailAsync(request, cancellationToken),
            WorkerCommands.RuntimeStatus => RuntimeStatus(request),
            WorkerCommands.RuntimeInstall => await RuntimeInstallAsync(request, cancellationToken),
            WorkerCommands.Suggest => await _pipeline.SuggestAsync(request, cancellationToken),
            WorkerCommands.Preview => await _pipeline.PreviewAsync(request, cancellationToken),
            WorkerCommands.Process => await _pipeline.ProcessAsync(request, cancellationToken),
            WorkerCommands.SelfTestUtilities => await SelfTestUtilitiesAsync(request, cancellationToken),
            WorkerCommands.SelfTestRuntime => await SelfTestRuntimeAsync(request, cancellationToken),
            _ => throw new InvalidOperationException($"Worker command không được hỗ trợ: {request.Command}")
        };
        _emit(result);
    }

    private async Task<WorkerMessage> ProbeAsync(WorkerRequest request, CancellationToken cancellationToken)
        => await _ffmpeg.ProbeAsync(RequireFile(request.InputPath), cancellationToken);

    private async Task<WorkerMessage> ThumbnailAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        var input = RequireFile(request.InputPath);
        var output = RequirePath(request.OutputPath);
        await _ffmpeg.CreateThumbnailAsync(input, output, request.StartSeconds, cancellationToken);
        var probe = await _ffmpeg.ProbeAsync(input, cancellationToken);
        probe.ThumbnailPath = output;
        probe.OutputPath = output;
        probe.Message = "Đã tạo thumbnail.";
        return probe;
    }

    private WorkerMessage RuntimeStatus(WorkerRequest request)
    {
        var directory = RequirePath(request.RuntimeDirectory);
        var status = _runtime.GetStatus(directory);
        return new WorkerMessage { Kind = "completed", Message = status.Message, Runtime = status };
    }

    private async Task<WorkerMessage> RuntimeInstallAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        var directory = RequirePath(request.RuntimeDirectory);
        var status = await _runtime.InstallAsync(directory, request.Profile ?? "auto", cancellationToken);
        return new WorkerMessage { Kind = "completed", Progress = 1, Message = status.Message, Runtime = status };
    }

    private async Task<WorkerMessage> SelfTestUtilitiesAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        var directory = RequirePath(request.JobDirectory ?? request.OutputPath);
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "fixture.mp4");
        var thumbnail = Path.Combine(directory, "fixture.png");
        await _ffmpeg.CreateSyntheticVideoAsync(video, cancellationToken);
        var probe = await _ffmpeg.ProbeAsync(video, cancellationToken);
        await _ffmpeg.CreateThumbnailAsync(video, thumbnail, 1, cancellationToken);
        if (probe.Width != 320 || probe.Height != 180 || probe.HasAudio != true || !File.Exists(thumbnail))
            throw new InvalidOperationException("FFmpeg utility self-test không đạt.");
        return new WorkerMessage
        {
            Kind = "completed",
            Message = "FFmpeg utility self-test đạt.",
            OutputPath = video,
            ThumbnailPath = thumbnail,
            Width = probe.Width,
            Height = probe.Height,
            FramesPerSecond = probe.FramesPerSecond,
            DurationSeconds = probe.DurationSeconds,
            HasAudio = probe.HasAudio
        };
    }

    private async Task<WorkerMessage> SelfTestRuntimeAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        var runtime = RequirePath(request.RuntimeDirectory);
        var status = _runtime.GetStatus(runtime);
        if (!status.IsReady) throw new InvalidOperationException(status.Message ?? "Runtime chưa sẵn sàng.");
        return await _bridge.RunAsync(
            runtime,
            ["selftest", "--runtime", runtime, "--device", request.Profile ?? "cpu"],
            cancellationToken);
    }

    private static string RequireFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Không tìm thấy tệp đầu vào.", path);
        return path;
    }

    private static string RequirePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Thiếu đường dẫn trong worker request.");
        return path;
    }
}
