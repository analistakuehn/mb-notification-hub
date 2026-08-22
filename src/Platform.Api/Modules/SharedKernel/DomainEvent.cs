namespace NotificationHub.SharedKernel;

public abstract record DomainEvent(DateTimeOffset OccurredAt);
