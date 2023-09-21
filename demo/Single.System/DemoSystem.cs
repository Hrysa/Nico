using Nico.Core;
using Nico.Core.Attributes;

namespace Single.Hotfix;

[AutoRegister]
public class DemoSystem : ISystem
{
    public void Created()
    {
        Console.WriteLine("DemoSystem Created");
    }
}
