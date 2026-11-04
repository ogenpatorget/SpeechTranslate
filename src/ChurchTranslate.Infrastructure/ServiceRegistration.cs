using ChurchTranslate.Application;
using Microsoft.Extensions.DependencyInjection;

namespace ChurchTranslate.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class ServiceRegistration
{
    public static IServiceCollection AddChurchTranslateFoundation(this IServiceCollection services, string dataPath)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<PricingCalculator>();
        services.AddSingleton(new JsonStore(dataPath));
        services.AddSingleton<IChurchRepository>(sp => sp.GetRequiredService<JsonStore>());
        services.AddSingleton<IUserRepository>(sp => sp.GetRequiredService<JsonStore>());
        services.AddSingleton<IUsageRepository>(sp => sp.GetRequiredService<JsonStore>());
        services.AddScoped<AdminService>();
        return services;
    }

    public static IServiceCollection AddChurchTranslateEphemeralFoundation(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<PricingCalculator>();
        services.AddSingleton<MemoryStore>();
        services.AddSingleton<IChurchRepository>(sp => sp.GetRequiredService<MemoryStore>());
        services.AddSingleton<IUserRepository>(sp => sp.GetRequiredService<MemoryStore>());
        services.AddSingleton<IUsageRepository>(sp => sp.GetRequiredService<MemoryStore>());
        services.AddScoped<AdminService>();
        return services;
    }
}
