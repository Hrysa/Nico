using Nico.Core;

namespace Single.Model;

public class Creator : ICreator
{
    public World Build()
    {
        var world = new World();
        Console.WriteLine("Creator new world");

        var entity = world.CreateEntity();
        entity.AddComponent<Component>();

        return world;
    }

    public async Task OnExit()
    {
        await Task.CompletedTask;

        Console.WriteLine("Creator Single OnExit");
    }
}
