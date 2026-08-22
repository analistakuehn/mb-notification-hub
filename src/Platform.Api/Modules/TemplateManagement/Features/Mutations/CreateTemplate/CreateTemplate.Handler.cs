using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplate
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<TemplateKey> key = TemplateKey.Create(command.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<TemplateKey, Response>();
            }

            Result<NotificationClass> notificationClass = NotificationClasses.Create(command.Class);
            if (notificationClass.IsFailure)
            {
                return notificationClass.AsFailure<NotificationClass, Response>();
            }

            Locale? defaultLocale = null;
            if (command.DefaultLocale is not null)
            {
                Result<Locale> parsed = Locale.Create(command.DefaultLocale);
                if (parsed.IsFailure)
                {
                    return parsed.AsFailure<Locale, Response>();
                }

                defaultLocale = parsed.Value;
            }

            Result<Template> template = Template.Create(key.Value!, new TemplateMetadata
            {
                Application = command.Application,
                Class = notificationClass.Value,
                OwnerTeam = command.OwnerTeam,
                Purpose = command.Purpose,
                LegalBasis = command.LegalBasis,
                DefaultLocale = defaultLocale,
                LinkDomainsAllowed = command.LinkDomainsAllowed ?? [],
                SensitiveVariables = command.SensitiveVariables ?? [],
            });
            if (template.IsFailure)
            {
                return template.AsFailure<Template, Response>();
            }

            bool exists = await dbContext.Templates
                .AsNoTracking()
                .WhereKey(key.Value!)
                .AnyAsync(cancellationToken);
            if (exists)
            {
                return Conflict(key.Value!);
            }

            dbContext.Templates.Add(template.Value!);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Conflict(key.Value!);
            }

            var response = Response.From(template.Value!);
            logger.TemplateCreated(response.Key, response.Application, response.Class);
            return Result.Success(response);
        }

        private static Result<Response> Conflict(TemplateKey key)
            => Result.BusinessRuleViolation<Response>(DomainError.Format(
                ErrorCodes.TemplateAlreadyExists,
                $"A template with key '{key.Value}' already exists."));
    }
}
