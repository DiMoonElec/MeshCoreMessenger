namespace MeshCoreMessenger.Core.Domain;

internal static class SendAttemptTransitions
{
    public static bool Allows(SendAttemptState current, AckExpectation expectation, SendAttemptState next) => current switch
    {
        SendAttemptState.Prepared => next is SendAttemptState.Sending or SendAttemptState.Failed,
        SendAttemptState.Sending => next is SendAttemptState.Accepted or SendAttemptState.Failed or SendAttemptState.Unknown,
        SendAttemptState.Accepted when expectation == AckExpectation.Expected => next is SendAttemptState.Delivered or SendAttemptState.Unconfirmed or SendAttemptState.Unknown,
        _ => false,
    };
}
