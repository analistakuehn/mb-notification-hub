using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Pins the variable set the analysis reports for every shape of the template
/// language that reads or writes a name. The collector decides on its own what
/// counts as a read, what counts as a write and which subtree it walks at all,
/// so any change to how the syntax tree is traversed has to leave this table
/// untouched, character for character.
/// </summary>
/// <remarks>
/// This is a characterization table and not a specification: it records what
/// the collector does today, including the corners where what it does is
/// arguable. Two of them are marked at their rows. A row that changes on
/// purpose is a decision about the validation an author sees, and it is made
/// by editing the expectation, never by letting a refactor move it.
/// </remarks>
public sealed class GlobalVariableCollectionCharacterizationTests
{
    [Theory]
    // A bare global is a read; a member is a member of its target and never a
    // name of its own, however long the chain gets.
    [InlineData("{{ user }}", "user")]
    [InlineData("{{ user.name }}", "user")]
    [InlineData("{{ a.b.c.d }}", "a")]
    [InlineData("{{ (grouped.value) }}", "grouped")]
    [InlineData("{{ this.name }}", "")]
    // An index is an expression and keeps being read; the member hanging off
    // the indexer is still a member.
    [InlineData("{{ items[index] }}", "index,items")]
    [InlineData("{{ items[0].label }}", "items")]
    [InlineData("{{ nested.deep[key].other }}", "key,nested")]
    // A loop variable is written by the loop, and the write covers the whole
    // source rather than the body it is scoped to.
    [InlineData("{{ for item in items }}{{ item.label }}{{ end }}", "items")]
    [InlineData("{{ for item in items }}{{ end }}{{ item }}", "items")]
    [InlineData("{{ for i in 1..limit }}{{ i }}{{ end }}", "limit")]
    // Characterized corner: 'tablerow' declares its variable exactly like
    // 'for', and the collector reports it as an undeclared read anyway.
    [InlineData("{{ tablerow item in items }}{{ item.label }}{{ end }}", "item,items")]
    // An assignment writes its target when the target is a name, and reads
    // everything on the right of it. A member target writes nothing and leaves
    // the root of the member being read.
    [InlineData("{{ total = amount + fee }}{{ total }}", "amount,fee")]
    [InlineData("{{ user.name = value }}", "user,value")]
    [InlineData("{{ $local = amount }}{{ $local }}", "amount")]
    [InlineData("{{ chosen = flag ? yes_value : no_value }}{{ chosen }}", "flag,no_value,yes_value")]
    // A function declares its own name and its parameters; its body still
    // reads whatever it did not declare.
    [InlineData("{{ func label(x)\nret x + suffix\nend }}{{ label total }}", "suffix,total")]
    [InlineData("{{ func pair(first, second)\nret first + second\nend }}{{ pair alpha beta }}", "alpha,beta")]
    // Blocks and calls carry no declaration of their own.
    [InlineData("{{ if flag }}{{ yes }}{{ else }}{{ no }}{{ end }}", "flag,no,yes")]
    [InlineData("{{ while condition }}{{ body }}{{ end }}", "body,condition")]
    [InlineData("{{ case value }}{{ when option }}{{ hit }}{{ end }}", "hit,option,value")]
    [InlineData("{{ with user }}{{ name }}{{ end }}", "name,user")]
    [InlineData("{{ do }}{{ side }}{{ end }}", "side")]
    [InlineData("{{ call_me arg }}", "arg,call_me")]
    [InlineData("{{ if a.empty? }}x{{ end }}", "a")]
    // Characterized corner: 'capture' names the variable it fills, and the
    // collector reports that name as an undeclared read.
    [InlineData("{{ capture chunk }}{{ body }}{{ end }}{{ chunk }}", "body,chunk")]
    // Characterized corner: a key of an object literal is reported as a read.
    [InlineData("{{ x = { key: value } }}{{ x.key }}", "key,value")]
    [InlineData("{{ list = [first, second] }}{{ list[0] }}", "first,second")]
    // The sandbox builtins are declared by the engine, never by a schema.
    [InlineData("{{ code | string.upcase }}", "code")]
    [InlineData("{{ string.upcase code }}{{ date.now }}{{ math.round value }}", "code,value")]
    public void The_reported_variable_set_is_pinned_for_every_shape_that_names_something(
        string source,
        string expected)
    {
        TemplateSourceAnalysis analysis = Engine().Analyze(source, "body");

        analysis.ParseSucceeded.ShouldBeTrue(analysis.ParseError);
        string.Join(",", analysis.UsedVariables).ShouldBe(expected);
    }

    private static ScribanTemplateEngine Engine()
        => new(Options.Create(new TemplatingOptions()), new ScribanParseCache());
}
