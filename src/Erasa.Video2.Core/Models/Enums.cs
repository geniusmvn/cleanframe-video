namespace Erasa.Video2.Core.Models;

public enum MediaKind
{
    Video,
    Image
}

public enum JobState
{
    Added,
    LoadingPreview,
    NeedsMask,
    MaskDirty,
    MaskConfirmed,
    Previewing,
    Ready,
    Processing,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum MaskTool
{
    Brush,
    Eraser,
    Rectangle,
    Ellipse,
    Pan
}

public enum QualityMode
{
    Fast,
    Beautiful
}

public enum RuntimeState
{
    Missing,
    Installing,
    Ready,
    Broken
}
