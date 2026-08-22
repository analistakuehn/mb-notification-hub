namespace NotificationHub.Api.Composition;

/// <summary>Endpoint mapping contract for one bounded context.</summary>
public interface IEndpointModule
{
    static abstract void MapEndpoints(IEndpointRouteBuilder app);
}
