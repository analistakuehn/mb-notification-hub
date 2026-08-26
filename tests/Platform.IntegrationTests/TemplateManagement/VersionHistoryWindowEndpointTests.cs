using System.Net;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// The template and layout details expose the version history one bounded
/// window at a time. The history of a long-lived identity grows without limit,
/// so these tests seed more versions than a single response may carry and pin
/// the window size, the ordering, the truncation signal, and the continuation.
/// <para>
/// Versions are seeded straight through the DbContext because the governed
/// authoring flow (draft, four eyes, publication, audit) cannot open two
/// hundred versions in a test: what is under test here is how the history is
/// read, not how it is written.
/// </para>
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class VersionHistoryWindowEndpointTests(TemplateManagementApiFixture fixture)
{
    /// <summary>Version summaries a single detail response may carry.</summary>
    private const int WindowSize = 200;

    /// <summary>Seeded history size, deliberately larger than one window.</summary>
    private const int HistorySize = 205;

    /// <summary>Seeded history that fits in one window with room to spare.</summary>
    private const int ShortHistorySize = 3;

    /// <summary>The published version sits next to the newest, as it does in a real history.</summary>
    private const int PublishedVersion = HistorySize - 1;

    /// <summary>
    /// A well formed cursor whose payload is the word "newest" instead of a
    /// version number: base64url of text the endpoint cannot page from.
    /// </summary>
    private const string CursorOverText = "bmV3ZXN0";

    [RequiresDockerFact]
    public async Task A_template_history_longer_than_the_window_lists_exactly_two_hundred_versions()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        await SeedTemplateHistoryAsync(key, HistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/templates/{key}");

        body.GetProperty("versions").GetArrayLength().ShouldBe(200);
    }

    [RequiresDockerFact]
    public async Task A_template_history_longer_than_the_window_still_lists_the_published_version()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-2");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        await SeedTemplateHistoryAsync(key, HistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/templates/{key}");

        // The window has to hold the newest versions: numbering is monotonic and
        // a rollback clones into a higher number, so cutting the history from
        // the oldest end would answer with superseded versions only.
        body.GetProperty("versions").EnumerateArray().ShouldContain(entry =>
            entry.GetProperty("version").GetInt32() == PublishedVersion
            && entry.GetProperty("status").GetString() == "published");
    }

    [RequiresDockerFact]
    public async Task The_template_version_window_reads_from_the_oldest_version_to_the_newest()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-3");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        await SeedTemplateHistoryAsync(key, HistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/templates/{key}");

        VersionNumbers(body).ShouldBe(Enumerable.Range(HistorySize - WindowSize + 1, WindowSize));
    }

    [RequiresDockerFact]
    public async Task A_template_history_longer_than_the_window_reports_the_truncation()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-4");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        await SeedTemplateHistoryAsync(key, HistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/templates/{key}");

        body.GetProperty("versionsTruncated").GetBoolean().ShouldBeTrue();
        body.GetProperty("versionsNextCursor").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [RequiresDockerFact]
    public async Task The_template_versions_cursor_returns_the_older_versions_without_repeating_or_dropping_any()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-5");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        await SeedTemplateHistoryAsync(key, HistorySize);

        JsonElement first = await ReadAsync(client, $"/v1/templates/{key}");
        var cursor = first.GetProperty("versionsNextCursor").GetString()!;
        JsonElement second = await ReadAsync(
            client,
            $"/v1/templates/{key}?versionsCursor={Uri.EscapeDataString(cursor)}");

        var older = VersionNumbers(second);
        older.ShouldBe(Enumerable.Range(1, HistorySize - WindowSize));
        second.GetProperty("versionsTruncated").GetBoolean().ShouldBeFalse();
        second.GetProperty("versionsNextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        older.Concat(VersionNumbers(first)).ShouldBe(Enumerable.Range(1, HistorySize));
    }

    [RequiresDockerFact]
    public async Task A_template_history_inside_the_window_lists_every_version_and_offers_no_cursor()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-6");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        await SeedTemplateHistoryAsync(key, ShortHistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/templates/{key}");

        VersionNumbers(body).ShouldBe(Enumerable.Range(1, ShortHistorySize));
        body.GetProperty("versionsTruncated").GetBoolean().ShouldBeFalse();
        body.GetProperty("versionsNextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [RequiresDockerFact]
    public async Task A_layout_history_longer_than_the_window_lists_exactly_two_hundred_versions()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-7");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey());
        await SeedLayoutHistoryAsync(key, HistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/layouts/{key}");

        body.GetProperty("versions").GetArrayLength().ShouldBe(200);
    }

    [RequiresDockerFact]
    public async Task A_layout_history_longer_than_the_window_still_lists_the_published_version()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-8");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey());
        await SeedLayoutHistoryAsync(key, HistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/layouts/{key}");

        body.GetProperty("versions").EnumerateArray().ShouldContain(entry =>
            entry.GetProperty("version").GetInt32() == PublishedVersion
            && entry.GetProperty("status").GetString() == "published");
    }

    [RequiresDockerFact]
    public async Task The_layout_version_window_reads_from_the_oldest_version_to_the_newest()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-9");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey());
        await SeedLayoutHistoryAsync(key, HistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/layouts/{key}");

        VersionNumbers(body).ShouldBe(Enumerable.Range(HistorySize - WindowSize + 1, WindowSize));
    }

    [RequiresDockerFact]
    public async Task A_layout_history_longer_than_the_window_reports_the_truncation()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-10");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey());
        await SeedLayoutHistoryAsync(key, HistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/layouts/{key}");

        body.GetProperty("versionsTruncated").GetBoolean().ShouldBeTrue();
        body.GetProperty("versionsNextCursor").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [RequiresDockerFact]
    public async Task The_layout_versions_cursor_returns_the_older_versions_without_repeating_or_dropping_any()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-11");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey());
        await SeedLayoutHistoryAsync(key, HistorySize);

        JsonElement first = await ReadAsync(client, $"/v1/layouts/{key}");
        var cursor = first.GetProperty("versionsNextCursor").GetString()!;
        JsonElement second = await ReadAsync(
            client,
            $"/v1/layouts/{key}?versionsCursor={Uri.EscapeDataString(cursor)}");

        var older = VersionNumbers(second);
        older.ShouldBe(Enumerable.Range(1, HistorySize - WindowSize));
        second.GetProperty("versionsTruncated").GetBoolean().ShouldBeFalse();
        second.GetProperty("versionsNextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        older.Concat(VersionNumbers(first)).ShouldBe(Enumerable.Range(1, HistorySize));
    }

    [RequiresDockerFact]
    public async Task A_layout_history_inside_the_window_lists_every_version_and_offers_no_cursor()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-12");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey());
        await SeedLayoutHistoryAsync(key, ShortHistorySize);

        JsonElement body = await ReadAsync(client, $"/v1/layouts/{key}");

        VersionNumbers(body).ShouldBe(Enumerable.Range(1, ShortHistorySize));
        body.GetProperty("versionsTruncated").GetBoolean().ShouldBeFalse();
        body.GetProperty("versionsNextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [RequiresDockerFact]
    public async Task A_versions_cursor_that_carries_no_version_number_is_rejected_with_400()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-13");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage malformed = await client.GetAsync(
            $"/v1/templates/{key}?versionsCursor=%2Fnot-valid%2F");
        HttpResponseMessage notANumber = await client.GetAsync(
            $"/v1/templates/{key}?versionsCursor={CursorOverText}");

        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        notANumber.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(notANumber);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    [RequiresDockerFact]
    public async Task A_layout_versions_cursor_that_carries_no_version_number_is_rejected_with_400()
    {
        HttpClient client = fixture.CreateAuthorClient("author-window-14");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey());

        HttpResponseMessage malformed = await client.GetAsync(
            $"/v1/layouts/{key}?versionsCursor=%2Fnot-valid%2F");
        HttpResponseMessage notANumber = await client.GetAsync(
            $"/v1/layouts/{key}?versionsCursor={CursorOverText}");

        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        notANumber.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(notANumber);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await TemplateApi.ReadJsonAsync(response);
    }

    private static int[] VersionNumbers(JsonElement body)
        => [.. body.GetProperty("versions")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("version").GetInt32())];

    /// <summary>
    /// Statuses of a history that reached the given size: everything is
    /// superseded except the newest pair, one published version and the draft
    /// opened on top of it.
    /// </summary>
    private static string SeededStatus(int version, int total) => version switch
    {
        _ when version == total => "draft",
        _ when version == total - 1 => "published",
        _ => "superseded",
    };

    private async Task SeedTemplateHistoryAsync(string key, int total)
        => await fixture.ExecuteDbAsync(async dbContext =>
        {
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;
            for (var version = 1; version <= total; version++)
            {
                dbContext.TemplateVersions.Add(TemplateVersion.Rehydrate(new TemplateVersionState
                {
                    TemplateKey = key,
                    Version = version,
                    Status = SeededStatus(version, total),
                    CreatedBy = "seed-author",
                    CreatedAt = createdAt,
                }));
            }

            await dbContext.SaveChangesAsync();
        });

    private async Task SeedLayoutHistoryAsync(string key, int total)
        => await fixture.ExecuteDbAsync(async dbContext =>
        {
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;
            for (var version = 1; version <= total; version++)
            {
                dbContext.LayoutVersions.Add(LayoutVersion.Rehydrate(new LayoutVersionState
                {
                    LayoutKey = key,
                    Version = version,
                    Status = SeededStatus(version, total),
                    CreatedBy = "seed-author",
                    CreatedAt = createdAt,
                }));
            }

            await dbContext.SaveChangesAsync();
        });
}
