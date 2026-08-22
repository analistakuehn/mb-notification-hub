using Microsoft.AspNetCore.Authorization;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.EndpointFilters;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();
builder.Services.AddAuthentication();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddRateLimiter(options =>
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
builder.Services.AddModules(builder.Configuration, SolutionAssemblies.All);

builder.Services.AddScoped<RequestLoggingFilter>();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapOpenApi();

app.MapModuleEndpoints(SolutionAssemblies.All);

await app.RunAsync();

public partial class Program;
