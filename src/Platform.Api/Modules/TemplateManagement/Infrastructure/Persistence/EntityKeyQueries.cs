using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

/// <summary>
/// Query helpers over the string-backed key columns. The template key is exposed
/// to the domain as a value object but mapped as a plain string so keyset
/// pagination and ordering translate to SQL.
/// </summary>
internal static class EntityKeyQueries
{
    // The model maps the backing fields directly (EF requires the property name
    // to match the field name when no CLR property is associated), because the
    // CLR properties expose the key as a value object.
    internal const string TemplateKeyProperty = "_key";
    internal const string VersionTemplateKeyProperty = "_templateKey";
    internal const string LayoutKeyProperty = "_key";
    internal const string VersionLayoutKeyProperty = "_layoutKey";

    internal static IQueryable<Template> WhereKey(this IQueryable<Template> source, TemplateKey key)
        => source.Where(template => EF.Property<string>(template, TemplateKeyProperty) == key.Value);

    internal static IQueryable<Template> WhereKeyAfter(this IQueryable<Template> source, string lastKey)
#pragma warning disable CA1309, CA1310 // EF Core only translates the two-argument Compare; it runs as a server-side column comparison, never in .NET.
        => source.Where(template => string.Compare(EF.Property<string>(template, TemplateKeyProperty), lastKey) > 0);
#pragma warning restore CA1309, CA1310

    internal static IOrderedQueryable<Template> OrderByKey(this IQueryable<Template> source)
        => source.OrderBy(template => EF.Property<string>(template, TemplateKeyProperty));

    internal static IQueryable<TemplateVersion> WhereTemplateKey(this IQueryable<TemplateVersion> source, TemplateKey key)
        => source.Where(version => EF.Property<string>(version, VersionTemplateKeyProperty) == key.Value);

    internal static IQueryable<Layout> WhereKey(this IQueryable<Layout> source, LayoutKey key)
        => source.Where(layout => EF.Property<string>(layout, LayoutKeyProperty) == key.Value);

    internal static IQueryable<Layout> WhereKeyAfter(this IQueryable<Layout> source, string lastKey)
#pragma warning disable CA1309, CA1310 // EF Core only translates the two-argument Compare; it runs as a server-side column comparison, never in .NET.
        => source.Where(layout => string.Compare(EF.Property<string>(layout, LayoutKeyProperty), lastKey) > 0);
#pragma warning restore CA1309, CA1310

    internal static IOrderedQueryable<Layout> OrderByKey(this IQueryable<Layout> source)
        => source.OrderBy(layout => EF.Property<string>(layout, LayoutKeyProperty));

    internal static IQueryable<LayoutVersion> WhereLayoutKey(this IQueryable<LayoutVersion> source, LayoutKey key)
        => source.Where(version => EF.Property<string>(version, VersionLayoutKeyProperty) == key.Value);
}
