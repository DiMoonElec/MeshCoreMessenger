namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal static partial class DatabaseMigrator
{
    private const string RegionDiscoverySql = """
        CREATE TABLE RegionDiscoveryOutcomes (
            RunId TEXT NOT NULL,
            NodeId TEXT NOT NULL REFERENCES Nodes(Id),
            SessionId TEXT NOT NULL,
            Generation INTEGER NOT NULL CHECK(Generation>=0),
            RepeaterPublicKey BLOB NOT NULL CHECK(typeof(RepeaterPublicKey)='blob' AND length(RepeaterPublicKey)=32),
            CompletedUtc TEXT NOT NULL,
            Status INTEGER NOT NULL CHECK(Status BETWEEN 0 AND 13),
            NamesJson TEXT NOT NULL CHECK(length(CAST(NamesJson AS BLOB))<=8192),
            Preparation INTEGER NOT NULL CHECK(Preparation BETWEEN 0 AND 6),
            Restoration INTEGER NOT NULL CHECK(Restoration BETWEEN 0 AND 5),
            ContactCreation INTEGER NOT NULL CHECK(ContactCreation BETWEEN 0 AND 3),
            ErrorCode TEXT NULL CHECK(ErrorCode IS NULL OR length(ErrorCode)<=128),
            PRIMARY KEY(RunId,RepeaterPublicKey),
            FOREIGN KEY(SessionId,NodeId) REFERENCES Sessions(Id,NodeId)
        );
        CREATE INDEX IX_RegionDiscoveryOutcomes_Node_Completed ON RegionDiscoveryOutcomes(NodeId,CompletedUtc DESC);
        """;
}
