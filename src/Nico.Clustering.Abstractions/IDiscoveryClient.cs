namespace Nico.Discovery.Abstractions;

public interface IDiscoveryClient
{
    public void Connect();
    public void Disconnect();
}
