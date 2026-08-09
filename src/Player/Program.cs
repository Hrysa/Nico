using Engine;
using Engine.UI;
using PlayerApp;
using System.Numerics;

const int Width = 1280;
const int Height = 720;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Player <game-project-root> <scene-path>");
    return 2;
}

using var window = EngineHost.CreateWindow("Game Engine Player", Width, Height);
window.LoadProjectScene(args[0], args[1]);
if (!window.HasSceneHud)
{
    var hud = PlayerHud.Create(out var pauseMenu, out var worldSpaceUI);
    window.SetUI(hud, viewportPolicy: new ReferenceResolutionUIViewportPolicy
    {
        ReferenceResolution = new Vector2(Width, Height),
        PixelPerfect = true
    }, inputContext: UIInputContextMode.GameplayOnly,
    schedulingMode: UIHostSchedulingMode.Continuous);
    window.AttachPauseMenu(pauseMenu);
    window.AttachWorldSpaceUI(worldSpaceUI);
}
window.Run();
return 0;
