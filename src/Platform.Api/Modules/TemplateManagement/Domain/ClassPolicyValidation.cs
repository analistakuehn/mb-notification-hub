using System.Globalization;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Canonical names of the class policy structural checks.</summary>
public static class ClassPolicyCheckNames
{
    public const string DefinitionDocument = "definition-document";
    public const string SchemaVersion = "schema-version";
    public const string ChannelsAllowed = "channels-allowed";
    public const string DeliveryPlan = "delivery-plan";
    public const string DefaultTtl = "default-ttl";
    public const string DedupeWindow = "dedupe-window";
    public const string QuietHours = "quiet-hours";
    public const string ConsentPurpose = "consent-purpose";
}

/// <summary>
/// Structural validation of a class policy definition against the version 1
/// vocabulary. Every check runs and lands in the report; a failed check never
/// interrupts the run, because the full <c>checks[]</c> list is the value the
/// caller is after and any failure blocks the draft and the publication.
/// Fields outside the vocabulary are tolerated on purpose: they belong to a
/// newer writer, never to an error.
/// </summary>
public static class ClassPolicyValidation
{
    public const int SupportedSchemaVersion = 1;
    public const int MaxConsentPurposeLength = 200;

    /// <summary>Durations travel as integer seconds with an 's' suffix, such as '30s'.</summary>
    public const string DurationFormat = "<seconds>s";

    public static readonly TimeSpan MinDefaultTtl = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDefaultTtl = TimeSpan.FromDays(30);
    public static readonly TimeSpan MinDedupeWindow = TimeSpan.Zero;
    public static readonly TimeSpan MaxDedupeWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinStepTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxStepTimeout = TimeSpan.FromHours(24);

    public static ValidationReport Validate(string? definitionJson) => Parse(definitionJson).Report;

    /// <summary>
    /// Runs the full check catalog and, when every check passes, materializes
    /// the typed definition in the same pass so reader and validator can never
    /// disagree about what a valid document is.
    /// </summary>
    internal static (ValidationReport Report, ClassPolicyDefinition? Definition) Parse(string? definitionJson)
    {
        List<ValidationCheck> checks = [];
        using JsonDocument? document = TryParseDocument(definitionJson, checks);
        if (document is null)
        {
            return (new ValidationReport(checks), null);
        }

        JsonElement root = document.RootElement;
        checks.Add(Passed(ClassPolicyCheckNames.DefinitionDocument, "The definition is a well-formed JSON object."));

        var schemaVersion = CheckSchemaVersion(root, checks);
        List<Channel>? channels = CheckChannelsAllowed(root, checks);
        List<DeliveryPlanStep>? plan = CheckDeliveryPlan(root, channels, checks);
        TimeSpan? defaultTtl = CheckDuration(
            root, "defaultTtl", ClassPolicyCheckNames.DefaultTtl, MinDefaultTtl, MaxDefaultTtl, checks);
        TimeSpan? dedupeWindow = CheckDuration(
            root, "dedupeWindow", ClassPolicyCheckNames.DedupeWindow, MinDedupeWindow, MaxDedupeWindow, checks);
        (var quietHoursValid, QuietHoursWindow? quietHours) = CheckQuietHours(root, checks);
        (var consentPurposeValid, var consentPurpose) = CheckConsentPurpose(root, checks);

        var report = new ValidationReport(checks);
        if (schemaVersion is null
            || channels is null
            || plan is null
            || defaultTtl is null
            || dedupeWindow is null
            || !quietHoursValid
            || !consentPurposeValid
            || !report.Passed)
        {
            return (report, null);
        }

        var definition = new ClassPolicyDefinition
        {
            SchemaVersion = schemaVersion.Value,
            ChannelsAllowed = channels,
            DeliveryPlan = plan,
            DefaultTtl = defaultTtl.Value,
            DedupeWindow = dedupeWindow.Value,
            QuietHours = quietHours,
            ConsentPurpose = consentPurpose,
        };
        return (report, definition);
    }

