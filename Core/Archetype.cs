namespace Nico.Core;

public class Archetype
{
    private readonly long Id;
    private HashSet<long> _valueSet = new();

    public Archetype(World world)
    {
        Id = world.GenerateId<Archetype>();
    }
}
