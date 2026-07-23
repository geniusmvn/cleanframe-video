using System.Diagnostics;
using System.Text;

namespace Erasa.Video2.Worker.Core.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"Không khởi động được {executable}.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        });

        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputTask = ReadLinesAsync(process.StandardOutput, output, onOutput, cancellationToken);
        var errorTask = ReadLinesAsync(process.StandardError, error, null, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);
        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        StringBuilder target,
        Action<string>? callback,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            target.AppendLine(line);
            callback?.Invoke(line);
        }
    }
}