    private static JsonDocument? TryParseDocument(string? definitionJson, List<ValidationCheck> checks)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DefinitionDocument,
                "The definition document is required.",
                null));
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(definitionJson);
        }
        catch (JsonException)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DefinitionDocument,
                "The definition must be well-formed JSON.",
                null));
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            checks.Add(Failed(
                ClassPolicyCheckNames.DefinitionDocument,
                "The definition must be a JSON object.",
                null));
            return null;
        }

        return document;
    }

    private static int? CheckSchemaVersion(JsonElement root, List<ValidationCheck> checks)
    {
        if (!TryGetPresent(root, "schemaVersion", out JsonElement element))
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.SchemaVersion,
                "The required field 'schemaVersion' is missing.",
                "schemaVersion"));
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.SchemaVersion,
                "Field 'schemaVersion' must be an integer.",
                "schemaVersion"));
            return null;
        }

        if (value != SupportedSchemaVersion)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.SchemaVersion,
                $"Unsupported schemaVersion {value}: this vocabulary supports version {SupportedSchemaVersion}.",
                "schemaVersion"));
            return null;
        }

        checks.Add(Passed(ClassPolicyCheckNames.SchemaVersion, $"schemaVersion {value} is supported."));
        return value;
    }

    private static List<Channel>? CheckChannelsAllowed(JsonElement root, List<ValidationCheck> checks)
    {
        if (!TryGetPresent(root, "channelsAllowed", out JsonElement element))
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.ChannelsAllowed,
                "The required field 'channelsAllowed' is missing.",
                "channelsAllowed"));
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.ChannelsAllowed,
                "Field 'channelsAllowed' must be a non-empty array of channel names.",
                "channelsAllowed"));
            return null;
        }

        var before = checks.Count;
        List<Channel> channels = [];
        var index = 0;
        foreach (JsonElement entry in element.EnumerateArray())
        {
            Channel? channel = ReadChannel(entry);
            if (channel is null)
            {
                checks.Add(Failed(
                    ClassPolicyCheckNames.ChannelsAllowed,
                    $"Unknown channel '{Describe(entry)}'. Supported channels: {SupportedChannels()}.",
                    $"channelsAllowed[{index}]"));
            }
            else if (channels.Contains(channel))
            {
                checks.Add(Failed(
                    ClassPolicyCheckNames.ChannelsAllowed,
                    $"Channel '{channel.Value}' appears more than once.",
                    $"channelsAllowed[{index}]"));
            }
            else
            {
                channels.Add(channel);
            }

            index++;
        }

        if (checks.Count > before)
        {
            return null;
        }

        checks.Add(Passed(
            ClassPolicyCheckNames.ChannelsAllowed,
            "Every allowed channel is known and appears once."));
        return channels;
    }

    private static List<DeliveryPlanStep>? CheckDeliveryPlan(
        JsonElement root,
        IReadOnlyList<Channel>? allowedChannels,
        List<ValidationCheck> checks)
    {
        if (!TryGetPresent(root, "deliveryPlan", out JsonElement element))
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DeliveryPlan,
                "The required field 'deliveryPlan' is missing.",
                "deliveryPlan"));
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DeliveryPlan,
                "Field 'deliveryPlan' must be a non-empty array of step objects.",
                "deliveryPlan"));
            return null;
        }

        var before = checks.Count;
        List<DeliveryPlanStep> steps = [];
        var index = 0;
        foreach (JsonElement entry in element.EnumerateArray())
        {
            DeliveryPlanStep? step = CheckDeliveryPlanStep(entry, index, allowedChannels, steps, checks);
            if (step is not null)
            {
                steps.Add(step);
            }

            index++;
        }

        if (checks.Count > before)
        {
            return null;
        }

        checks.Add(Passed(
            ClassPolicyCheckNames.DeliveryPlan,
            "Every delivery step names an allowed channel once, with valid timeouts."));
        return steps;
    }

    private static DeliveryPlanStep? CheckDeliveryPlanStep(
        JsonElement entry,
        int index,
        IReadOnlyList<Channel>? allowedChannels,
        IReadOnlyList<DeliveryPlanStep> earlierSteps,
        List<ValidationCheck> checks)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DeliveryPlan,
                "Each delivery step must be an object with a 'channel' field.",
                $"deliveryPlan[{index}]"));
            return null;
        }

        Channel? channel = TryGetPresent(entry, "channel", out JsonElement channelElement)
            ? ReadChannel(channelElement)
            : null;
        if (channel is null)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DeliveryPlan,
                $"Each delivery step requires a known 'channel'. Supported channels: {SupportedChannels()}.",
                $"deliveryPlan[{index}].channel"));
            return null;
        }

        if (allowedChannels is not null && !allowedChannels.Contains(channel))
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DeliveryPlan,
                $"Channel '{channel.Value}' is not in 'channelsAllowed'.",
                $"deliveryPlan[{index}].channel"));
            return null;
        }

        if (earlierSteps.Any(step => step.Channel == channel))
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DeliveryPlan,
                $"Channel '{channel.Value}' appears in more than one delivery step.",
                $"deliveryPlan[{index}].channel"));
            return null;
        }

        if (!TryGetPresent(entry, "timeout", out JsonElement timeoutElement))
        {
            return new DeliveryPlanStep(channel, Timeout: null);
        }

        if (timeoutElement.ValueKind != JsonValueKind.String
            || !TryParseDuration(timeoutElement.GetString()!, out TimeSpan timeout)
            || timeout < MinStepTimeout
            || timeout > MaxStepTimeout)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.DeliveryPlan,
                $"Step 'timeout' must be a duration ('{DurationFormat}') between "
                + $"{Seconds(MinStepTimeout)} and {Seconds(MaxStepTimeout)}.",
                $"deliveryPlan[{index}].timeout"));
            return null;
        }

        return new DeliveryPlanStep(channel, timeout);
    }

    private static TimeSpan? CheckDuration(
        JsonElement root,
        string field,
        string checkName,
        TimeSpan min,
        TimeSpan max,
        List<ValidationCheck> checks)
    {
        if (!TryGetPresent(root, field, out JsonElement element))
        {
            checks.Add(Failed(checkName, $"The required field '{field}' is missing.", field));
            return null;
        }

        if (element.ValueKind != JsonValueKind.String
            || !TryParseDuration(element.GetString()!, out TimeSpan duration)
            || duration < min
            || duration > max)
        {
            checks.Add(Failed(
                checkName,
                $"Field '{field}' must be a duration ('{DurationFormat}') between {Seconds(min)} and {Seconds(max)}.",
                field));
            return null;
        }

        checks.Add(Passed(checkName, $"Field '{field}' is a valid duration."));
        return duration;
    }

    private static (bool Valid, QuietHoursWindow? Window) CheckQuietHours(
        JsonElement root,
        List<ValidationCheck> checks)
    {
        if (!TryGetPresent(root, "quietHours", out JsonElement element))
        {
            checks.Add(Passed(ClassPolicyCheckNames.QuietHours, "No quiet-hours window applies."));
            return (true, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.QuietHours,
                "Field 'quietHours' must be null or an object with 'from' and 'to'.",
                "quietHours"));
            return (false, null);
        }

        TimeOnly? from = ReadTimeOfDay(element, "from");
        TimeOnly? to = ReadTimeOfDay(element, "to");
        if (from is null || to is null)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.QuietHours,
                "Fields 'quietHours.from' and 'quietHours.to' must be times in 24h 'HH:mm' format.",
                from is null ? "quietHours.from" : "quietHours.to"));
            return (false, null);
        }

        if (from.Value == to.Value)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.QuietHours,
                "The quiet-hours window must not start and end at the same time.",
                "quietHours"));
            return (false, null);
        }

        checks.Add(Passed(ClassPolicyCheckNames.QuietHours, "The quiet-hours window is a valid time range."));
        return (true, new QuietHoursWindow(from.Value, to.Value));
    }

    private static (bool Valid, string? Purpose) CheckConsentPurpose(JsonElement root, List<ValidationCheck> checks)
    {
        if (!TryGetPresent(root, "consentPurpose", out JsonElement element))
        {
            checks.Add(Passed(
                ClassPolicyCheckNames.ConsentPurpose,
                "No consent purpose applies: the class rides on a contractual or legal basis."));
            return (true, null);
        }

        var purpose = element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(purpose) || purpose.Length > MaxConsentPurposeLength)
        {
            checks.Add(Failed(
                ClassPolicyCheckNames.ConsentPurpose,
                $"Field 'consentPurpose' must be null or a non-empty string with at most "
                + $"{MaxConsentPurposeLength} characters.",
                "consentPurpose"));
            return (false, null);
        }

        checks.Add(Passed(ClassPolicyCheckNames.ConsentPurpose, "The consent purpose is a valid identifier."));
        return (true, purpose);
    }

    /// <summary>An explicit null is read exactly like an absent field.</summary>
    private static bool TryGetPresent(JsonElement parent, string name, out JsonElement element)
        => parent.TryGetProperty(name, out element) && element.ValueKind != JsonValueKind.Null;

    private static Channel? ReadChannel(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        Result<Channel> created = Channel.Create(element.GetString());
        return created.IsSuccess ? created.Value : null;
    }

    private static TimeOnly? ReadTimeOfDay(JsonElement parent, string name)
        => TryGetPresent(parent, name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && TimeOnly.TryParseExact(element.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly value)
            ? value
            : null;

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        var candidate = value.Trim();
        if (candidate.Length < 2
            || !candidate.EndsWith('s')
            || !int.TryParse(candidate[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static string Seconds(TimeSpan value)
        => $"'{((long)value.TotalSeconds).ToString(CultureInfo.InvariantCulture)}s'";

    private static string SupportedChannels()
        => string.Join(", ", Channel.All.Select(channel => channel.Value));

    private static string Describe(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ValueKind.ToString();

    private static ValidationCheck Passed(string name, string message)
        => new(name, ValidationCheckStatuses.Passed, message, null);

    private static ValidationCheck Failed(string name, string message, string? location)
        => new(name, ValidationCheckStatuses.Failed, message, location);
}
