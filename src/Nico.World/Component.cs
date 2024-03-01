namespace Nico.Core;

public class Component
{
    public readonly long Id;
    public readonly Entity Entity;

    public Component(Entity entity)
    {
        Entity = entity;
        // Id = entity.World.GenerateId<Component>();
    }
}

