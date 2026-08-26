using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetTemplate
{
    // Upper bound on the version summaries a single detail response carries.
    // The history of a long-lived template grows without limit, so the detail
    // reads one bounded window and hands back a cursor for the rest. The bound
    // matches the largest page the catalog listings accept.
    private const int VersionWindowSize = 200;

    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(
            string key,
            string? versionsCursor,
            CancellationToken cancellationToken)
        {
            Result<TemplateKey> templateKey = TemplateKey.Create(key);
            if (templateKey.IsFailure)
            {
                return templateKey.AsFailure<TemplateKey, Response>();
            }

            Result<int?> cursor = DecodeVersionsCursor(versionsCursor);
            if (cursor.IsFailure)
            {
                return cursor.AsFailure<int?, Response>();
            }

            Template? template = await dbContext.Templates
                .AsNoTracking()
                .WhereKey(templateKey.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (template is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateNotFound,
                    $"Template '{templateKey.Value!.Value}' does not exist."));
            }

            IQueryable<TemplateVersion> versions = dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(templateKey.Value!);

            // Each page walks backwards from the newest version. The next one
            // continues below the oldest version already returned.
            if (cursor.Value is int olderThan)
            {
                versions = versions.Where(version => version.Version < olderThan);
            }

            // Projected in the database, on purpose. The summary reads five
            // scalar columns, while the entity carries its owned content
            // collection, which EF loads with the owner and whose body alone
            // reaches MaxBodyLength per (channel, locale) entry. Materializing
            // versions to list their numbers pulled the entire authored content
            // of every version the identity ever had.
            //
            // Descending on purpose, and not an ascending cut: numbering is
            // monotonic and a rollback clones into a higher number, so the draft
            // and the published version live at the tail of the history. Reading
            // the head would return superseded versions only.
            var rows = await versions
                .OrderByDescending(version => version.Version)
                .Select(version => new
                {
                    version.Version,
                    version.Status,
                    version.ContentHash,
                    version.CreatedBy,
                    version.CreatedAt,
                })
                .Take(VersionWindowSize + 1)
                .ToListAsync(cancellationToken);

            // The extra row only answers whether another page exists; it is
            // never emitted.
            var truncated = rows.Count > VersionWindowSize;

            // Canonical() is an extension over the enum and has no SQL
            // translation, so the canonical text is resolved after materializing.
            // The retained window is reversed here so the emitted array keeps
            // reading oldest first, the order this detail has always exposed.
            var summaries = rows
                .Take(VersionWindowSize)
                .Reverse()
                .Select(row => new VersionSummary(
                    row.Version,
                    row.Status.Canonical(),
                    row.ContentHash,
                    row.CreatedBy,
                    row.CreatedAt))
                .ToList();

            var nextCursor = truncated
                ? PageCursor.Encode(summaries[0].Version.ToString(CultureInfo.InvariantCulture))
                : null;

            return Result.Success(Response.From(template, summaries, truncated, nextCursor));
        }

        private static Result<int?> DecodeVersionsCursor(string? cursor)
        {
            if (string.IsNullOrWhiteSpace(cursor))
            {
                return Result.Success<int?>(null);
            }

            Result<string> decoded = PageCursor.Decode(cursor);
            if (decoded.IsFailure)
            {
                return decoded.AsFailure<string, int?>();
            }

            return int.TryParse(decoded.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                && version >= 1
                ? Result.Success<int?>(version)
                : Result.ValidationError<int?>(DomainError.Format(
                    ErrorCodes.InvalidRequest,
                    "The versions cursor is not valid. Use the versionsNextCursor value returned by the previous page."));
        }
    }
}
