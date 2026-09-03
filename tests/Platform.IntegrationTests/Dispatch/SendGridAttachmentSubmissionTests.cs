using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using Xunit.Abstractions;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// What the provider actually received.
/// <para>
/// Everything asserted here is read off the request the double captured, over
/// a real socket, rather than off the composition that produced it. A test
/// that compared the adapter against its own description of the message would
/// pass with any pair of matching mistakes; the receiving end is the only
/// place that can say which bytes left.
/// </para>
/// </summary>
public sealed class SendGridAttachmentSubmissionTests(ITestOutputHelper output)
{
    private const string SenderAddress = "no-reply@example.com";

    /// <summary>
    /// The whole approved envelope of raw content one notification may carry,
    /// as ratified: seven mebibytes.
    /// </summary>
    private const int ApprovedEnvelopeBytes = 7 * 1_024 * 1_024;

    private static readonly EmailDeliveryTarget Target = new("person@example.com");

    private static readonly EmailMessage Message = new(
        "Confirme sua operação", "Aguardando confirmação", "<p>Olá</p>", "Olá");

    /// <summary>
    /// The set the provider received is the set that was accepted: the same
    /// members, in the same order, each with the name, the media type and the
    /// length it was released under, and each carrying the bytes it was
    /// released over.
    /// <para>
    /// The digest is what makes the last one a claim about content rather than
    /// about size. Two attachments of equal length whose bytes were swapped
    /// would satisfy every other assertion here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_payload_carries_the_whole_set_with_its_names_types_digests_and_lengths()
    {
        byte[][] contents = [Content(3_000), Content(1), Content(48 * 1_024)];
        var custody = new AttachmentCustodyDouble();
        for (var index = 0; index < contents.Length; index++)
        {
            custody.Plant(index, contents[index]);
        }

        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "msg-anexos" }));
        await using ServiceProvider services = Host(server, custody);
        var correlation = new DispatchCorrelation(Guid.NewGuid(), Guid.NewGuid());

        ProviderResult result = await Provider(services).SendAsync(
            new DispatchRequest(Target, Message, correlation, Attachments: Set(contents)),
            CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        FakeProviderRequest captured = server.Requests.ShouldHaveSingleItem();
        captured.Path.ShouldBe("/v3/mail/send");
        captured.ContentType.ShouldBe("application/json; charset=utf-8");

        using var payload = JsonDocument.Parse(captured.Body);
        JsonElement fields = payload.RootElement.GetProperty("attachments");
        fields.GetArrayLength().ShouldBe(contents.Length);
        for (var index = 0; index < contents.Length; index++)
        {
            JsonElement field = fields[index];
            field.GetProperty("filename").GetString().ShouldBe(FileName(index));
            field.GetProperty("type").GetString().ShouldBe(MediaType(index));
            field.GetProperty("disposition").GetString().ShouldBe("attachment");

            var received = Convert.FromBase64String(field.GetProperty("content").GetString()!);
            received.LongLength.ShouldBe(contents[index].LongLength);
            Digest(received).ShouldBe(Digest(contents[index]));
        }

        // The envelope the set travels beside is untouched by carrying it.
        payload.RootElement.GetProperty("personalizations")[0]
            .GetProperty("custom_args").GetProperty("notification_id").GetString()
            .ShouldBe(correlation.NotificationId.ToString());
        custody.Opened.ShouldBe([.. Enumerable.Range(0, contents.Length)
            .Select(AttachmentCustodyDouble.HandleOf)]);
    }

    /// <summary>
    /// The length is declared before the body moves and it is the length the
    /// provider measures. Declaring it is what lets a message of any size
    /// travel without being held anywhere, and it is what makes a body that
    /// stopped short unreadable as a complete message rather than delivered
    /// as a shorter one.
    /// </summary>
    [Fact]
    public async Task The_provider_is_told_the_exact_size_of_the_body_before_it_arrives()
    {
        byte[][] contents = [Content(12_288)];
        AttachmentCustodyDouble custody = new AttachmentCustodyDouble().Plant(0, contents[0]);

        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        await using ServiceProvider services = Host(server, custody);

        await Provider(services).SendAsync(
            new DispatchRequest(Target, Message, Attachments: Set(contents)),
            CancellationToken.None);

        FakeProviderRequest captured = server.Requests.ShouldHaveSingleItem();
        captured.DeclaredContentLength.ShouldBe(captured.BodyBytes);

        // And the declared size is the arithmetic of base64 over the content,
        // not a number the transport discovered by holding the message.
        using var payload = JsonDocument.Parse(captured.Body);
        var field = payload.RootElement.GetProperty("attachments")[0]
            .GetProperty("content").GetString()!;
        field.Length.ShouldBe(4 * ((contents[0].Length + 2) / 3));
    }

    /// <summary>
    /// A message larger than one call may carry is refused before the call,
    /// and before a byte of custody is read. The set below is stated in
    /// lengths and carries no bytes at all, which is the point: the
    /// measurement reads the lengths the releases were granted over and never
    /// opens anything.
    /// <para>
    /// The zero sits next to a one. A provider that received nothing looks
    /// exactly like a host that could not have called it, so the same host,
    /// the same double and the same configuration then send a set that fits.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_message_larger_than_one_call_may_carry_never_reaches_the_provider()
    {
        AttachmentCustodyDouble custody = new AttachmentCustodyDouble().Plant(0, Content(64));

        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        await using ServiceProvider services = Host(server, custody);
        IChannelProvider provider = Provider(services);

        // Twenty-three million raw bytes are under the thirty million the
        // provider accepts, and their base64 is not.
        ProviderResult refused = await provider.SendAsync(
            new DispatchRequest(Target, Message, Attachments: Declaring(23_000_000)),
            CancellationToken.None);

        refused.Outcome.ShouldBe(ProviderOutcome.Rejected);
        refused.ErrorCode.ShouldBe("message-too-large");
        server.RequestCount.ShouldBe(0);
        custody.Opened.ShouldBeEmpty();

        ProviderResult sent = await provider.SendAsync(
            new DispatchRequest(Target, Message, Attachments: Set([Content(64)])),
            CancellationToken.None);

        sent.Outcome.ShouldBe(ProviderOutcome.Accepted);
        server.RequestCount.ShouldBe(1);
    }

    /// <summary>
    /// The whole approved envelope crosses in one call, whole and unchanged,
    /// as five members that add up to it.
    /// <para>
    /// It is the case the ratified numbers describe, and it is here because
    /// every other case in this file is small enough to hide a cost that grows
    /// with the message. The elapsed time is written out rather than asserted:
    /// what it measures on a loopback socket is composition, encoding and a
    /// local write, and turning that into a threshold would publish a number
    /// about this machine as if it were about the deployment.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_whole_approved_envelope_crosses_in_one_call()
    {
        var perMember = ApprovedEnvelopeBytes / 5;
        byte[][] contents = [.. Enumerable.Range(0, 5).Select(_ => Content(perMember))];
        var custody = new AttachmentCustodyDouble();
        for (var index = 0; index < contents.Length; index++)
        {
            custody.Plant(index, contents[index]);
        }

        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));

        // Far above the five seconds the deployment configures, on purpose:
        // this case measures what crosses, and a timeout asserted on a shared
        // machine would measure the machine.
        await using ServiceProvider services = Host(server, custody, timeoutSeconds: 60);
        var clock = Stopwatch.StartNew();

        ProviderResult result = await Provider(services).SendAsync(
            new DispatchRequest(Target, Message, Attachments: Set(contents)),
            CancellationToken.None);

        clock.Stop();
        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        FakeProviderRequest captured = server.Requests.ShouldHaveSingleItem();
        captured.DeclaredContentLength.ShouldBe(captured.BodyBytes);

        using var payload = JsonDocument.Parse(captured.Body);
        JsonElement fields = payload.RootElement.GetProperty("attachments");
        fields.GetArrayLength().ShouldBe(5);
        for (var index = 0; index < contents.Length; index++)
        {
            Digest(Convert.FromBase64String(fields[index].GetProperty("content").GetString()!))
                .ShouldBe(Digest(contents[index]));
        }

        var bodyBytes = captured.BodyBytes.ToString(CultureInfo.InvariantCulture);
        var elapsed = clock.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
        output.WriteLine(
            "envelope aprovado: "
            + ApprovedEnvelopeBytes.ToString(CultureInfo.InvariantCulture)
            + " bytes crus em 5 anexos, corpo de " + bodyBytes + " bytes, "
            + elapsed + " ms em soquete de retorno local.");
    }

    /// <summary>
    /// The deadline of a send covers the body, and the body is read out of
    /// custody while the request is being written. A custody that reads slower
    /// than the deadline therefore ends the send as a timeout, with no verdict
    /// and no complete message at the provider.
    /// <para>
    /// It is here because the deadline and the size of the approved envelope
    /// are set by different owners and meet only on this path: the send has to
    /// compose, read and push the whole envelope inside the same window, and
    /// nothing before this call measures that.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_custody_slower_than_the_deadline_ends_the_send_without_a_verdict()
    {
        byte[][] contents = [Content(384 * 1_024)];
        var custody = new AttachmentCustodyDouble { ReadDelay = TimeSpan.FromMilliseconds(300) };
        custody.Plant(0, contents[0]);

        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        await using ServiceProvider services = Host(server, custody, timeoutSeconds: 1);

        ProviderResult result = await Provider(services).SendAsync(
            new DispatchRequest(Target, Message, Attachments: Set(contents)),
            CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.TransientError);
        result.ErrorCode.ShouldBe("timeout");

        // The provider never received a message it could act on: the body was
        // still being written when the deadline ended the request.
        server.RequestCount.ShouldBe(0);
    }

    private static ServiceProvider Host(
        FakeProviderServer server,
        IAcceptedAttachmentContent custody,
        int timeoutSeconds = 5,
        IAttachmentSubmissionWitness? witness = null)
        => DispatchTestServices.BuildProviderHost(
            new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = server.BaseAddress.ToString(),
                ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "sg-test-key",
                ["Modules:Dispatch:Providers:SendGrid:SenderEmail"] = SenderAddress,
                ["Modules:Dispatch:Providers:SendGrid:TimeoutSeconds"] =
                    timeoutSeconds.ToString(CultureInfo.InvariantCulture),
            },
            services =>
            {
                services.AddSingleton(custody);
                services.AddSingleton(witness ?? new AttachmentSubmissionWitnessDouble());
            });

    /// <summary>
    /// The witness the attempt hands over is a measurement of the bytes the
    /// provider received, and not of the record that describes them.
    /// <para>
    /// Every digest asserted here is taken on the receiving end, off the
    /// base64 the double decoded out of the captured request, and held against
    /// the one the adapter measured while writing. A witness built from the
    /// released values the send was handed would agree with everything else in
    /// this file and would disagree with this: those values state a length and
    /// a name and say nothing about content, so nothing but the bytes
    /// themselves can produce these digests.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_witness_of_the_attempt_measures_the_bytes_the_provider_received()
    {
        byte[][] contents = [Content(3_000), Content(1), Content(48 * 1_024)];
        var custody = new AttachmentCustodyDouble();
        for (var index = 0; index < contents.Length; index++)
        {
            custody.Plant(index, contents[index]);
        }

        var witness = new AttachmentSubmissionWitnessDouble
        {
            Answer = AttachmentSubmissionVerdict.Matched,
        };
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        await using ServiceProvider services = Host(server, custody, witness: witness);

        ProviderResult result = await Provider(services).SendAsync(
            new DispatchRequest(
                Target,
                Message,
                new DispatchCorrelation(Guid.NewGuid(), Guid.NewGuid()),
                Attachments: Set(contents)),
            CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        IReadOnlyList<SubmittedAttachmentBytes> submitted =
            witness.Settlements.ShouldHaveSingleItem();
        submitted.Count.ShouldBe(contents.Length);

        FakeProviderRequest captured = server.Requests.ShouldHaveSingleItem();
        using var payload = JsonDocument.Parse(captured.Body);
        JsonElement fields = payload.RootElement.GetProperty("attachments");
        for (var index = 0; index < contents.Length; index++)
        {
            var received = Convert.FromBase64String(
                fields[index].GetProperty("content").GetString()!);
            submitted[index].ContentIdentity.ShouldBe(AttachmentCustodyDouble.HandleOf(index));
            submitted[index].Length.ShouldBe(received.LongLength);
            Convert.ToHexString(submitted[index].Digest.Span).ShouldBe(Digest(received));
        }
    }

    /// <summary>
    /// A send that carries no set settles no witness. It is the cheapest way
    /// this can be wrong: a settlement on every message would reach the module
    /// that owns attachments on every notification this hub sends, and would
    /// report a submission of nothing.
    /// </summary>
    [Fact]
    public async Task A_send_that_carries_no_attachment_settles_no_witness()
    {
        var witness = new AttachmentSubmissionWitnessDouble();
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        var custody = new AttachmentCustodyDouble();
        await using ServiceProvider services = Host(server, custody, witness: witness);

        ProviderResult result = await Provider(services).SendAsync(
            new DispatchRequest(Target, Message),
            CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        server.RequestCount.ShouldBe(1);
        custody.Opened.ShouldBeEmpty();
        witness.Settlements.ShouldBeEmpty();
    }

    /// <summary>
    /// A body that stopped settles no witness. The measurement of a member the
    /// writer could not finish is a digest over a prefix, and settling a
    /// submission short of a member would be read as a divergence of the bytes
    /// when what happened is that the message never arrived.
    /// <para>
    /// The set carries two members and the second is the one that breaks, so
    /// the writer has already measured one of them when the body stops. An
    /// arrangement of a single member would leave nothing measured at all, and
    /// the absence of a settlement would say nothing about partial ones.
    /// </para>
    /// <para>
    /// The zero sits next to a one, in the same host and the same double: a
    /// witness that was never reached looks exactly like one that refused, so
    /// the send that follows carries a set the custody can finish.
    /// </para>
    /// <para>
    /// What closes this today is the throw, and not the guard that also
    /// refuses a partial submission: a body that stops never reaches the
    /// settlement at all, so that guard has no runtime falsifier and is
    /// recorded here as the belt it is rather than claimed as the proof.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_body_that_stopped_settles_no_witness()
    {
        AttachmentCustodyDouble custody = new AttachmentCustodyDouble()
            .Plant(0, Content(64))
            .Plant(1, Content(61));
        var witness = new AttachmentSubmissionWitnessDouble
        {
            Answer = AttachmentSubmissionVerdict.Matched,
        };
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        await using ServiceProvider services = Host(server, custody, witness: witness);
        IChannelProvider provider = Provider(services);

        ProviderResult stopped = await provider.SendAsync(
            new DispatchRequest(Target, Message, Attachments: Declaring(64, 64)),
            CancellationToken.None);

        stopped.Outcome.ShouldBe(ProviderOutcome.TransientError);
        stopped.ErrorCode.ShouldBe("attachment-content-length-changed");
        witness.Settlements.ShouldBeEmpty();

        custody.Plant(1, Content(64));
        ProviderResult sent = await provider.SendAsync(
            new DispatchRequest(Target, Message, Attachments: Declaring(64, 64)),
            CancellationToken.None);

        sent.Outcome.ShouldBe(ProviderOutcome.Accepted);
        witness.Settlements.ShouldHaveSingleItem().Count.ShouldBe(2);
    }

    /// <summary>
    /// A host that can open the bytes and cannot settle what it sent with them
    /// does not send. The two ports are halves of one act and are composed in
    /// one call, so a role holding only the first is a composition defect: it
    /// is raised rather than answered, because a result would report a provider
    /// verdict for a set whose bytes nobody would ever be able to account for.
    /// </summary>
    [Fact]
    public async Task A_host_that_cannot_settle_the_witness_never_reaches_the_provider()
    {
        AttachmentCustodyDouble custody = new AttachmentCustodyDouble().Plant(0, Content(64));
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(202, null, null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = server.BaseAddress.ToString(),
                ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "sg-test-key",
                ["Modules:Dispatch:Providers:SendGrid:SenderEmail"] = SenderAddress,
                ["Modules:Dispatch:Providers:SendGrid:TimeoutSeconds"] = "5",
            },
            withoutWitness => withoutWitness.AddSingleton<IAcceptedAttachmentContent>(custody));

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await Provider(services).SendAsync(
                new DispatchRequest(Target, Message, Attachments: Set([Content(64)])),
                CancellationToken.None));

        server.RequestCount.ShouldBe(0);
        custody.Opened.ShouldBeEmpty();
    }

    private static IChannelProvider Provider(ServiceProvider services)
        => DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

    private static AcceptedAttachmentSet Set(byte[][] contents)
        => AcceptedAttachmentSet.Of(
            contents.Select((content, index) => Item(index, content.LongLength)));

    /// <summary>
    /// A set that states lengths and carries no bytes, which is all the
    /// measurement before the call ever reads.
    /// </summary>
    private static AcceptedAttachmentSet Declaring(params long[] lengths)
        => AcceptedAttachmentSet.Of(lengths.Select((length, index) => Item(index, length)));

    private static AcceptedAttachment Item(int index, long length) => new()
    {
        Reference = "att_" + index.ToString(CultureInfo.InvariantCulture),
        ContentIdentity = AttachmentCustodyDouble.HandleOf(index),
        Name = FileName(index),
        MediaType = MediaType(index),
        Length = length,
    };

    private static string FileName(int index)
        => "comprovante-" + index.ToString(CultureInfo.InvariantCulture) + ".pdf";

    private static string MediaType(int index) => index % 2 == 0 ? "application/pdf" : "image/png";

    private static byte[] Content(int length) => RandomNumberGenerator.GetBytes(length);

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
}
