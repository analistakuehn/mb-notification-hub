using System.Reflection;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// A measurement of allocation and of collection pauses answers about the
/// collector it ran under. The probe therefore has to declare the collector the
/// API declares, and the place that says which one runs is the probe's own
/// runtime configuration, not a comment.
/// </summary>
public sealed class PerformanceProbeCollectorTests
{
    private const string ServerCollectorProperty = "System.GC.Server";

    [Fact]
    public void The_probe_declares_the_server_collector_in_its_runtime_configuration()
    {
        var path = Path.Combine(ProbeDirectory(), "NotificationHub.PerformanceTests.runtimeconfig.json");
        File.Exists(path).ShouldBeTrue($"a configuração de runtime da sonda não está em {path}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        document.RootElement
            .GetProperty("runtimeOptions")
            .GetProperty("configProperties")
            .GetProperty(ServerCollectorProperty)
            .GetBoolean()
            .ShouldBeTrue("a sonda mede sob um coletor diferente do que a API declara");
    }

    [Fact]
    public void The_api_and_the_probe_declare_the_same_collector()
    {
        var root = RepositoryRoot();
        var api = File.ReadAllText(Path.Combine(root, "src", "Platform.Api", "Platform.Api.csproj"));
        var probe = File.ReadAllText(
            Path.Combine(root, "tests", "Platform.PerformanceTests", "Platform.PerformanceTests.csproj"));

        api.ShouldContain("<ServerGarbageCollection>true</ServerGarbageCollection>");
        probe.ShouldContain("<ServerGarbageCollection>true</ServerGarbageCollection>");
    }

    private static string ProbeDirectory()
        => typeof(PerformanceProbeCollectorTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => string.Equals(
                attribute.Key, "PerformanceProbeDirectory", StringComparison.Ordinal))
            .Value!;

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "MonteBravo.NotificationHub.sln")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("a raiz do repositório não foi encontrada a partir do diretório do teste");
        return directory.FullName;
    }
}
