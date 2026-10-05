using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Pulse.Api.Configuration;

public static class HostBuilderExtensions
{
    public static IHostBuilder AddPulseLogging(this IHostBuilder host) =>
        host.UseSerilog((ctx, cfg) =>
        {
            cfg.Enrich.FromLogContext();

            if (ctx.HostingEnvironment.IsDevelopment())
            {
                cfg.MinimumLevel.Debug()
                   .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Information)
                   .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Infrastructure", LogEventLevel.Warning)
                   .WriteTo.Console();
            }
            else
            {
                cfg.MinimumLevel.Information()
                   .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                   .MinimumLevel.Override("Hangfire", LogEventLevel.Warning)
                   .WriteTo.Console(new JsonFormatter());
            }
        });
}
