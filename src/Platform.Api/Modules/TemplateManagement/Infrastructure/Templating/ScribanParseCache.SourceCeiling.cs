using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

// The assertion below belongs to the memoization and is written in a file of
// its own for one reason: the part that holds the budget imports the engine,
// whose Template is a different type from the domain Template, and the domain
// side is what this assertion has to read. Two namespaces that cannot meet in
// one file meet across two.
internal sealed partial class ScribanParseCache
{
    /// <summary>
    /// The tie between the source ceiling and this budget, asserted at compile
    /// time: the slack between the two is declared unsigned, so a ceiling the
    /// memoization cannot promise to hold does not build. Today the two are
    /// 208411 and 131072 characters and the slack is 77339.
    /// <para>
    /// What it protects is the failure mode this type declares unacceptable. A
    /// source that alone outweighs the budget is refused on arrival without a
    /// word and reparsed on every single call, which reads as a slow renderer
    /// and never as a misconfiguration. This declaration is the only thing
    /// left tying the two numbers together, and it answers at build time
    /// rather than at deploy time.
    /// </para>
    /// <para>
    /// The compiler names neither constant when it fires. Raising the source
    /// ceiling to 300000 reports <c>error CS0031: Constant value '-91589'
    /// cannot be converted to a 'uint'</c> on this line and says nothing else,
    /// so whoever lands here without this paragraph can answer it by deleting
    /// the declaration, and the guard would disappear leaving no trace. Answer
    /// it instead by lowering the source ceiling or by raising the budget
    /// above it.
    /// </para>
    /// </summary>
    private const uint SourceCeilingFitsTheBudget =
        MaxMemoizableSourceChars - TemplateSourceSize.MaxChars;
}
