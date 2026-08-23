using FluentValidation;
using FluentValidation.Results;

namespace NotificationHub.Api.Infrastructure.EndpointFilters;

/// <summary>
/// Endpoint filter that resolves an <see cref="IValidator{T}"/> from DI and
/// short-circuits with <c>ValidationProblem</c> when the request fails validation.
/// No-op when no validator is registered for <typeparamref name="TRequest"/>.
/// </summary>
public sealed class ValidationFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        TRequest? request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return Results.BadRequest($"Request body of type {typeof(TRequest).Name} is required.");
        }

        IValidator<TRequest>? validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>();
        if (validator is null)
        {
            return await next(context);
        }

        ValidationResult result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (!result.IsValid)
        {
            return Results.ValidationProblem(result.ToDictionary());
        }

        return await next(context);
    }
}
