namespace Erasa.Video2.Core.Models;

public sealed class RuntimeStatus
{
    public RuntimeState State { get; set; } = RuntimeState.Missing;
    public string Profile { get; set; } = "none";
    public string? Message { get; set; }
    public string? RuntimeDirectory { get; set; }
    public bool FfmpegReady { get; set; }
    public bool PythonReady { get; set; }
    public bool ModelReady { get; set; }
    public bool OriginalSourceReady { get; set; }

    public bool IsReady => State == RuntimeState.Ready && PythonReady && ModelReady && OriginalSourceReady;
}
