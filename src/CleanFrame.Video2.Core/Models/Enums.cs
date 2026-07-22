namespace CleanFrame.Video2.Core.Models;

public enum MaskTool { Rectangle, Ellipse, Brush, Eraser, Pan }
public enum ProcessingMode { Fast, Beautiful }
public enum JobKind { Detect, Preview, Full }
public enum JobStatus { Pending, Detecting, ReadyForMask, Queued, Running, Paused, Completed, Failed, Cancelled }
public enum WorkerEventKind { Ready, Progress, Detection, Completed, Failed, Cancelled, Pong, Log }
