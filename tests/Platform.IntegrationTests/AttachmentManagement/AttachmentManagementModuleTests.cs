using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.AttachmentManagement;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Authorization;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.RateLimiting;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

[Collection(AttachmentManagementModuleFixtureGroup.Name)]
public sealed class AttachmentManagementModuleTests(TestApplicationFactory factory)
{
    [Fact]
    public void Module_is_discovered_as_a_service_registration_module()
    {
        Type[] modules = ModuleRegistrationExtensions
            .DiscoverImplementations(typeof(IModule), SolutionAssemblies.All)
            .ToArray();

        modules.ShouldContain(typeof(AttachmentManagementModule));
    }

    [Fact]
    public void Module_is_discovered_as_an_endpoint_module()
    {
        Type[] modules = ModuleRegistrationExtensions
            .DiscoverImplementations(typeof(IEndpointModule), SolutionAssemblies.All)
            .ToArray();

        modules.ShouldContain(typeof(AttachmentManagementModule));
    }

    /// <summary>
    /// Every producer route declares the bare requirement and resolves the
    /// grant against the resource inside the use case, because the resource
    /// arrives in the address or in the body. The count is frozen so a route
    /// that joins the tree has to be acknowledged here.
    /// </summary>
    [Fact]
    public async Task Endpoints_challenge_anonymous_callers_and_keep_the_named_resource_policy()
    {
        RouteEndpoint[] endpoints = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/v1/attachments",
                StringComparison.Ordinal) == true)
            .ToArray();

        // Two routes were added on purpose: asking for a verdict, and taking a
        // release back. Both are gated by the producer's grant over the exact
        // reference in the address, which is why both belong in this set and
        // carry the bare requirement like the three before them.
        endpoints.Length.ShouldBe(5);
        foreach (RouteEndpoint endpoint in endpoints)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => data.Policy is null)
                .ShouldBeTrue();
            endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()
                .ShouldNotBeNull()
                .PolicyName.ShouldBe(RateLimitingSetup.PolicyName);
        }

        IAuthorizationPolicyProvider policyProvider = factory.Services
            .GetRequiredService<IAuthorizationPolicyProvider>();
        AuthorizationPolicy policy = (await policyProvider.GetPolicyAsync(
            AuthorizationSetup.ProducerPolicyName)).ShouldNotBeNull();
        policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>()
            .ShouldHaveSingleItem();
        policy.Requirements.OfType<AttachmentProducerRequirement>()
            .ShouldHaveSingleItem();
    }

    /// <summary>
    /// The authorized reading is gated by a policy of its own, named at the
    /// route rather than resolved in the use case: there is no per-application
    /// scope to check for it, so the route is the whole gate and it has to say
    /// so out loud. A route that declared the producer's requirement instead
    /// would hand the fine detail of a refusal to the producer it was kept
    /// from.
    /// </summary>
    [Fact]
    public async Task The_operations_reading_declares_a_policy_of_its_own_and_not_the_producer_one()
    {
        RouteEndpoint endpoint = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText?.StartsWith(
                "/v1/attachment-operations",
                StringComparison.Ordinal) == true);

        IAuthorizeData[] authorization =
            [.. endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()];
        authorization.ShouldContain(data =>
            data.Policy == AuthorizationSetup.OperationsPolicyName);
        authorization.ShouldAllBe(data =>
            data.Policy != AuthorizationSetup.ProducerPolicyName);
        endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()
            .ShouldNotBeNull()
            .PolicyName.ShouldBe(RateLimitingSetup.PolicyName);

        IAuthorizationPolicyProvider policyProvider = factory.Services
            .GetRequiredService<IAuthorizationPolicyProvider>();
        AuthorizationPolicy policy = (await policyProvider.GetPolicyAsync(
            AuthorizationSetup.OperationsPolicyName)).ShouldNotBeNull();

        // The role is what the policy is, so the assertion names it. A policy
        // that stopped requiring it would still be a named policy, and every
        // shape assertion above would still pass.
        policy.Requirements.OfType<RolesAuthorizationRequirement>()
            .ShouldHaveSingleItem()
            .AllowedRoles
            .ShouldBe([AuthorizationSetup.OperationsRole]);
        policy.Requirements.OfType<AttachmentProducerRequirement>().ShouldBeEmpty();
    }

    [Fact]
    public void Host_resolves_the_module_context_with_its_owned_schema()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AttachmentManagementDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<AttachmentManagementDbContext>();

        dbContext.Model.GetDefaultSchema().ShouldBe("attachmentmanagement");
        dbContext.Database.ProviderName.ShouldBe("Npgsql.EntityFrameworkCore.PostgreSQL");

        IDbContextOptions contextOptions = dbContext.GetService<IDbContextOptions>();
        RelationalOptionsExtension relationalOptions = contextOptions.Extensions
            .OfType<RelationalOptionsExtension>()
            .ShouldHaveSingleItem();
        relationalOptions.MigrationsHistoryTableName.ShouldBe("__EFMigrationsHistory");
        relationalOptions.MigrationsHistoryTableSchema.ShouldBe("attachmentmanagement");
        contextOptions.Extensions
            .OfType<CoreOptionsExtension>()
            .ShouldHaveSingleItem()
            .IsSensitiveDataLoggingEnabled.ShouldBeFalse();

        IDbContextFactory<AttachmentManagementDbContext> contextFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<AttachmentManagementDbContext>>();
        using AttachmentManagementDbContext independentContext = contextFactory.CreateDbContext();
        independentContext.ShouldNotBeSameAs(dbContext);
        independentContext.Model.GetDefaultSchema().ShouldBe("attachmentmanagement");
    }

    [Fact]
    public void Persistence_options_bind_only_from_the_module_configuration_section()
    {
        const string expectedConnectionString =
            "Host=attachment-runtime;Port=5432;Database=attachment_runtime;Username=test";
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:AttachmentManagement:Persistence:Ef:ConnectionString"] =
                    expectedConnectionString,
                ["Modules:AttachmentManagement:Persistence:Ef:EnableSensitiveDataLogging"] = "false",
                ["Modules:AttachmentManagement:Persistence:Ef:EnableDetailedErrors"] = "false",
                ["Modules:Notifications:Persistence:Ef:ConnectionString"] =
                    "Host=wrong-runtime;Port=5432;Database=wrong_runtime;Username=test",
                ["Modules:Notifications:Persistence:Ef:EnableSensitiveDataLogging"] = "true",
                ["Modules:Notifications:Persistence:Ef:EnableDetailedErrors"] = "true",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAttachmentManagementPersistence(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        AttachmentManagementEfOptions options = provider
            .GetRequiredService<IOptions<AttachmentManagementEfOptions>>()
            .Value;

        AttachmentManagementEfOptions.SectionName
            .ShouldBe("Modules:AttachmentManagement:Persistence:Ef");
        options.ConnectionString.ShouldBe(expectedConnectionString);
        options.EnableSensitiveDataLogging.ShouldBeFalse();
        options.EnableDetailedErrors.ShouldBeFalse();
    }

    [Fact]
    public void Startup_validation_refuses_sensitive_data_logging()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:AttachmentManagement:Persistence:Ef:ConnectionString"] =
                    "Host=attachment-runtime;Database=attachment_runtime;Username=test",
                ["Modules:AttachmentManagement:Persistence:Ef:EnableSensitiveDataLogging"] = "true",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAttachmentManagementPersistence(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        failure.Failures.ShouldContain(message =>
            message.Contains(
                nameof(AttachmentManagementEfOptions.EnableSensitiveDataLogging),
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Startup_validation_refuses_an_unusable_connection_string(
        string? connectionString)
    {
        Dictionary<string, string?> settings = [];
        if (connectionString is not null)
        {
            settings["Modules:AttachmentManagement:Persistence:Ef:ConnectionString"] =
                connectionString;
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddAttachmentManagementPersistence(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        failure.Failures.ShouldContain(message =>
            message.Contains(nameof(AttachmentManagementEfOptions.ConnectionString),
                StringComparison.Ordinal));
    }

    [Fact]
    public void Design_time_factory_uses_the_module_connection_and_history_schema()
    {
        const string expectedConnectionString =
            "Host=attachment-factory;Port=5433;Database=attachment_factory;Username=test";
        DirectoryInfo temporaryDirectory = Directory.CreateTempSubdirectory(
            "notification-hub-attachment-");
        var originalDirectory = Directory.GetCurrentDirectory();

        try
        {
            var settings = $$"""
                {
                  "Modules": {
                    "AttachmentManagement": {
                      "Persistence": {
                        "Ef": {
                          "ConnectionString": "{{expectedConnectionString}}"
                        }
                      }
                    },
                    "Notifications": {
                      "Persistence": {
                        "Ef": {
                          "ConnectionString": "Host=wrong-factory;Port=5432;Database=wrong_factory;Username=test"
                        }
                      }
                    }
                  }
                }
                """;
            File.WriteAllText(
                Path.Combine(temporaryDirectory.FullName, "appsettings.json"),
                settings);
            Directory.SetCurrentDirectory(temporaryDirectory.FullName);

            var contextFactory = new AttachmentManagementDbContextFactory();
            using AttachmentManagementDbContext dbContext = contextFactory.CreateDbContext([]);

            dbContext.Database.ProviderName.ShouldBe("Npgsql.EntityFrameworkCore.PostgreSQL");
            dbContext.Database.GetConnectionString().ShouldBe(expectedConnectionString);

            IDbContextOptions contextOptions = dbContext.GetService<IDbContextOptions>();
            RelationalOptionsExtension relationalOptions = contextOptions.Extensions
                .OfType<RelationalOptionsExtension>()
                .ShouldHaveSingleItem();
            relationalOptions.MigrationsHistoryTableName.ShouldBe("__EFMigrationsHistory");
            relationalOptions.MigrationsHistoryTableSchema.ShouldBe("attachmentmanagement");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            temporaryDirectory.Delete(recursive: true);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AttachmentManagementModuleFixtureGroup
    : ICollectionFixture<TestApplicationFactory>
{
    public const string Name = "attachment-management-module";
}
