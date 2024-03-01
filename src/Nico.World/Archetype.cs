namespace Nico.Core;

public class Archetype
{
    public readonly long Id;
    private ArchetypeValue _valueSet = new List<long>();

    public Archetype(World world)
    {
        Id = world.GenerateId<Archetype>();
    }
}
