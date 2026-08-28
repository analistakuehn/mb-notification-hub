namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class DiffClassPolicyVersions
{
    internal sealed record Query(string Application, string Class, int Version, int AgainstVersion);
}
