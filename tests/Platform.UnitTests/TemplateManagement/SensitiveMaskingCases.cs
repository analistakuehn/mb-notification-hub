using System.Globalization;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// One masking scenario, written once and replayed against both maskers. The
/// planted values are what the assertions look for: a value listed as leaked
/// must not appear anywhere in the serialized form, which is the only oracle
/// that catches a mask that reports "nothing changed" after having masked, and
/// the only one that catches a leak reusing an existing nullable member.
/// </summary>
public sealed record SensitiveMaskingCase
{
    public required string Scenario { get; init; }

    public required string Payload { get; init; }

    public required IReadOnlyList<string> SensitiveNames { get; init; }

    /// <summary>Planted values no serialized form may carry after the mask.</summary>
    public IReadOnlyList<string> LeakedValues { get; init; } = [];

    /// <summary>Values of variables nobody declared sensitive; they must survive.</summary>
    public IReadOnlyList<string> SurvivingValues { get; init; } = [];

    /// <summary>Slash-separated locations that must hold the fixed mask.</summary>
    public IReadOnlyList<string> MaskedLocations { get; init; } = [];

    /// <summary>Slash-separated locations that must still hold JSON null.</summary>
    public IReadOnlyList<string> NullLocations { get; init; } = [];

    /// <summary>The sensitive name whose path breaks, when the case is a refusal.</summary>
    public string? RefusedName { get; init; }

    public override string ToString() => Scenario;
}

/// <summary>
/// The shared table both maskers replay. Symmetry between the two is asserted
/// here rather than remembered: a case added to the table runs on both sides.
/// Equality between the two maskers is not an oracle on its own, because they
/// can agree by being wrong in the same way; the nested cases in this table are
/// what makes the comparison mean something.
/// </summary>
public static class SensitiveMaskingCases
{
    public const string NestedProperty = "a sensitive value one level below the root";
    public const string ArrayElement = "a sensitive value inside an array element";
    public const string DuplicatedKey = "a payload carrying the same key twice";
    public const string NestedSibling = "a sibling of a nested sensitive value";
    public const string ExactPathNarrowing = "a sensitive name colliding under another container";
    public const string PathToNull = "a sensitive path resolving to null";
    public const string LiteralPathKey = "a top level key spelling the sensitive path";
    public const string NothingToMask = "a payload carrying no sensitive value";
    public const string BrokenPrefix = "a sensitive path breaking on a non object prefix";

