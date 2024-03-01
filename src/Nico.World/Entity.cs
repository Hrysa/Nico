namespace Nico.Core;

public class Entity
{
    public readonly long Id;

    internal Entity(World world)
    {
        Id = world.GenerateId<Entity>();
    }

    public void AddComponent<T>() where T : Component
    {
    }
}
