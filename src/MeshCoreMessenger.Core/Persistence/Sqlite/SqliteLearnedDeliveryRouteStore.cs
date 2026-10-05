using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed partial class SqliteOutgoingMessageStore
{
    public Task<bool> EnrichDeliveryRouteAsync(LearnedDeliveryRoute observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var target = observation.Target with { ContactPublicKey = observation.Target.ContactPublicKey.ToArray() };
        if (target.EvidenceId == Guid.Empty || target.NodeId == Guid.Empty || target.SessionId == Guid.Empty || target.ContactPublicKey.Length != 32)
            throw new ArgumentException("Complete delivery evidence ownership is required.", nameof(observation));
        var route = new PrivateRouteSnapshot(observation.Route.Descriptor, observation.Route.Path, observation.Route.ObservedUtc);
        return writer.ExecuteAsync(connection =>
        {
            using var command = Command(connection, null, """
                UPDATE ContactDeliveryEvidence SET LearnedRouteDescriptor=$descriptor,LearnedRoutePath=$path,LearnedRouteObservedUtc=$utc
                WHERE Id=$evidence AND LearnedRouteObservedUtc IS NULL AND DeliveryId IN (
                    SELECT Id FROM ContactDeliveryHistory WHERE NodeId=$node AND SessionId=$session AND ContactPublicKey=$key);
                """, ("$descriptor", route.Descriptor), ("$path", route.Path.ToArray()), ("$utc", route.ObservedUtc),
                ("$evidence", target.EvidenceId), ("$node", target.NodeId), ("$session", target.SessionId), ("$key", target.ContactPublicKey.ToArray()));
            return command.ExecuteNonQuery() == 1;
        }, cancellationToken);
    }
}
