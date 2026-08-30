using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// How large a rendered SMS may be, published so a consumer can explain a
/// refusal and an operator can reason about a budget in the same unit this
/// module refuses in. This module owns the rule because it owns what the rule
/// protects: the text it composes and hands a provider.
/// <para>
/// There is deliberately no assessment entry point here, and that absence is
/// the design and not an omission. Nothing can answer this question about a
/// request, because the size that matters belongs to text that does not exist
/// until the render has interpolated the variables, framed the body in the
/// pinned layout and normalized the result for the channel. A published gate
/// would be a contract no caller could satisfy and every caller would believe,
/// which is worse than no gate: a consumer would check it, pass, and still be
/// refused at the render.
/// </para>
/// <para>
/// What a consumer can do with these two members is explain and predict. The
/// number and its unit tell a producer what the refusal meant, and the counter
/// answers exactly what the render will answer for a text the consumer already
/// has in hand, which is what the preview surface uses to let an author check
/// a version before anyone requests it.
/// </para>
/// </summary>
public static class SmsSegmentLimit
{
    /// <summary>
    /// Segments a rendered SMS may occupy. Segments and not characters,
    /// because the segment is what the carrier splits, bills and delivers, and
    /// because the same text costs a different number of them depending on
    /// which characters it carries.
    /// </summary>
    public static int MaxSegments => SmsSegmentCeiling.MaxSegments;

    /// <summary>
    /// Segments the text costs, counted exactly the way the render counts it,
    /// so a consumer and this module can never disagree about one text. The
    /// answer depends on the alphabet: a single character outside the GSM
    /// tables switches the whole message to a two-byte encoding and can more
    /// than double the count.
    /// </summary>
    public static int CountSegments(string text) => SmsSegmentCount.Of(text);
}
