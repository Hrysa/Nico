namespace Nico.Core;

public interface ICreator
{
    World Build();

    Task OnExit();
}
