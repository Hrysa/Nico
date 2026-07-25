using Editor;
using Engine.Graphics;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddConsole();
    b.SetMinimumLevel(LogLevel.Trace);
});

var logger = loggerFactory.CreateLogger<Program>();
logger.LogInformation("Starting Editor...");

var window = new SilkWindow(loggerFactory);
var width = 1280;
var height = 720;
var options = new Engine.Graphics.WindowOptions
{
    Title = "Game Engine Editor",
    Width = width,
    Height = height
};

logger.LogInformation("Initializing window...");
window.Initialize(options);

logger.LogInformation("Setting up editor UI...");
window.SetVertices(EditorGeometry.CreateVertices(width, height));
window.SetPushConstants(EditorGeometry.CreatePushConstants(width, height));
window.CreateVertexBuffer();

logger.LogInformation("Running main loop...");
window.Run();
logger.LogInformation("Done.");
