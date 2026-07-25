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
var options = new Engine.Graphics.WindowOptions
{
    Title = "Game Engine Editor",
    Width = 1280,
    Height = 720
};

logger.LogInformation("Initializing window...");
window.Initialize(options);

logger.LogInformation("Setting up geometry...");
window.SetVertices(Triangle.CreateVertices());
window.SetPushConstants(Triangle.CreatePushConstants());
window.CreateVertexBuffer();

logger.LogInformation("Running main loop...");
window.Run();
logger.LogInformation("Done.");
