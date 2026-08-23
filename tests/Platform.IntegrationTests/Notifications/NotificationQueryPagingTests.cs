using System.Globalization;
using System.Text.Json;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class NotificationQueryPagingTests(NotificationsApiFixture fixture)
{
    private const string InstantFormat = "yyyy-MM-ddTHH:mm:ss.ffffffZ";

    [RequiresDockerFact]
    public async Task The_recipient_history_pages_descending_and_the_cursor_resumes_exactly_where_it_stopped()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        List<string> accepted = [];
        foreach (var _ in Enumerable.Range(0, 3))
        {
            NotificationQueryApi.Accepted one = await NotificationQueryApi.AcceptAsync(
                fixture, templateKey, "transactional", recipientId);
            accepted.Add(one.PublicId);
        }

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        (var firstStatus, JsonElement first, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{recipientId}/notifications?limit=2");
        firstStatus.ShouldBe(200);

        var firstIds = NotificationQueryApi.ItemIds(first);
        firstIds.Length.ShouldBe(2);
        var cursor = NotificationQueryApi.NextCursor(first);
        cursor.ShouldNotBeNullOrWhiteSpace();

        (var secondStatus, JsonElement second, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{recipientId}/notifications?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        secondStatus.ShouldBe(200);

        var secondIds = NotificationQueryApi.ItemIds(second);
        secondIds.Length.ShouldBe(1);
        NotificationQueryApi.NextCursor(second).ShouldBeNull();

        // Descending by creation, no row seen twice and no row lost between
        // the pages: the union is the whole history, in reverse order.
        string[] paged = [.. firstIds, .. secondIds];
        paged.ShouldBe([.. Enumerable.Reverse(accepted)]);
        firstIds.Intersect(secondIds, StringComparer.Ordinal).ShouldBeEmpty();
    }

    [RequiresDockerFact]
    public async Task The_history_echoes_the_window_it_actually_applied()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        await NotificationQueryApi.AcceptAsync(fixture, templateKey, "transactional", recipientId);

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        (_, JsonElement page, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{recipientId}/notifications");

        JsonElement window = page.GetProperty("window");
        DateTimeOffset from = window.GetProperty("from").GetDateTimeOffset();
        DateTimeOffset to = window.GetProperty("to").GetDateTimeOffset();
        (to - from).TotalDays.ShouldBe(90, tolerance: 0.001);
        to.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
        page.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_window_that_excludes_the_history_answers_empty_instead_of_ignoring_the_bounds()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        await NotificationQueryApi.AcceptAsync(fixture, templateKey, "transactional", recipientId);

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        var from = Format(DateTimeOffset.UtcNow.AddDays(-40));
        var to = Format(DateTimeOffset.UtcNow.AddDays(-30));
        (var status, JsonElement page, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{recipientId}/notifications?from={from}&to={to}");

        status.ShouldBe(200);
        page.GetProperty("items").GetArrayLength().ShouldBe(0);
        page.GetProperty("window").GetProperty("to").GetDateTimeOffset()
            .ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(-29));
    }

    [RequiresDockerFact]
    public async Task A_cursor_outside_the_window_asked_for_is_refused_as_an_invalid_cursor()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            await NotificationQueryApi.AcceptAsync(fixture, templateKey, "transactional", recipientId);
        }

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        (_, JsonElement first, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{recipientId}/notifications?limit=1");
        var cursor = NotificationQueryApi.NextCursor(first);
        cursor.ShouldNotBeNullOrWhiteSpace();

        // The same cursor against a window that ended before the position it
        // carries: the page it would produce is not the page the caller asked
        // for, so the read refuses instead of quietly widening the window.
        var from = Format(DateTimeOffset.UtcNow.AddDays(-40));
        var to = Format(DateTimeOffset.UtcNow.AddDays(-30));
        (var status, JsonElement problem, var raw) = await NotificationQueryApi.ReadAsync(
            reader,
            $"/v1/recipients/{recipientId}/notifications?from={from}&to={to}&cursor={Uri.EscapeDataString(cursor!)}");

        status.ShouldBe(400);
        problem.GetProperty("type").GetString().ShouldBe("invalid-cursor");
        raw.ShouldNotContain(cursor!);
    }

    [RequiresDockerFact]
    public async Task A_cursor_that_is_not_a_cursor_is_refused_as_an_invalid_cursor()
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");

        (var status, JsonElement problem, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{ContactConsentApi.NewRecipientId()}/notifications?cursor=not-a-cursor");

        status.ShouldBe(400);
        problem.GetProperty("type").GetString().ShouldBe("invalid-cursor");
    }

    [RequiresDockerTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task A_limit_outside_the_published_range_is_a_bad_request(int limit)
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");

        (var status, JsonElement problem, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{ContactConsentApi.NewRecipientId()}/notifications?limit={limit}");

        status.ShouldBe(400);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    [RequiresDockerFact]
    public async Task The_largest_published_page_is_accepted()
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");

        (var status, _, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{ContactConsentApi.NewRecipientId()}/notifications?limit=200");

        status.ShouldBe(200);
    }

    [RequiresDockerFact]
    public async Task A_window_wider_than_the_ceiling_is_a_bad_request()
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");
        var from = Format(DateTimeOffset.UtcNow.AddDays(-181));

        (var status, JsonElement problem, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{ContactConsentApi.NewRecipientId()}/notifications?from={from}");

        status.ShouldBe(400);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    [RequiresDockerFact]
    public async Task An_unknown_class_filter_is_a_bad_request()
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");

        (var status, JsonElement problem, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{ContactConsentApi.NewRecipientId()}/notifications?class=marketing");

        status.ShouldBe(400);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    [RequiresDockerFact]
    public async Task The_correlation_route_answers_every_notification_of_one_transaction_and_nothing_else()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        var correlationId = $"txn-{Guid.NewGuid():N}";
        var otherRecipient = ContactConsentApi.NewRecipientId();

        NotificationQueryApi.Accepted first = await NotificationQueryApi.AcceptAsync(
            fixture, templateKey, "transactional", recipientId, correlationId);
        NotificationQueryApi.Accepted second = await NotificationQueryApi.AcceptAsync(
            fixture, templateKey, "transactional", otherRecipient, correlationId);
        NotificationQueryApi.Accepted unrelated = await NotificationQueryApi.AcceptAsync(
            fixture, templateKey, "transactional", recipientId, $"txn-{Guid.NewGuid():N}");

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        (var status, JsonElement page, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications?correlationId={correlationId}");

        status.ShouldBe(200);
        var ids = NotificationQueryApi.ItemIds(page);
        ids.ShouldBe([second.PublicId, first.PublicId]);
        ids.ShouldNotContain(unrelated.PublicId);
        page.GetProperty("window").GetProperty("from").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [RequiresDockerFact]
    public async Task The_correlation_route_obeys_the_same_window_rule_as_the_recipient_history()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        var correlationId = $"txn-{Guid.NewGuid():N}";
        await NotificationQueryApi.AcceptAsync(
            fixture, templateKey, "transactional", recipientId, correlationId);

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        var from = Format(DateTimeOffset.UtcNow.AddDays(-40));
        var to = Format(DateTimeOffset.UtcNow.AddDays(-30));

        (var status, JsonElement page, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications?correlationId={correlationId}&from={from}&to={to}");

        status.ShouldBe(200);
        page.GetProperty("items").GetArrayLength().ShouldBe(0);

        (var wideStatus, JsonElement wide, _) = await NotificationQueryApi.ReadAsync(
            reader,
            $"/v1/notifications?correlationId={correlationId}&from={Format(DateTimeOffset.UtcNow.AddDays(-181))}");
        wideStatus.ShouldBe(400);
        wide.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    private static string Format(DateTimeOffset instant)
        => Uri.EscapeDataString(instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture));

    private async Task<(string TemplateKey, string RecipientId)> SeedAsync()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        return (templateKey, ContactConsentApi.NewRecipientId());
    }
}
