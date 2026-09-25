namespace Scanner.Server.Services;
public sealed record ServerSettings
{
    public int Port { get; init; } = 8765;
    public CameraSettings Camera { get; init; } = new();
    public bool LocalPreviewEnabled { get; init; } = true;
    public bool KeepCameraConnected { get; init; }
    public bool ShowDetails { get; init; }
    public void Validate()
    {
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be 1–65535.");
        if (Camera.Index < 0 || Camera.Width < 160 || Camera.Height < 120 || !double.IsFinite(Camera.Fps) || Camera.Fps is < 1 or > 120)
            throw new ArgumentException("Invalid camera index, dimensions or FPS.");
    }
}
public sealed record CameraSettings
{
    public int Index { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public double Fps { get; init; } = 30;
    public bool Autofocus { get; init; } = true;
    public double? Focus { get; init; }
    public bool AutoExposure { get; init; } = true;
    public double? Exposure { get; init; }
    public double? Gain { get; init; }
    public bool AutoWhiteBalance { get; init; } = true;
    public double? WhiteBalance { get; init; }
    public int? PowerLineFrequency { get; init; }
}
public sealed record CameraChoice(int Index, string Name);
