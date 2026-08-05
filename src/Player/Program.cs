using Engine;

const int Width = 1280;
const int Height = 720;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Player <game-project-root> <scene-path>");
    return 2;
}

using var window = EngineHost.CreateWindow("Game Engine Player", Width, Height);
window.LoadProjectScene(args[0], args[1]);
window.Run();
return 0;
