namespace Nico.Runtime.Hosting;

public static class StageBuilderExtensions
{
    public static IStageBuilder UseLocalhostClustering(
        this IStageBuilder builder,
        int stagePort = 10000,
        int gatewayPort = 20000)
    {
        return builder;
    }
}
