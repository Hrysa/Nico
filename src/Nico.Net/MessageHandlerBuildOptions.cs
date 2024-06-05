using System.Reflection;
using Nico.Net.Abstractions;

namespace Nico.Net;

public class MessageHandlerBuildOptions
{
    public void Scan(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            Console.WriteLine(type);
        }
    }
}
