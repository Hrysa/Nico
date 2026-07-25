using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Engine.Core;

/// <summary>
/// Centralized debug logging with per-system conditional compilation.
/// Define symbols to enable: DEBUG_CORE, DEBUG_GRAPHICS, DEBUG_GRAPHICS_SILK, DEBUG_UI, DEBUG_EDITOR.
/// </summary>
public static class Debug
{
    private static ILoggerFactory? _loggerFactory;
    private static readonly Dictionary<string, ILogger> _loggers = new();

    /// <summary>
    /// Sets the logger factory for all debug logging. Call once at startup.
    /// </summary>
    /// <param name="factory">The logger factory to use.</param>
    public static void SetLoggerFactory(ILoggerFactory factory)
    {
        _loggerFactory = factory;
        _loggers.Clear();
    }

    private static ILogger GetLogger(string category)
    {
        if (_loggers.TryGetValue(category, out var logger))
            return logger;

        logger = _loggerFactory?.CreateLogger(category) ?? NullLogger.Instance;
        _loggers[category] = logger;
        return logger;
    }

    // ── Engine.Core ────────────────────────────────────────────

    /// <summary>Logs a message from the Core subsystem.</summary>
    /// <param name="level">The log level.</param>
    /// <param name="message">Log message with placeholders.</param>
    /// <param name="args">Format arguments.</param>
    [Conditional("DEBUG_CORE")]
    public static void Core(LogLevel level, string message, params object?[] args)
    {
        GetLogger("Engine.Core").Log(level, message, args);
    }

    // ── Engine.Graphics ───────────────────────────────────────

    /// <summary>Logs a message from the Graphics subsystem.</summary>
    /// <param name="level">The log level.</param>
    /// <param name="message">Log message with placeholders.</param>
    /// <param name="args">Format arguments.</param>
    [Conditional("DEBUG_GRAPHICS")]
    public static void Graphics(LogLevel level, string message, params object?[] args)
    {
        GetLogger("Engine.Graphics").Log(level, message, args);
    }

    // ── Engine.Graphics.Silk ──────────────────────────────────

    /// <summary>Logs a message from the Graphics.Silk subsystem.</summary>
    /// <param name="level">The log level.</param>
    /// <param name="message">Log message with placeholders.</param>
    /// <param name="args">Format arguments.</param>
    [Conditional("DEBUG_GRAPHICS_SILK")]
    public static void GraphicsSilk(LogLevel level, string message, params object?[] args)
    {
        GetLogger("Engine.Graphics.Silk").Log(level, message, args);
    }

    // ── Engine.UI ─────────────────────────────────────────────

    /// <summary>Logs a message from the UI subsystem.</summary>
    /// <param name="level">The log level.</param>
    /// <param name="message">Log message with placeholders.</param>
    /// <param name="args">Format arguments.</param>
    [Conditional("DEBUG_UI")]
    public static void UI(LogLevel level, string message, params object?[] args)
    {
        GetLogger("Engine.UI").Log(level, message, args);
    }

    // ── Editor ────────────────────────────────────────────────

    /// <summary>Logs a message from the Editor subsystem.</summary>
    /// <param name="level">The log level.</param>
    /// <param name="message">Log message with placeholders.</param>
    /// <param name="args">Format arguments.</param>
    [Conditional("DEBUG_EDITOR")]
    public static void Editor(LogLevel level, string message, params object?[] args)
    {
        GetLogger("Editor").Log(level, message, args);
    }

    // ── Input ─────────────────────────────────────────────────

    /// <summary>Logs a message from the Input subsystem.</summary>
    /// <param name="level">The log level.</param>
    /// <param name="message">Log message with placeholders.</param>
    /// <param name="args">Format arguments.</param>
    [Conditional("DEBUG_INPUT")]
    public static void Input(LogLevel level, string message, params object?[] args)
    {
        GetLogger("Input").Log(level, message, args);
    }
}
