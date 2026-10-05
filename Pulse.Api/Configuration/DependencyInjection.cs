using Pulse.Application.Common.Interfaces;

namespace Pulse.Api.Configuration;

public static class DependencyInjection
{
    public static IServiceCollection AddPulseServices(this IServiceCollection services)
    {
        var settings = AppSettings.FromEnvironment();

        services.AddSingleton<IAppSettings>(settings);
        services.AddPulseInfrastructure(settings);
        services.AddPulseAuth(settings);
        services.AddPulseSignalR();
        services.AddPulseHangfire(settings);
        services.AddPulseApiVersioning();
        services.AddPulseMediatR();
        services.AddPulseValidation();
        services.AddPulseSwagger();
        services.AddPulseHealthChecks(settings);
        services.AddPulseForwardedHeaders();
        services.AddPulseCors(settings);
        services.AddPulseRateLimiting();

        return services;
    }
}
