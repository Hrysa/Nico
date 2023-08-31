using Nico.Core;

namespace Single.Model;

public class Creator : ICreator
{
    public void Build()
    {
        new World().StartAsync();
        Console.WriteLine("Creator Single Build");
    }

    public void OnExit()
    {
        Console.WriteLine("Creator Single OnExit");
    }
}