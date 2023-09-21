namespace Nico.Core;

public class World
{
    private Dictionary<long, Entity> Entitys = new();
    private Dictionary<long, Component> Components = new();
    private Dictionary<long, Archetype> Archetypes = new();

    private Dictionary<long, ArchetypeValue> EntityComponentIds = new();
    private Dictionary<long, Archetype> EntityArcheTypeIndex = new();

    private Dictionary<ArchetypeValue, Archetype> ArchetypeIndex = new();

    private Dictionary<Type, Ref<long>> IdPool = new();

    public Entity CreateEntity()
    {
        var entity = new Entity(this);
        Entitys.Add(entity.Id, entity);

        return entity;
    }

    public long GenerateId<T>()
    {
        var type = typeof(T);
        if (!IdPool.TryGetValue(type, out var idRef))
        {
            idRef = new(0);
            IdPool[type] = idRef;
        }

        return Interlocked.Increment(ref idRef.DeRef);
    }
}
