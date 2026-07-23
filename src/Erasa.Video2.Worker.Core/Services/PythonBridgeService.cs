using Erasa.Video2.Core.Protocol;

namespace Erasa.Video2.Worker.Core.Services;

public sealed class PythonBridgeService(Action<WorkerMessage> emit)
{
    public async Task<WorkerMessage> RunAsync(
        string runtimeDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        Func<double, double>? progressMap = null)
    {
        var python = RuntimeInstaller.FindPython(runtimeDirectory);
        if (!File.Exists(python)) throw new InvalidOperationException("Python runtime chưa được cài.");
        var allArguments = new List<string> { "-X", "utf8", "-I", ToolPaths.BridgePath };
        allArguments.AddRange(arguments);
        WorkerMessage? completed = null;
        string? failed = null;
        var result = await ProcessRunner.RunAsync(
            python,
            allArguments,
            onOutput: line =>
            {
                var message = WorkerJson.Deserialize<WorkerMessage>(line);
                if (message is null) return;
                if (message.Progress is not null && progressMap is not null) message.Progress = progressMap(message.Progress.Value);
                if (message.IsCompleted) completed = message;
                if (message.IsFailed) failed = message.Error ?? message.Message;
                emit(message);
            },
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0 || failed is not null)
        {
            var detail = failed ?? result.StandardError;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "Python worker dừng bất thường." : detail.Trim());
        }
        return completed ?? new WorkerMessage { Kind = "completed", Message = "Hoàn thành." };
    }
}