    private static readonly Dictionary<string, SensitiveMaskingCase> Table =
        new(StringComparer.Ordinal)
        {
            [NestedProperty] = new SensitiveMaskingCase
            {
                Scenario = NestedProperty,
                Payload = """{"cliente":{"cpf":"9990001"},"orderId":"ord-1"}""",
                SensitiveNames = ["cpf"],
                LeakedValues = ["9990001"],
                SurvivingValues = ["ord-1"],
                MaskedLocations = ["cliente/cpf"],
            },
            [ArrayElement] = new SensitiveMaskingCase
            {
                Scenario = ArrayElement,
                Payload = """{"clientes":[{"cpf":"9990001"},{"cpf":"9990002"}],"orderId":"ord-1"}""",
                SensitiveNames = ["cpf"],
                LeakedValues = ["9990001", "9990002"],
                SurvivingValues = ["ord-1"],
                MaskedLocations = ["clientes/0/cpf", "clientes/1/cpf"],
            },
            [DuplicatedKey] = new SensitiveMaskingCase
            {
                // The duplicate sits on a key nobody declared sensitive, which
                // is enough: the mask materializes the whole object before it
                // reaches the sensitive one.
                Scenario = DuplicatedKey,
                Payload = """{"cpf":"9990001","orderId":"ord-1","orderId":"ord-2"}""",
                SensitiveNames = ["cpf"],
                LeakedValues = ["9990001"],
                MaskedLocations = ["cpf"],
            },
            [NestedSibling] = new SensitiveMaskingCase
            {
                Scenario = NestedSibling,
                Payload = """{"cliente":{"cpf":"9990001","nome":"Ana","email":"ana@exemplo.com"}}""",
                SensitiveNames = ["cpf"],
                LeakedValues = ["9990001"],
                SurvivingValues = ["Ana", "ana@exemplo.com"],
                MaskedLocations = ["cliente/cpf"],
            },
            [ExactPathNarrowing] = new SensitiveMaskingCase
            {
                Scenario = ExactPathNarrowing,
                Payload = """{"cliente":{"cpf":"9990001"},"empresa":{"cpf":"8880002"}}""",
                SensitiveNames = ["cliente.cpf"],
                LeakedValues = ["9990001"],
                SurvivingValues = ["8880002"],
                MaskedLocations = ["cliente/cpf"],
            },
            [PathToNull] = new SensitiveMaskingCase
            {
                Scenario = PathToNull,
                Payload = """{"cliente":{"cpf":null,"nome":"Ana"}}""",
                SensitiveNames = ["cliente.cpf"],
                SurvivingValues = ["Ana"],
                NullLocations = ["cliente/cpf"],
            },
            [LiteralPathKey] = new SensitiveMaskingCase
            {
                Scenario = LiteralPathKey,
                Payload = """{"cliente.cpf":"5550003","cliente":{"cpf":"9990001"}}""",
                SensitiveNames = ["cliente.cpf"],
                LeakedValues = ["9990001"],
                SurvivingValues = ["5550003"],
                MaskedLocations = ["cliente/cpf"],
            },
            [NothingToMask] = new SensitiveMaskingCase
            {
                Scenario = NothingToMask,
                Payload = """{"cliente":{"nome":"Ana"},"orderId":"ord-1"}""",
                SensitiveNames = ["cpf", "cliente.cpf"],
                SurvivingValues = ["Ana", "ord-1"],
            },
            [BrokenPrefix] = new SensitiveMaskingCase
            {
                Scenario = BrokenPrefix,
                Payload = """{"cliente":{"endereco":[{"cpf":"9990001"}]},"orderId":"ord-1"}""",
                SensitiveNames = ["cliente.endereco.cpf"],
                LeakedValues = ["9990001"],
                SurvivingValues = ["ord-1"],
                RefusedName = "cliente.endereco.cpf",
            },
        };

    /// <summary>
    /// Every case both maskers replay through the shared assertions. Derived
    /// from the table itself, so a case added there runs on both sides without
    /// anyone remembering to list it twice.
    /// </summary>
    public static IEnumerable<object[]> All
        => Table.Keys.Select(scenario => new object[] { scenario });

    public static SensitiveMaskingCase For(string scenario) => Table[scenario];

    /// <summary>
    /// The shared oracle. The first assertion names the harm, so a mutation
    /// that reopens the leak fails on the leak and not on a shape guard that
    /// happens to run before it.
    /// </summary>
    public static void AssertMaskedForm(string serialized, JsonElement masked, SensitiveMaskingCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(serialized);

        foreach (var leaked in testCase.LeakedValues)
        {
            serialized.Contains(leaked, StringComparison.Ordinal)
                .ShouldBeFalse($"o valor sensível plantado '{leaked}' saiu em claro na forma mascarada.");
        }

        foreach (var location in testCase.MaskedLocations)
        {
            Resolve(masked, location).GetString().ShouldBe(VariableMasking.MaskedValue);
        }

        foreach (var location in testCase.NullLocations)
        {
            Resolve(masked, location).ValueKind.ShouldBe(JsonValueKind.Null);
        }

        foreach (var surviving in testCase.SurvivingValues)
        {
            serialized.Contains(surviving, StringComparison.Ordinal)
                .ShouldBeTrue($"o valor não sensível '{surviving}' desapareceu da forma mascarada.");
        }
    }

    private static JsonElement Resolve(JsonElement root, string location)
    {
        JsonElement current = root;
        foreach (var segment in location.Split('/'))
        {
            current = current.ValueKind == JsonValueKind.Array
                ? current[int.Parse(segment, CultureInfo.InvariantCulture)]
                : current.GetProperty(segment);
        }

        return current;
    }
}
