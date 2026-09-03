using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// Reads the mapping the module ships. The model answers about every column at
/// once, which is what a mutation test on one column cannot do: removing any
/// other column from the freeze call leaves such a test green.
/// </summary>
public sealed class AttachmentPersistenceModelTests
{
    [Fact]
    public void Every_recorded_generation_column_outside_the_key_refuses_a_later_write()
    {
        IEntityType generation = Model
            .FindEntityType(typeof(AttachmentObjectGeneration))
            .ShouldNotBeNull();

        var writable = generation.GetProperties()
            .Where(property => !property.IsPrimaryKey()
                && property.GetAfterSaveBehavior() != PropertySaveBehavior.Throw)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        writable.ShouldBeEmpty();

        // Naming the count keeps the assertion above from passing on an empty
        // entity type, which is the shape a renamed or unmapped type takes.
        generation.GetProperties().Count().ShouldBe(10);
    }

    [Fact]
    public void The_aggregate_table_carries_exactly_the_columns_it_was_registered_with()
    {
        IEntityType attachment = Model
            .FindEntityType(typeof(Attachment))
            .ShouldNotBeNull();

        var columns = attachment.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The proof of the bytes lives on the generation row and is never
        // copied here. A copy would be an update on a row that already exists,
        // which is the shape the freeze cannot cover, and it would put the
        // digest inside every projection of the aggregate. Freezing the whole
        // column set is what makes any addition fail, whatever it is named.
        //
        // Two columns were added to this set on purpose, and the reason each
        // one lives on the aggregate rather than on a row of its own is the
        // whole of the decision.
        //
        // "validation_detail" holds which check refused, or which verdict did
        // not conclude. The public answer is one word for the whole family of
        // content refusals, so the fine detail has to survive somewhere the
        // authorized query can read it, and it describes the attachment rather
        // than a set of bytes: a refused attachment has no release row to carry
        // it. It is a mutable column by construction, because a later verdict
        // overwrites it, which is exactly why it cannot sit on the append-only
        // release row.
        //
        // "inconclusive_until" holds the deadline of a verdict that did not
        // conclude. The tolerance was decided as a deadline and not as a count
        // of attempts, so it is one instant per attachment, written once and
        // moved by nothing. It sits here for the same reason: an attachment
        // that is waiting has not been released and has no row of its own.
        //
        // Neither column carries a storage coordinate, a content type or a
        // digest, so the rule that keeps the proof of the bytes off this table
        // is untouched by the addition.
        //
        // Taking a release back added nothing here, and that is a decision too.
        // A revocation is an act over a grant: it names the exact release it
        // withdrew, it carries an instant and a declared reason, and none of
        // those describe the attachment. Written here they would be three more
        // columns that are null for every attachment nobody revoked, and the
        // instant of the withdrawal would sit on a row this module revises,
        // which is the one shape the freeze on an append-only line exists to
        // prevent. What the state carries is the word "revoked", and the word
        // is a value of a column that already existed.
        columns.ShouldBe(
        [
            "application",
            "content_id",
            "content_type",
            "created_at",
            "file_name",
            "id",
            "inconclusive_until",
            "received_at",
            "reference",
            "size_bytes",
            "state",
            "validation_detail",
            "xmin",
        ]);
    }

    /// <summary>
    /// The measured constraint on naming a durable detail, read the same way
    /// the state names are read: from the mapping, never from a number written
    /// twice. A detail longer than the column would only fail when a real
    /// refusal tried to persist it.
    /// </summary>
    [Fact]
    public void Every_durable_validation_detail_fits_the_column_that_stores_it()
    {
        var details = typeof(AttachmentValidationDetails)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType: { } type }
                && type == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        // A walk that stopped finding details would make the assertion below
        // pass over nothing, and every detail added later would join it.
        details.Order(StringComparer.Ordinal).ShouldBe(
        [
            "content-not-inspectable",
            "content-type-divergent",
            "content-type-not-admitted",
            "inconclusive-window-elapsed",
        ]);

        var ceiling = Model
            .FindEntityType(typeof(Attachment))
            .ShouldNotBeNull()
            .GetProperty(nameof(Attachment.ValidationDetail))
            .GetMaxLength()
            .ShouldNotBeNull();

