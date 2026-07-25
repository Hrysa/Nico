using System.Numerics;
using Editor;
using Engine.Graphics;
using Engine.UI;
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
var options = new WindowOptions
{
    Title = "Game Engine Editor",
    Width = width,
    Height = height
};

logger.LogInformation("Initializing window...");
window.Initialize(options);

logger.LogInformation("Setting up editor UI...");
var uiRoot = EditorUI.BuildUI(width, height);
window.SetVertices(uiRoot.CollectVertices().ToArray());
window.SetPushConstants(EditorUI.CreatePushConstants(width, height));
window.CreateVertexBuffer();

UIElement? hoveredElement = null;
bool mouseIsDown = false;

void HitTest(Vector2 mousePos)
{
    var hit = HitTestElement(uiRoot, mousePos);

    if (hit != hoveredElement)
    {
        hoveredElement?.SetHover(false);
        hoveredElement = hit;
        hoveredElement?.SetHover(true);
        logger.LogDebug("Hover: {Name}", hoveredElement?.Name ?? "(none)");
    }
}

UIElement? HitTestElement(UIElement element, Vector2 pos)
{
    if (!element.IsVisible || !element.ContainsPoint(pos))
        return null;

    // Check children back-to-front (last child = topmost)
    for (int i = element.Children.Count - 1; i >= 0; i--)
    {
        if (element.Children[i] is UIElement child)
        {
            var childHit = HitTestElement(child, pos);
            if (childHit != null)
                return childHit;
        }
    }

    return element;
}

window.MouseMove += pos =>
{
    logger.LogTrace("Mouse: ({X:F0}, {Y:F0})", pos.X, pos.Y);
    HitTest(pos);
};

window.MouseDown += button =>
{
    mouseIsDown = true;
    logger.LogDebug("MouseDown: button={Button}", button);
    hoveredElement?.SetPressed(true);
};

window.MouseUp += button =>
{
    mouseIsDown = false;
    logger.LogDebug("MouseUp: button={Button}", button);
    if (hoveredElement != null)
    {
        hoveredElement.SetPressed(false);
        hoveredElement.InvokeClick();
    }
};

logger.LogInformation("Running main loop...");
window.Run();
logger.LogInformation("Done.");
