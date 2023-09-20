namespace Nico.Core;

public class Entity
{
    public readonly long Id;

    private List<IComponent> Components = new();
}
