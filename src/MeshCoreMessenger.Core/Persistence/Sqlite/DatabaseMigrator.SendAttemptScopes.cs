namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal static partial class DatabaseMigrator
{
    private const string SendAttemptScopesSql = """
        CREATE TABLE SendAttemptScopes (
            AttemptId TEXT NOT NULL PRIMARY KEY REFERENCES SendAttempts(Id) ON DELETE CASCADE,
            NodeId TEXT NOT NULL REFERENCES Nodes(Id),
            SessionId TEXT NULL,
            Generation INTEGER NULL CHECK(Generation IS NULL OR Generation>=0),
            RequestedMode INTEGER NOT NULL CHECK(RequestedMode IN(0,1,2)),
            PolicyRevision INTEGER NOT NULL CHECK(PolicyRevision>=0),
            State INTEGER NOT NULL CHECK(State IN(0,1,2,3)),
            Key BLOB NULL,
            KeyFingerprint BLOB NULL,
            Name TEXT NULL CHECK(Name IS NULL OR (instr(Name,char(0))=0 AND length(CAST(Name AS BLOB))<=30)),
            MaximumTextBytes INTEGER NULL CHECK(MaximumTextBytes IS NULL OR MaximumTextBytes>=0),
            FOREIGN KEY(SessionId,NodeId) REFERENCES Sessions(Id,NodeId),
            CHECK((State=2 AND Key IS NOT NULL AND typeof(Key)='blob' AND length(Key)=16 AND Key<>zeroblob(16)
                AND KeyFingerprint IS NOT NULL AND typeof(KeyFingerprint)='blob' AND length(KeyFingerprint)=32)
                OR (State<>2 AND Key IS NULL AND KeyFingerprint IS NULL)),
            CHECK(State NOT IN(2,3) OR (SessionId IS NOT NULL AND Generation IS NOT NULL)),
            CHECK(NOT(RequestedMode=2 AND State=2) AND NOT(RequestedMode=1 AND State=3))
        );
        CREATE INDEX IX_SendAttemptScopes_Node ON SendAttemptScopes(NodeId);
        INSERT INTO SendAttemptScopes(AttemptId,NodeId,SessionId,RequestedMode,PolicyRevision,State)
            SELECT a.Id,c.NodeId,a.SessionId,0,0,0 FROM SendAttempts a
            JOIN Messages m ON m.Id=a.MessageId JOIN Conversations c ON c.Id=m.ConversationId;
        CREATE TRIGGER TR_SendAttemptScopes_Owner BEFORE INSERT ON SendAttemptScopes
        WHEN NOT EXISTS(SELECT 1 FROM SendAttempts a JOIN Messages m ON m.Id=a.MessageId
            JOIN Conversations c ON c.Id=m.ConversationId WHERE a.Id=NEW.AttemptId AND c.NodeId=NEW.NodeId
                AND a.SessionId IS NEW.SessionId)
        BEGIN SELECT RAISE(ABORT,'Attempt scope ownership mismatch'); END;
        CREATE TRIGGER TR_SendAttemptScopes_Immutable BEFORE UPDATE ON SendAttemptScopes
        BEGIN SELECT RAISE(ABORT,'Attempt scope is immutable'); END;
        """;
}
