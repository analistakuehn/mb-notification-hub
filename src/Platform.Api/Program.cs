using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.EndpointFilters;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();

// Bearer options bind from the "Authentication:Schemes:Bearer" configuration
// section. Development and tests supply symmetric signing keys there; the
// production identity provider replaces that section with its authority and
// audience settings. Without configuration the API fails closed (401).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Raw JWT claim names on purpose: the actor identity chain reads
        // 'oid' and 'sub' exactly as issued; the legacy claim-type mapping
        // would rename them and detach the audited actor from the token.
        options.MapInboundClaims = false;

        // With the mapping off, role checks must name the raw claim too.
        options.TokenValidationParameters.RoleClaimType = "role";
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddRateLimiter(options =>
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
builder.Services.AddModules(builder.Configuration, SolutionAssemblies.All);

builder.Services.AddScoped<RequestLoggingFilter>();
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// The committed development signing key only ever signs tokens in
// Development: anywhere else the host refuses to boot, because anyone with
// repository access could forge accepted tokens. Checked after Build() on
// purpose: only the built app sees the final configuration and environment,
// including test-host and deployment overlays.
const string devSigningIssuer = "notification-hub-dev-only";
var devSigningKeyConfigured = app.Configuration
    .GetSection("Authentication:Schemes:Bearer:SigningKeys")
    .GetChildren()
    .Any(key => string.Equals(key["Issuer"], devSigningIssuer, StringComparison.Ordinal));
if (devSigningKeyConfigured && !app.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        $"A chave de assinatura de desenvolvimento (issuer '{devSigningIssuer}') está configurada, "
        + $"mas o ambiente é '{app.Environment.EnvironmentName}'. "
        + "Configure as chaves do provedor de identidade real ou execute o host em Development.");
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// After authentication on purpose: the rate-limit partition is keyed by the
// authenticated principal, which does not exist earlier in the pipeline.
app.UseRateLimiter();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapOpenApi();

app.MapModuleEndpoints(SolutionAssemblies.All);

await app.RunAsync();

public partial class Program;
