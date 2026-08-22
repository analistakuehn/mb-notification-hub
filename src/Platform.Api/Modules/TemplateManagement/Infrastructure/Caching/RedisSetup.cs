using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Caching;

public static class RedisSetup
{
    public static IServiceCollection AddRedis(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            RedisOptions options = sp.GetRequiredService<IOptions<RedisOptions>>().Value;
            return ConnectionMultiplexer.Connect(options.ConnectionString);
        });

        services.AddStackExchangeRedisCache(redisOptions =>
        {
            IConfigurationSection section = configuration.GetRequiredSection(RedisOptions.SectionName);
            redisOptions.Configuration = section[nameof(RedisOptions.ConnectionString)];
            redisOptions.InstanceName = section[nameof(RedisOptions.InstanceName)];
        });

        return services;
    }
}
