namespace Flash.Shared.Messaging;

public enum FailureAction { Retry, DeadLetter }

/// <summary>
/// What a consumer does with a message whose handler threw. A poison message is dead-lettered at once (its rejection is
/// already committed); anything else is retried until it has been tried MaxAttempts times.
/// </summary>
public static class ConsumerRetryPolicy
{
    public static FailureAction Decide(int priorAttempts, Exception error) =>
        error is PoisonMessageException ? FailureAction.DeadLetter
        : priorAttempts + 1 >= Topology.MaxAttempts ? FailureAction.DeadLetter
        : FailureAction.Retry;
}
