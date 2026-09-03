using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.EndpointFilters;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.OpenApi;
using NotificationHub.Api.Infrastructure.RateLimiting;

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
builder.Services.AddPlatformMessaging(builder.Configuration);
builder.Services.AddEnvelopeEncryption(builder.Configuration);
builder.Services.AddModules(builder.Configuration, SolutionAssemblies.All);

builder.Services.AddScoped<RequestLoggingFilter>();
builder.Services.AddOpenApi(options => options.UseModuleQualifiedSchemaNames());
builder.Services.AddOpenApiRateLimiting();

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

// Same containment rule for the committed development envelope master key:
// it only ever derives data keys in Development, because anyone with
// repository access could decrypt stored variables protected by it.
var envelopeKeyId = app.Configuration[$"{EnvelopeCipherOptions.SectionName}:KeyId"];
if (envelopeKeyId is not null
    && envelopeKeyId.Contains(EnvelopeCipherOptions.DevelopmentKeyIdMarker, StringComparison.OrdinalIgnoreCase)
    && !app.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        $"A chave-mestra de cifra de desenvolvimento (key id '{envelopeKeyId}') está configurada, "
        + $"mas o ambiente é '{app.Environment.EnvironmentName}'. "
        + "Configure a chave do provedor de KMS real ou execute o host em Development.");
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// After authentication on purpose: the rate-limit partition is keyed by the
// authenticated principal, which does not exist earlier in the pipeline.
app.UseRateLimiter();
app.MapHealthChecks("/health").AllowAnonymous();

// Served in every environment on purpose: this document is the machine
// contract that producers and administrative clients generate their clients
// from, and no build step publishes it anywhere else. Both requirements are
// stated even though the fallback policy already covers authorization and
// nothing else competes for the budget: MapOpenApi carries no authorization
// metadata of its own, so without the explicit call the route's protection
// would rest entirely on the fallback policy registered at the top of this
// file, and would turn public the day that policy is relaxed or the package
// starts shipping AllowAnonymous. Every other route in the API declares both.
app.MapOpenApi()
    .RequireAuthorization()
    .RequireRateLimiting(OpenApiRateLimitingSetup.PolicyName);

app.MapModuleEndpoints(SolutionAssemblies.All);

await app.RunAsync();

public partial class Program;
