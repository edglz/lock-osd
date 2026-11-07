using System;

namespace LockOsd;

public enum OsdPosition
{
    Bottom,
    Top,
}

public enum OsdMonitor
{
    Active,
    Primary,
}

/// <summary>The schema of config.yaml. Missing values keep the defaults set here.</summary>
internal sealed class AppConfig
{
    public string Language { get; set; } = "auto";

    public KeysConfig Keys { get; set; } = new();

    public DisplayConfig Display { get; set; } = new();

    /// <summary>Replaces empty sections and brings numbers into a usable range.</summary>
    public AppConfig Normalized()
    {
        Language = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim();
        Keys ??= new KeysConfig();
        Display ??= new DisplayConfig();
        Display.DurationMs = Math.Clamp(Display.DurationMs, 300, 30000);
        Display.MarginDip = Math.Clamp(Display.MarginDip, 0, 1000);
        return this;
    }
}

internal sealed class KeysConfig
{
    public bool CapsLock { get; set; } = true;

    public bool NumLock { get; set; } = true;

    public bool ScrollLock { get; set; } = true;
}

public sealed class DisplayConfig
{
    public int DurationMs { get; set; } = 1600;

    public OsdPosition Position { get; set; } = OsdPosition.Bottom;

    public double MarginDip { get; set; } = 24;

    public OsdMonitor Monitor { get; set; } = OsdMonitor.Active;
}
