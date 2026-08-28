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
