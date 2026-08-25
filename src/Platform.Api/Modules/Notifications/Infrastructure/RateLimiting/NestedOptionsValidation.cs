using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

/// <summary>
/// Runs the data annotations of a nested options object, which the outer
/// registration never reaches.
/// <para>
/// <c>ValidateDataAnnotations</c> validates the object it was registered for
/// and stops there: an attribute on the value of a dictionary entry or on an
/// item of a list is never evaluated. The failure is silent and it fails open,
/// which is the worst direction for a limit: the range reads as enforced in
/// the source, the host boots, and the out-of-range value reaches the control
/// at runtime.
/// </para>
/// <para>
/// The member path is prefixed onto every result so the message names the
/// entry an operator has to fix, not just the property.
/// </para>
/// <para>
/// Dispatch carries its own copy of this helper by design. Referencing that
/// one crosses a bounded-context boundary, and the shared kernel holds domain
/// vocabulary that stays free of technology, so host configuration plumbing
/// does not belong there either. The copy is stateless and carries no policy,
/// so the two cannot drift in behavior an operator would notice.
/// </para>
/// </summary>
internal static class NestedOptionsValidation
{
    internal static IEnumerable<ValidationResult> Validate(object nested, string memberPath)
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(
            nested,
            new ValidationContext(nested),
            results,
            validateAllProperties: true);

        foreach (ValidationResult result in results)
        {
            var members = result.MemberNames
                .Select(name => $"{memberPath}:{name}")
                .ToArray();
            yield return new ValidationResult(
                result.ErrorMessage,
                members.Length == 0 ? [memberPath] : members);
        }
    }
}