        details.ShouldAllBe(detail => detail.Length <= ceiling);
    }

    /// <summary>
    /// The measured constraint on naming a state. The column is what decides
    /// how long a name may be, so the rule is read from the mapping and never
    /// from a number written twice.
    /// </summary>
    [Fact]
    public void Every_state_name_fits_the_column_that_stores_it()
    {
        var names = typeof(AttachmentStates)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType: { } type }
                && type == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        // A walk that stopped finding names would make the assertion below
        // pass over nothing, and every state added later would join it.
        //
        // One name was added on purpose. "revoked" is the state a release that
        // was taken back leaves behind, and it is a word of its own rather than
        // a second spelling of "rejected" because the two are different events
        // for every reader: a refused attachment was never approved, and a
        // revoked one was approved and the approval was withdrawn. Folding them
        // would make the check performed before a message goes out unable to
        // tell "the content never passed" from "someone took it back", and it
        // would answer a producer that the content was refused when no check
        // ever refused it.
        names.Order(StringComparer.Ordinal).ShouldBe(
        [
            "awaiting-upload",
            "received",
            "rejected",
            "released",
            "revoked",
            "validation-inconclusive",
        ]);

        var ceiling = Model
            .FindEntityType(typeof(Attachment))
            .ShouldNotBeNull()
            .GetProperty(nameof(Attachment.State))
            .GetMaxLength()
            .ShouldNotBeNull();

        names.ShouldAllBe(name => name.Length <= ceiling);
    }

    [Fact]
    public void Every_release_column_outside_the_key_refuses_a_later_write()
    {
        IEntityType release = Model
            .FindEntityType(typeof(AttachmentRelease))
            .ShouldNotBeNull();

        var writable = release.GetProperties()
            .Where(property => !property.IsPrimaryKey()
                && property.GetAfterSaveBehavior() != PropertySaveBehavior.Throw)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        writable.ShouldBeEmpty();

        // The release is born complete, so every column it has is named here.
        // An addition that arrives without the freeze fails the assertion
        // above; an addition that arrives with it fails this one, and both
        // have to be a decision rather than a default.
        release.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(["attachment_id", "expires_at", "generation_id", "id", "released_at"]);
    }

    [Fact]
    public void Every_revocation_column_outside_the_key_refuses_a_later_write()
    {
        IEntityType revocation = Model
            .FindEntityType(typeof(AttachmentRevocation))
            .ShouldNotBeNull();

        var writable = revocation.GetProperties()
            .Where(property => !property.IsPrimaryKey()
                && property.GetAfterSaveBehavior() != PropertySaveBehavior.Throw)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        writable.ShouldBeEmpty();

        // Four columns were registered on purpose, and each one is here because
        // the alternative was worse.
        //
        // "attachment_id" is what any reading of one attachment's lifecycle
        // starts from, and the release it names is reachable only through it.
        //
        // "release_id" is the whole reason this is a row and not a state. The
        // release line can hold more than one grant, so a withdrawal that named
        // only the attachment would say that some approval was taken back
        // without saying which, and the day an explicit revalidation writes a
        // second release, that sentence stops having an answer.
        //
        // "reason" is the caller's declaration. Taking content back is always
        // permitted from the released state, so why it happened is the only
        // part of the act the module cannot derive, and a withdrawal nobody can
        // explain is the one an investigation cannot close.
        //
        // "revoked_at" is when it happened, and it is here rather than on the
        // aggregate because it dates this act and not the attachment. It is
        // also what makes the withdrawal impossible to move: the row is written
        // complete and frozen, so a repeat cannot quietly redate it.
        //
        // No column here carries a storage coordinate, a content identity, a
        // name, a declared type or a digest, so the rule that keeps the proof
        // of the bytes on the generation row alone is untouched.
        revocation.GetProperties()
            .Select(property => property.GetColumnName())
            .Order(StringComparer.Ordinal)
            .ToArray()
            .ShouldBe(["attachment_id", "id", "reason", "release_id", "revoked_at"]);
    }

    /// <summary>
    /// One release is taken back at most once, and the storage says so rather
    /// than only the state machine. The state is what the machine reads under a
    /// row lock; this index is what stands when two callers reach the act on two
    /// connections and both read a state that is about to stop being true.
    /// </summary>
    [Fact]
    public void One_release_carries_at_most_one_withdrawal()
    {
        IEntityType revocation = Model
            .FindEntityType(typeof(AttachmentRevocation))
            .ShouldNotBeNull();

        IIndex index = revocation
            .GetIndexes()
            .Single(candidate => candidate.Properties
                .Select(property => property.Name)
                .SequenceEqual([nameof(AttachmentRevocation.ReleaseId)]));

        index.IsUnique.ShouldBeTrue();
    }

    [Fact]
    public void No_table_outside_the_generation_row_maps_a_digest()
    {
        var carriers = Model.GetEntityTypes()
            .Where(entity => entity.ClrType != typeof(AttachmentObjectGeneration))
            .SelectMany(entity => entity.GetProperties()
                .Where(CarriesADigest)
                .Select(property => $"{entity.ShortName()}.{property.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        carriers.ShouldBeEmpty();
    }

    private static bool CarriesADigest(IProperty property)
        => property.ClrType == typeof(byte[])
            || property.Name.Contains("digest", StringComparison.OrdinalIgnoreCase)
            || property.GetColumnName().Contains("digest", StringComparison.OrdinalIgnoreCase);

    // The model is built from the mapping alone and never opens a connection,
    // so the address below only has to be well formed. It is built once,
    // because building it is the expensive part and every test here reads the
    // same answer.
    private static IModel Model { get; } = BuildModel();

    private static IModel BuildModel()
    {
        using var context = new AttachmentManagementDbContext(
            new DbContextOptionsBuilder<AttachmentManagementDbContext>()
                .UseNpgsql("Host=127.0.0.1;Database=attachment-model-only")
                .Options);
        return context.Model;
    }
}
