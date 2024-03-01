namespace Nico.Core;

public class World
{
    private readonly Dictionary<long, Entity> Entitys = new();
    private readonly Dictionary<long, Component> Components = new();
    private readonly Dictionary<long, Archetype> Archetypes = new();

    private readonly Dictionary<long, ArchetypeValue> EntityComponentIds = new();
    private readonly Dictionary<long, Archetype> EntityArchetypeIndex = new();
    private readonly Dictionary<ArchetypeValue, Archetype> ArchetypeIndex = new();

    private readonly IList<ISystem> Systems = new List<ISystem>();

    private readonly Dictionary<Type, Ref<long>> IdPool = new();

    private long Frame = 0;

    public Entity CreateEntity()
    {
        var entity = new Entity(this);
        Entitys.Add(entity.Id, entity);

        return entity;
    }

    public Entity CreateEntity(Archetype archetype)
    {
        throw new NotImplementedException();
    }

    public Archetype CreateArchetype()
    {
        var archtype = new Archetype(this);
        Archetypes.Add(archtype.Id, archtype);

        return archtype;
    }

    public void RegisterSystem<T>() where T : ISystem
    {
        RegisterSystem(typeof(T));
    }

    public void RegisterSystem(Type type)
    {
        var system = Activator.CreateInstance(type) as ISystem;
        system!.Created();
        Systems.Add(system);
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

    public void Update()
    {
        Console.WriteLine($"World update {++Frame}");
    }
}
