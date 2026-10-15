#if MAUI_DEVFLOW
using Microsoft.Maui.DevFlow.Agent;
#endif

namespace QuickPark;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiMaps();

#if MAUI_DEVFLOW
        builder.AddMauiDevFlowAgent();
#endif

        return builder.Build();
    }
}
