using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class LinkDomainPolicyTests
{
    [Theory]
    [InlineData("//evil.com/x", "evil.com")]
    [InlineData("evil.com/x", "evil.com")]
    [InlineData("www.evil.com", "www.evil.com")]
    [InlineData("https://ok.com/a", "ok.com")]
    [InlineData("bit.ly/3xYz", "bit.ly")]
    [InlineData("acesse evil.com", "evil.com")]
    public void Text_that_offers_something_to_click_yields_its_host(string text, string expectedHost)
        => LinkDomainPolicy.HostsIn(text).ShouldHaveSingleItem().ShouldBe(expectedHost);

    [Theory]
    [InlineData("12.05/2026")]
    [InlineData("v1.2/beta")]
    [InlineData("arquivo.txt/")]
    [InlineData("R$ 1.234,56/mês")]
    [InlineData("Rua Sete, 1.234/ap 2")]
    [InlineData("CNPJ 12.345.678/0001-90")]
    [InlineData("Nota fiscal 1.234/56 emitida")]
    [InlineData("Processo 0001234-56.2026/8.26.0001")]
    [InlineData("Cláusula 3.1/b do contrato")]
    [InlineData("Índice 1.5/2.0 no período")]
    [InlineData("Enviado por João A. Silva")]
    public void Brazilian_number_formats_yield_no_host(string text)
        => LinkDomainPolicy.HostsIn(text).ShouldBeEmpty();

    [Theory]
    [InlineData("//{{ host }}/x")]
    [InlineData("https://{{ host }}/x")]
    public void A_placeholder_in_host_position_yields_a_host_that_is_not_a_literal_domain(string text)
    {
        IReadOnlyList<string> hosts = LinkDomainPolicy.HostsIn(text);

        hosts.ShouldHaveSingleItem().ShouldBe(LinkDomainPolicy.UnresolvedHost);
        LinkDomainPolicy.FirstDisallowedHost(text, MakeTemplate()).ShouldBe(LinkDomainPolicy.UnresolvedHost);
    }

    [Fact]
    public void A_userinfo_prefix_does_not_hide_the_real_host()
    {
        const string Text = "https://montebravo.com.br@evil.com/x";

        IReadOnlyList<string> hosts = LinkDomainPolicy.HostsIn(Text);

        hosts.Count.ShouldBe(2);
        hosts[0].ShouldBe("evil.com");
        hosts[1].ShouldBe(LinkDomainPolicy.UnresolvedHost);
        LinkDomainPolicy.FirstDisallowedHost(Text, MakeTemplate()).ShouldBe("evil.com");
    }

    [Theory]
    [InlineData("//")]
    [InlineData("http://")]
    [InlineData("//?x=1")]
    [InlineData("acesse // agora")]
    public void A_blank_host_never_reaches_the_allowlist(string text)
    {
        LinkDomainPolicy.HostsIn(text).ShouldAllBe(host => host.Trim().Length > 0);

        Should.NotThrow(() => { _ = LinkDomainPolicy.FirstDisallowedHost(text, MakeTemplate()); });
    }

    [Theory]
    [InlineData("""
        <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">
        """)]
    [InlineData("""<html xmlns="http://www.w3.org/1999/xhtml">""")]
    [InlineData("""<html xmlns:v="urn:schemas-microsoft-com:vml">""")]
    [InlineData("<!DOCTYPE html>")]
    public void An_html_construct_that_carries_a_uri_without_offering_a_link_yields_no_host(string markup)
    {
        LinkDomainPolicy.HostsIn(LinkDomainPolicy.WithoutNonLinkUri(markup)).ShouldBeEmpty();
        LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBeNull();
    }

    [Fact]
    public void A_link_next_to_a_doctype_still_yields_its_host()
    {
        const string Markup = """
            <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.0 Transitional//EN" "http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd">
            <html xmlns="http://www.w3.org/1999/xhtml"><a href="https://evil.example.io/x">ir</a></html>
            """;

        LinkDomainPolicy.HostsIn(LinkDomainPolicy.WithoutNonLinkUri(Markup))
            .ShouldHaveSingleItem()
            .ShouldBe("evil.example.io");
        LinkDomainPolicy.FirstDisallowedHostInMarkup(Markup, MakeTemplate()).ShouldBe("evil.example.io");
    }

    [Fact]
    public void An_entity_encoded_scheme_in_markup_does_not_hide_its_host()
    {
        const string Markup = """<img src="HTTPS&#58;//evil.example.io/pixel">""";

        LinkDomainPolicy.FirstDisallowedHostInMarkup(Markup, MakeTemplate()).ShouldBe("evil.example.io");
    }

    [Theory]
    [InlineData("https://[2001:db8::1]/pay", "2001:db8::1")]
    [InlineData("https://аpple.com/pay", "xn--pple-43d.com")]
    [InlineData("//аpple.com/pay", "xn--pple-43d.com")]
    public void An_announced_destination_yields_its_canonical_uri_host(string destination, string expectedHost)
    {
        LinkDomainPolicy.HostsIn(destination).ShouldHaveSingleItem().ShouldBe(expectedHost);
        LinkDomainPolicy.FirstDisallowedHost(destination, MakeTemplate()).ShouldBe(expectedHost);
    }

    [Theory]
    [InlineData("https://assets.montebravo.com.br/pay", true)]
    [InlineData("//assets.montebravo.com.br/pay", false)]
    public void A_url_variable_still_requires_an_absolute_http_destination(string destination, bool expected)
    {
        JsonElement value = JsonSerializer.SerializeToElement(destination);

        LinkDomainPolicy.IsAllowedUrlValue(MakeTemplate(), value).ShouldBe(expected);
    }

    /// <summary>
    /// The separator between a scheme and its authority is whatever a client
    /// skips, and a client skips every slash and backslash it finds, the empty
    /// run included. Each spelling below reaches the same host, and a detector
    /// that demanded the two canonical slashes was stricter than the canonizer
    /// it feeds: the value never arrived, no host came back, and the verdict
    /// was to approve.
    /// </summary>
    [Theory]
    [InlineData("""https:\\evil.example""")]
    [InlineData("""https:/\evil.example""")]
    [InlineData("""https:evil.example""")]
    [InlineData("""https:/evil.example""")]
    [InlineData("""https:///evil.example""")]
    [InlineData("""\\evil.example""")]
    [InlineData("""HTTPS:\\evil.example""")]
    [InlineData("""http:\\evil.example""")]
    public void Any_run_of_slashes_after_a_scheme_still_yields_the_host(string destination)
    {
        LinkDomainPolicy.HostsIn(destination).ShouldHaveSingleItem().ShouldBe("evil.example");
        LinkDomainPolicy.FirstDisallowedHost(destination, MakeTemplate()).ShouldBe("evil.example");
    }

    /// <summary>
    /// A scheme with nothing to resolve after it stays prose. The authority is
    /// empty, and an empty authority offers no destination.
    /// </summary>
    [Theory]
    [InlineData("veja o protocolo http: no manual")]
    [InlineData("informe a URL no formato https:// para continuar")]
    [InlineData("o limite vale das 10:00 às 18:30")]
    public void A_scheme_with_no_authority_yields_no_host(string text)
        => LinkDomainPolicy.HostsIn(text).ShouldBeEmpty();

    /// <summary>
    /// A URL parser removes the tab, the line feed, and the carriage return
    /// from the string it is handed, before it reads the scheme or the
    /// authority. An attribute value that carries one therefore reaches a
    /// different host than the one written in front of it, and a character
    /// reference delivers those characters as readily as a literal.
    /// <para>
    /// The order is the finding: the detector's authority stops at the first
    /// whitespace, so a value cleaned after the candidate has been cut is
    /// cleaned too late, and the host the client reaches was already thrown
    /// away. The second separator also breaks the dot of the top-level domain,
    /// which takes the plausible-suffix gate out of the way and leaves every
    /// domain reachable.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("""<a href="https://assets.montebravo.com.br&#9;@evil&#9;.example">ir</a>""")]
    [InlineData("""<a href="https://assets.montebravo.com.br&#10;@evil&#10;.example">ir</a>""")]
    [InlineData("""<a href="https://assets.montebravo.com.br&#13;@evil&#13;.example">ir</a>""")]
    [InlineData("""<a href="https://assets.montebravo.com.br&#x09;@evil&#x09;.example">ir</a>""")]
    [InlineData("""<img src="https://assets.montebravo.com.br&#9;@evil&#9;.example">""")]
    [InlineData("""<form action="https://assets.montebravo.com.br&#9;@evil&#9;.example"></form>""")]
    public void A_separator_removed_by_the_client_does_not_truncate_the_authority(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBe("evil.example");

    [Fact]
    public void A_separator_removed_by_the_client_does_not_hide_a_foreign_subdomain()
    {
        const string Markup =
            """<a href="https://assets.montebravo.com.br&#9;.evil&#9;.example">ir</a>""";

        LinkDomainPolicy.FirstDisallowedHostInMarkup(Markup, MakeTemplate())
            .ShouldBe("assets.montebravo.com.br.evil.example");
    }

    /// <summary>
    /// A destination that carries its payload instead of naming a place is
    /// refused rather than measured, because the allowlist decides by host and
    /// there is no host in it to decide on. With no host to name, the refusal
    /// is the fixed marker: the value is attacker-supplied and never becomes
    /// the detail of an error.
    /// </summary>
    [Theory]
    [InlineData("""<a href="data:text/html;base64,PGh0bWw+PC9odG1sPg==">ir</a>""")]
    [InlineData("""<a href="blob:https://assets.montebravo.com.br/1234">ir</a>""")]
    [InlineData("""<a href="d&#9;ata:text/html;base64,PGh0bWw+PC9odG1sPg==">ir</a>""")]
    [InlineData("""<img src="data:image/png;base64,AAAA">""")]
    public void A_destination_with_a_scheme_this_catalog_does_not_deliver_is_refused(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate())
            .ShouldBe(LinkDomainPolicy.UnresolvedHost);

    /// <summary>
    /// A refused scheme still names the host it carried, when it carried one.
    /// The author has to read that host to decide whether to rewrite the
    /// destination or widen the allowed domains, and answering every one of
    /// these with the same marker made a foreign address indistinguishable from
    /// an unreadable value.
    /// </summary>
    [Theory]
    [InlineData("""<a href="javascript:fetch('https://evil.example')">ir</a>""")]
    [InlineData("""<a href="blob:https://evil.example/1234">ir</a>""")]
    public void A_refused_scheme_still_names_the_host_it_carried(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate())
            .ShouldBe("evil.example");

    /// <summary>
    /// A destination with no scheme resolves against the message it arrived in,
    /// which is the sender's own document, so it stays. So does the one shape
    /// with a scheme that names a person rather than a place.
    /// </summary>
    [Theory]
    [InlineData("""<a href="/comprovante/2026">ir</a>""")]
    [InlineData("""<a href="#detalhes">ir</a>""")]
    [InlineData("""<a href="comprovante.html">ir</a>""")]
    [InlineData("""<a href="">ir</a>""")]
    [InlineData("""<a href="mailto:atendimento@montebravo.com.br">falar</a>""")]
    [InlineData("""<a href="https://assets.montebravo.com.br/comprovante">ir</a>""")]
    public void A_relative_or_deliverable_destination_is_accepted(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBeNull();

    /// <summary>
    /// The authority the canonizer receives runs to the first real URL
    /// delimiter, and to nothing else.
    /// <para>
    /// A URL parser ends an authority at a slash, a backslash, a question mark,
    /// a number sign, or the end of the value. It carries a quote, an angle
    /// bracket, and every kind of space into the userinfo and reads the host
    /// after the at sign. Every character below therefore leaves the client on
    /// evil.example while a detector that stopped there read the allowed name
    /// in front of it and approved. Removing three of them by name closed three
    /// spellings; handing over the whole authority closes the shape.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("&quot;")]
    [InlineData("&#34;")]
    [InlineData("&#x22;")]
    [InlineData("&apos;")]
    [InlineData("&#39;")]
    [InlineData("&lt;")]
    [InlineData("&gt;")]
    [InlineData("&nbsp;")]
    [InlineData("&#160;")]
    [InlineData("&#8201;")]
    [InlineData("&#12288;")]
    [InlineData("&#9;")]
    [InlineData("&#10;")]
    [InlineData("&#13;")]
    [InlineData(" ")]
    public void A_separator_the_candidate_cannot_carry_does_not_truncate_the_authority(string planted)
    {
        var markup = $"""<a href="https://assets.montebravo.com.br{planted}@evil.example">ir</a>""";

        LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBe("evil.example");
    }

    [Theory]
    [InlineData("href")]
    [InlineData("src")]
    [InlineData("action")]
    [InlineData("poster")]
    [InlineData("srcset")]
    [InlineData("ping")]
    [InlineData("cite")]
    public void A_truncating_separator_is_read_in_every_uri_bearing_attribute(string attribute)
    {
        var markup = $"""<a {attribute}="https://assets.montebravo.com.br&quot;@evil.example">ir</a>""";

        LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBe("evil.example");
    }

    /// <summary>
    /// A doubled slash inside a path is the commonest string defect in an email
    /// template, and it is not a destination of its own. The guard runs on the
    /// rendered result, so reading one as a host stops a template that already
    /// publishes from rendering at all.
    /// </summary>
    [Theory]
    [InlineData("""<img src="https://assets.montebravo.com.br//imagens/logo.png">""")]
    [InlineData("""<a href="https://assets.montebravo.com.br//pt-br/faturas/2026">ir</a>""")]
    [InlineData("""<img src="https://assets.montebravo.com.br/t//open.gif?id=abc">""")]
    [InlineData("""<div style="background:url(https://assets.montebravo.com.br//imagens/fundo.png)"></div>""")]
    public void A_doubled_slash_inside_a_path_is_not_a_destination_of_its_own(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBeNull();

    /// <summary>
    /// A separator that carries nothing leaves a destination spelled exactly
    /// like ordinary writing, so it has to earn the reading. These are the
    /// shapes an operator writes in a note about configuration, measured over
    /// Portuguese operational text, and none of them is an address.
    /// </summary>
    [Theory]
    [InlineData("codigo HTTP:200 retornado pelo parceiro")]
    [InlineData("HTTPS:443 liberada no firewall")]
    [InlineData("erro HTTP:404 na consulta")]
    [InlineData("status http:500 no parceiro")]
    [InlineData("http:port nao configurado")]
    [InlineData("Https:Sim no cadastro")]
    [InlineData("campo https:true no contrato")]
    [InlineData("""compartilhamento \\fileserver\notas""")]
    [InlineData("razao 2//3 do total")]
    [InlineData("protocolo https: descrito no manual")]
    public void A_note_about_configuration_is_not_a_destination(string text)
        => LinkDomainPolicy.FirstDisallowedHost(text, MakeTemplate()).ShouldBeNull();

    /// <summary>
    /// What that gate keeps: an address written as one number is still an
    /// address, and dropping it would have traded the whole slash-less family
    /// for the false positives above.
    /// </summary>
    [Theory]
    [InlineData("https:3232235777", "192.168.1.1")]
    [InlineData("https:evil.example", "evil.example")]
    [InlineData("https:[2001:db8::1]", "2001:db8::1")]
    public void An_address_written_without_a_separator_is_still_an_address(string text, string expectedHost)
        => LinkDomainPolicy.FirstDisallowedHost(text, MakeTemplate()).ShouldBe(expectedHost);

    /// <summary>
    /// A destination the canonizer cannot read fails closed, whatever it is
    /// spelled like.
    /// <para>
    /// A host parser percent-decodes before it resolves a name, so
    /// <c>evil%2Eru</c> is the domain evil.ru; System.Uri does not, and refuses.
    /// The gate that decides whether a sparsely separated value is writing has
    /// to answer that refusal with the unreadable marker and never with
    /// silence: silence dropped the candidate, left no host behind it, and took
    /// the class-wide ban on links with it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("""<a href="//evil%2Eru/p">ir</a>""")]
    [InlineData("""<a href="//evil%2eru/p">ir</a>""")]
    [InlineData("""<a href="//evil%2Ecom/s">ir</a>""")]
    [InlineData("""<a href="//%65vil%2Ecom">ir</a>""")]
    [InlineData("""<a href="//user@evil%2Eru/p">ir</a>""")]
    [InlineData("""<img src="//evil%2Eru/p">""")]
    [InlineData("""<form action="//evil%2Eru/p"></form>""")]
    public void A_destination_the_canonizer_cannot_read_fails_closed(string markup)
    {
        LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate())
            .ShouldBe(LinkDomainPolicy.UnresolvedHost);

        // The class-wide ban on links reads the same list, so an empty one
        // disarms it as quietly as it disarms the allowlist.
        LinkDomainPolicy.HostsIn(LinkDomainPolicy.MarkupForLinkScan(markup)).ShouldNotBeEmpty();
    }

    /// <summary>
    /// An attribute that holds a list of destinations is read one destination
    /// at a time. Its separators are ASCII whitespace and commas, and a
    /// separator that became a percent-encoding welded two entries together and
    /// hid the second one behind the first.
    /// </summary>
    [Theory]
    [InlineData("""<img srcset="https://assets.montebravo.com.br/a.png 1x,//evil.example/b.png 2x">""")]
    [InlineData("""<img srcset="https://assets.montebravo.com.br/a.png 1x, //evil.example/b.png 2x">""")]
    [InlineData("""<a href="/x" ping="https://assets.montebravo.com.br/p,//evil.example/p">ir</a>""")]
    [InlineData("""<img srcset="https://assets.montebravo.com.br&nbsp;@evil.example 1x">""")]
    public void A_second_entry_of_a_list_attribute_is_read_as_its_own_destination(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBe("evil.example");

    /// <summary>
    /// The same rule the other way round: an entry with no path of its own is
    /// still a whole destination, and the descriptor beside it is not part of
    /// the authority.
    /// </summary>
    [Theory]
    [InlineData("""<img srcset="https://assets.montebravo.com.br 1x, https://cdn.montebravo.com.br 2x">""")]
    [InlineData("""<a ping="https://assets.montebravo.com.br https://cdn.montebravo.com.br">ir</a>""")]
    [InlineData("""<img srcset="https://assets.montebravo.com.br/a.png 480w, https://assets.montebravo.com.br/b.png 800w">""")]
    public void An_entry_of_a_list_attribute_keeps_its_separator(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBeNull();

    /// <summary>
    /// A long body does not switch the guard off.
    /// <para>
    /// The detector runs on NonBacktracking, and on this runtime that engine
    /// paired with a match timeout answers "no match" instead of matching or
    /// throwing, once the stretch in front of the first match passes roughly a
    /// hundred thousand characters. Nothing throws, so the refusal that fails
    /// closed is never reached, and "no match" reads as "no host", which reads
    /// as approval. The whole preparation of an attribute destination sits
    /// behind that one call, so above the turn there is no removal, no
    /// percent-encoding and no scheme allowlist left: an author switches the
    /// defence off by writing a long enough body, and the render ceiling is a
    /// million characters.
    /// </para>
    /// <para>
    /// The filler is chosen to match nothing until the destination at the end,
    /// because the turn is measured against the unmatched stretch and not
    /// against the size of the body. The last two rows are the control: they
    /// are found by the scan itself rather than by the preparation, and they
    /// were still refused above the turn while the four above them were not.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("<a href=\"https://assets.montebravo.com.br&quot;@evil.example\">ir</a>", "evil.example")]
    [InlineData("<a href=\"https://assets.montebravo.com.br\u00A0@evil.example\">ir</a>", "evil.example")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">ir</a>", LinkDomainPolicy.UnresolvedHost)]
    [InlineData("<a href=\"javascript:alert(1)\">ir</a>", LinkDomainPolicy.UnresolvedHost)]
    [InlineData("<div style=\"background:url('https://evil.example.io/pagar')\"></div>", "evil.example.io")]
    [InlineData("<a href=\"https://evil.example.io/pagar\">ir</a>", "evil.example.io")]
    [InlineData("<img src=\"cid:logo123\">", null)]
    [InlineData("<a href=\"tel:+5511999999999\">ligar</a>", null)]
    public void A_long_body_does_not_switch_the_guard_off(string destination, string? expectedHost)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(UnmatchedFiller + destination, MakeTemplate())
            .ShouldBe(expectedHost);

    /// <summary>
    /// Markup that offers no destination at all, long enough to carry the
    /// detector past the turn measured above.
    /// </summary>
    private static readonly string UnmatchedFiller = BuildUnmatchedFiller();

    private static string BuildUnmatchedFiller()
    {
        var builder = new StringBuilder(160_000);
        while (builder.Length < 150_000)
        {
            builder.Append("""<td style="padding:8px;color:#333">texto comum</td>""");
        }

        return builder.ToString();
    }

    /// <summary>
    /// A scheme that names no place at all is delivered. A mailbox, a telephone
    /// number and a part of the message the reader already holds reach nothing
    /// on the network, so the allowed domains have nothing to rule on and
    /// nothing to protect. Refusing them stopped an inline image, which is how
    /// mail has always carried one, from rendering at all.
    /// </summary>
    [Theory]
    [InlineData("""<img src="cid:logo123">""")]
    [InlineData("""<img src="cid:image001.png@01DA1234.5678ABCD">""")]
    [InlineData("""<img src="cid:6f1e0d8a-2b3c-4d5e-8f90-a1b2c3d4e5f6">""")]
    [InlineData("""<a href="tel:+5511999999999">ligar</a>""")]
    [InlineData("""<a href="mailto:atendimento@montebravo.com.br">falar</a>""")]
    public void A_scheme_that_names_no_place_is_delivered(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBeNull();

    /// <summary>
    /// None of those three has an authority, so a value that opens one is not
    /// one of them whatever it wrote before the colon. The refusal does not
    /// depend on a client declining to read a host where the scheme defines
    /// none, which is exactly the reading nobody should have to guarantee.
    /// </summary>
    [Theory]
    [InlineData("""<img src="cid://evil.example/x">""")]
    [InlineData("""<img src="cid://user:pass@evil.example/x">""")]
    [InlineData("""<a href="tel://evil.example">x</a>""")]
    [InlineData("""<a href="mailto://evil.example/x">x</a>""")]
    public void A_scheme_that_names_no_place_may_not_open_an_authority(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate())
            .ShouldBe(LinkDomainPolicy.UnresolvedHost);

    /// <summary>
    /// Delivering the scheme does not exempt the value. Whatever the rest of it
    /// holds is prepared and scanned exactly as it would be anywhere else, so
    /// an address written inside one of them is read as an address.
    /// </summary>
    [Theory]
    [InlineData("""<img src="cid:logo.png#https://evil.example.io">""")]
    [InlineData("""<a href="tel:+5511999999999?url=https://evil.example.io">x</a>""")]
    [InlineData("""<img src="cid:logo@evil.example.io">""")]
    [InlineData("""<img src="cid:logo&quot;@evil.example.io">""")]
    [InlineData("""<a href="tel:+55&nbsp;@evil.example.io">x</a>""")]
    public void A_destination_written_inside_a_hostless_scheme_is_still_read(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBe("evil.example.io");

    /// <summary>
    /// The scheme is compared whole. A name that merely ends in one of them is
    /// a different scheme and stays refused.
    /// </summary>
    [Theory]
    [InlineData("""<img src="xcid:logo123">""")]
    [InlineData("""<img src="acid:logo123">""")]
    [InlineData("""<a href="nottel:+5511999999999">x</a>""")]
    [InlineData("""<a href="notmailto:x@montebravo.com.br">x</a>""")]
    public void A_scheme_is_read_whole_and_not_as_an_ending(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate())
            .ShouldBe(LinkDomainPolicy.UnresolvedHost);

    /// <summary>
    /// The separators a URL parser removes come out before the scheme is read,
    /// so the decision is taken on the same text the client parses and a
    /// control character cannot turn one scheme into another in either
    /// direction.
    /// </summary>
    [Theory]
    [InlineData("""<img src="c&#9;id:logo123">""")]
    [InlineData("""<img src="cid:logo&#10;123">""")]
    public void A_separator_does_not_turn_one_scheme_into_another(string markup)
        => LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, MakeTemplate()).ShouldBeNull();

    /// <summary>
    /// Approval implies the client lands inside the allowlist, over the product
    /// of the axes both findings ran along: the run of slashes after the
    /// scheme, which of the three characters a URL parser removes is planted,
    /// and where in the authority it sits.
    /// <para>
    /// The oracle is built the other way round on purpose. It never cuts a
    /// candidate out of the markup: it takes the attribute value whole, decodes
    /// its character references, removes the tab, the line feed, and the
    /// carriage return, skips the slashes a special scheme allows in front of
    /// its authority, and asks System.Uri for the host. Both defects this
    /// covers were defects of extraction, so the reading that does not extract
    /// is the one that sees them, and it is the reading that catches the next
    /// spelling nobody wrote down here.
    /// </para>
    /// <para>
    /// What it cannot see is named, because the oracle and the guard share one
    /// parser. Where System.Uri and a browser's host parser disagree, both
    /// sides of the comparison are wrong together and the walk stays silent:
    /// the percent-encoded dot is that case, and
    /// <see cref="A_destination_the_canonizer_cannot_read_fails_closed"/> is
    /// what covers it.
    /// </para>
    /// </summary>
    [Fact]
    public void An_approved_markup_destination_lands_where_the_allowlist_permits()
    {
        Template template = MakeTemplate();
        List<string> escapes = [];
        var examined = 0;

        foreach (var destination in ClientReadableDestinations())
        {
            // The same destination in three carriers: alone, as the second
            // entry of a comma-separated list, and as the second entry of a
            // space-separated one. A list separator is a value boundary, and a
            // boundary that stops being one hides everything behind it.
            // An HTML parser resolves the character references first and the
            // list grammar splits afterwards, so the oracle reads them in that
            // order too.
            var decoded = WebUtility.HtmlDecode(destination);
            (string Markup, string Url)[] carriers =
            [
                ($"""<a href="{destination}">ir</a>""", decoded),
                ($"""<img srcset="https://{Permitted}/a.png 1x,{destination} 2x""" + "\">", FirstListEntry(decoded)),
                ($"""<a ping="https://{Permitted}/p {destination}">ir</a>""", FirstListEntry(decoded)),
            ];

            foreach ((var markup, var url) in carriers)
            {
                examined++;
                if (LinkDomainPolicy.FirstDisallowedHostInMarkup(markup, template) is not null)
                {
                    continue;
                }

                var reached = ClientHost(url);
                if (reached is not null && !template.IsLinkDomainAllowed(reached))
                {
                    escapes.Add($"{markup} reaches {reached}");
                }
            }
        }

        // A generator that stopped producing would leave this test green
        // without having read a single destination.
        examined.ShouldBe(27216);
        escapes.ShouldBeEmpty();
    }

    private const string Permitted = "assets.montebravo.com.br";

    private static IEnumerable<string> ClientReadableDestinations()
    {
        const string Allowed = Permitted;
        string[] separators = ["//", @"\\", @"/\", @"\/", "/", "", "///"];
        string[] schemes = ["https:", "http:", "HTTPS:", ""];
        string[] planted =
        [
            "", "&#9;", "&#10;", "&#13;", "&#x09;", "\t",
            "&quot;", "&#34;", "&#x22;", "&apos;", "&#39;", "&lt;", "&gt;",
            "&nbsp;", "&#160;", "&#8201;", "&#12288;", " ",
        ];
        string[] authorities =
        [
            Allowed,
            "evil.example.io",
            "evil.example",

            // A host parser percent-decodes before it resolves a name, so
            // these spell the same domain and System.Uri refuses all three.
            "evil%2Eexample",
            "evil%2eexample",
            "%65vil%2Eexample",
        ];

        foreach (var scheme in schemes)
        {
            foreach (var separator in separators)
            {
                foreach (var authority in authorities)
                {
                    foreach (var mark in planted)
                    {
                        yield return scheme + separator + authority + mark + "/pagina";
                        yield return scheme + separator + Allowed + mark + "@" + authority + "/pagina";
                        yield return scheme + separator + Allowed + mark + "." + authority + "/pagina";
                    }
                }
            }
        }
    }

    /// <summary>
    /// The URL of one srcset or ping entry. Both grammars split on ASCII
    /// whitespace and commas before a single URL is parsed, so an entry ends
    /// there and the rest is a descriptor or the next entry. Reading a whole
    /// list value as one URL is what an oracle does when it forgets which
    /// carrier it is looking at.
    /// </summary>
    private static string FirstListEntry(string destination)
    {
        var end = destination.AsSpan().IndexOfAny(ListSeparators);
        return end < 0 ? destination : destination[..end];
    }

    private static readonly SearchValues<char> ListSeparators =
        SearchValues.Create([' ', '\t', '\n', '\r', '\f', ',']);

    /// <summary>
    /// The host a client reaches for a value whose character references are
    /// already resolved: the run of slashes a special scheme allows in front of
    /// its authority is skipped, and the rest is handed to System.Uri whole.
    /// <para>
    /// Nothing else is taken out of the value first. An earlier version of this
    /// oracle removed the tab, the line feed, and the carriage return before
    /// asking, which quietly taught it the same three-character habit the
    /// implementation had, and it went blind to every other character that ends
    /// an authority for a detector and does not end one for a parser.
    /// </para>
    /// </summary>
    private static string? ClientHost(string value)
    {
        string scheme;
        if (value.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "https:";
            value = value[6..];
        }
        else if (value.StartsWith("http:", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "http:";
            value = value[5..];
        }
        else if (value.Length >= 2 && IsAuthoritySlash(value[0]) && IsAuthoritySlash(value[1]))
        {
            scheme = "https:";
        }
        else
        {
            // Relative to the document the message itself is, so it names no
            // host of its own.
            return null;
        }

        var skipped = 0;
        while (skipped < value.Length && IsAuthoritySlash(value[skipped]))
        {
            skipped++;
        }

        return Uri.TryCreate(scheme + "//" + value[skipped..], UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.IdnHost
            : null;
    }

    private static bool IsAuthoritySlash(char character)
        => character is '/' or '\\';

    private static Template MakeTemplate()
        => Template.Create(TemplateKey.Create("orders.status.changed").Value!, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = "order-updates",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = Locale.Create("pt-BR").Value,
            LinkDomainsAllowed = ["montebravo.com.br"],
        }).Value!;
}
