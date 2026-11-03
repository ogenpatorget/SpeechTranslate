using ChurchTranslate.Application;
using ChurchTranslate.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ChurchTranslate.IntegrationTests;

public class FoundationTests
{
    [Fact]
    public void InfrastructureRegistersFoundationServices()
    {
        using var provider = new ServiceCollection()
            .AddChurchTranslateFoundation(Path.Combine(Path.GetTempPath(), "churchtranslate-test-" + Guid.NewGuid().ToString("N")))
            .BuildServiceProvider(validateScopes: true);

        Assert.NotNull(provider.GetRequiredService<IClock>());
        Assert.NotNull(provider.GetRequiredService<PricingCalculator>());
    }
}
