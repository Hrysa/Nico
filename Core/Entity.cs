namespace Nico.Core;

public class Entity
{
    public readonly long Id;
    public readonly World World;

    public Archetype Archetype => _archetype;
    private Archetype _archetype;

    public Entity(World world)
    {
        Id = world.GenerateId<Entity>();
        World = world;
        _archetype = new(world);
    }

    public void AddComponent<T>() where T : Component
    {
    }
}
