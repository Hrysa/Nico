using Engine.Graphics;

namespace Engine;

/// <summary>
/// Creates the default engine runtime using the bundled Silk.NET graphics backend.
/// </summary>
public static class EngineHost
{
    /// <summary>
    /// Creates and initializes the default game window.
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="width">Initial client width.</param>
    /// <param name="height">Initial client height.</param>
    /// <returns>An initialized engine application.</returns>
    public static EngineApplication CreateWindow(string title, int width, int height)
    {
        var window = new SilkWindow();
        window.Initialize(new WindowOptions { Title = title, Width = width, Height = height });
        return new EngineApplication(window);
    }
}

/// <summary>
/// Owns the runtime services needed to run a game application.
/// </summary>
public sealed class EngineApplication : IDisposable
{
    private readonly IWindow _window;

    /// <summary>
    /// Creates an application around an initialized window.
    /// </summary>
    /// <param name="window">Initialized engine window.</param>
    internal EngineApplication(IWindow window)
    {
        _window = window;
    }

    /// <summary>
    /// Runs the application until its window closes.
    /// </summary>
    public void Run()
    {
        _window.Run();
    }

    /// <summary>
    /// Releases the application and its runtime services.
    /// </summary>
    public void Dispose()
    {
        _window.Dispose();
    }
}
