using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal static class DatabaseMigrator
{
    public const int CurrentVersion = 3;

    private static readonly Migration[] Migrations =
    [
        new(1, "Initial local storage", InitialSchemaSql),
        new(2, "Incoming message metadata", IncomingMessageMetadataSql),
        new(3, "Durable outgoing messages", OutgoingMessagesSql),
    ];

    public static void ApplyPending(SqliteConnection connection, int targetVersion = CurrentVersion)
    {
        var currentVersion = SqliteDatabase.GetUserVersion(connection);
        if (currentVersion > CurrentVersion)
        {
            throw new DatabaseVersionTooNewException(currentVersion, CurrentVersion);
        }

        foreach (var migration in Migrations.Where(item => item.Version > currentVersion && item.Version <= targetVersion))
        {
            Apply(connection, migration);
            currentVersion = migration.Version;
        }
    }

    private static void Apply(SqliteConnection connection, Migration migration)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = migration.Sql;
                schema.ExecuteNonQuery();
            }

            using (var history = connection.CreateCommand())
            {
                history.Transaction = transaction;
                history.CommandText = "INSERT INTO SchemaMigrations (Version, Name, AppliedUtc) VALUES ($version, $name, $appliedUtc);";
                history.Parameters.AddWithValue("$version", migration.Version);
                history.Parameters.AddWithValue("$name", migration.Name);
                history.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
                history.ExecuteNonQuery();
            }

            using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = $"PRAGMA user_version={migration.Version};";
                version.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (Exception exception)
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
                // Preserve the original migration error. SQLite will also roll back on dispose.
            }

            throw new DatabaseStorageException(
                $"Database migration {migration.Version} ('{migration.Name}') failed. The existing database was preserved.",
                exception);
        }
    }

    private sealed record Migration(int Version, string Name, string Sql);

    private const string InitialSchemaSql = """
        CREATE TABLE SchemaMigrations (
            Version INTEGER NOT NULL PRIMARY KEY CHECK (Version > 0),
            Name TEXT NOT NULL UNIQUE,
            AppliedUtc TEXT NOT NULL
        );

        CREATE TABLE Settings (
            Key TEXT NOT NULL PRIMARY KEY CHECK (length(Key) > 0),
            Value TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        );

        CREATE TABLE ConnectionProfiles (
            Id TEXT NOT NULL PRIMARY KEY,
            Name TEXT NOT NULL CHECK (length(Name) > 0),
            Transport INTEGER NOT NULL CHECK (Transport IN (0, 1)),
            TcpHost TEXT,
            TcpPort INTEGER CHECK (TcpPort BETWEEN 1 AND 65535),
            SerialPortName TEXT,
            BaudRate INTEGER CHECK (BaudRate > 0),
            DtrEnable INTEGER NOT NULL CHECK (DtrEnable IN (0, 1)),
            RtsEnable INTEGER NOT NULL CHECK (RtsEnable IN (0, 1)),
            OpenDelayMilliseconds INTEGER NOT NULL CHECK (OpenDelayMilliseconds >= 0),
            CommandTimeoutMilliseconds INTEGER NOT NULL CHECK (CommandTimeoutMilliseconds > 0),
            AcknowledgementTimeoutMilliseconds INTEGER NOT NULL CHECK (AcknowledgementTimeoutMilliseconds > 0),
            AutoConnect INTEGER NOT NULL CHECK (AutoConnect IN (0, 1)),
            Reconnect INTEGER NOT NULL CHECK (Reconnect IN (0, 1)),
            ExpectedNodePublicKey BLOB CHECK (ExpectedNodePublicKey IS NULL OR length(ExpectedNodePublicKey) = 32),
            CreatedUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            CHECK (
                (Transport = 0 AND TcpHost IS NOT NULL AND length(TcpHost) > 0 AND TcpPort IS NOT NULL AND SerialPortName IS NULL AND BaudRate IS NULL)
                OR
                (Transport = 1 AND SerialPortName IS NOT NULL AND length(SerialPortName) > 0 AND BaudRate IS NOT NULL AND TcpHost IS NULL AND TcpPort IS NULL)
            )
        );

        CREATE TABLE Nodes (
            Id TEXT NOT NULL PRIMARY KEY,
            PublicKey BLOB NOT NULL UNIQUE CHECK (length(PublicKey) = 32),
            LastName TEXT,
            FirstSeenUtc TEXT NOT NULL,
            LastSeenUtc TEXT NOT NULL
        );

        CREATE TABLE Sessions (
            Id TEXT NOT NULL PRIMARY KEY,
            ConnectionProfileId TEXT NOT NULL,
            NodeId TEXT,
            StartedUtc TEXT NOT NULL,
            EndedUtc TEXT,
            EndReason TEXT,
            FOREIGN KEY (ConnectionProfileId) REFERENCES ConnectionProfiles(Id) ON DELETE RESTRICT,
            FOREIGN KEY (NodeId) REFERENCES Nodes(Id) ON DELETE RESTRICT,
            CHECK (EndedUtc IS NULL OR EndedUtc >= StartedUtc)
        );
        CREATE INDEX IX_Sessions_Profile_StartedUtc ON Sessions(ConnectionProfileId, StartedUtc);

        CREATE TABLE Contacts (
            NodeId TEXT NOT NULL,
            PublicKey BLOB NOT NULL CHECK (length(PublicKey) = 32),
            PublicKeyPrefix BLOB NOT NULL CHECK (length(PublicKeyPrefix) = 6),
            DisplayName TEXT NOT NULL,
            ContactType INTEGER NOT NULL,
            Flags INTEGER NOT NULL,
            OutPath BLOB CHECK (OutPath IS NULL OR length(OutPath) <= 64),
            AdvertPayload BLOB,
            PresentOnNode INTEGER NOT NULL CHECK (PresentOnNode IN (0, 1)),
            LastAdvertUtc TEXT,
            Latitude REAL,
            Longitude REAL,
            UpdatedUtc TEXT NOT NULL,
            PRIMARY KEY (NodeId, PublicKey),
            FOREIGN KEY (NodeId) REFERENCES Nodes(Id) ON DELETE CASCADE
        );
        CREATE INDEX IX_Contacts_Node_Prefix ON Contacts(NodeId, PublicKeyPrefix);

        CREATE TABLE Channels (
            Id TEXT NOT NULL PRIMARY KEY,
            NodeId TEXT NOT NULL,
            LastName TEXT NOT NULL,
            KeyFingerprint BLOB NOT NULL CHECK (length(KeyFingerprint) = 32),
            AccessKind INTEGER NOT NULL CHECK (AccessKind IN (0, 1, 2)),
            CreatedUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            FOREIGN KEY (NodeId) REFERENCES Nodes(Id) ON DELETE CASCADE,
            UNIQUE (NodeId, KeyFingerprint),
            UNIQUE (Id, NodeId)
        );

        CREATE TABLE ChannelBindings (
            Id TEXT NOT NULL PRIMARY KEY,
            NodeId TEXT NOT NULL,
            Slot INTEGER NOT NULL CHECK (Slot BETWEEN 0 AND 255),
            Generation INTEGER NOT NULL CHECK (Generation > 0),
            ChannelId TEXT NOT NULL,
            BoundUtc TEXT NOT NULL,
            UnboundUtc TEXT,
            ObservedSessionId TEXT,
            FOREIGN KEY (NodeId) REFERENCES Nodes(Id) ON DELETE CASCADE,
            FOREIGN KEY (ChannelId, NodeId) REFERENCES Channels(Id, NodeId) ON DELETE CASCADE,
            FOREIGN KEY (ObservedSessionId) REFERENCES Sessions(Id) ON DELETE SET NULL,
            UNIQUE (NodeId, Slot, Generation),
            CHECK (UnboundUtc IS NULL OR UnboundUtc >= BoundUtc)
        );
        CREATE UNIQUE INDEX UX_ChannelBindings_ActiveSlot ON ChannelBindings(NodeId, Slot) WHERE UnboundUtc IS NULL;

        CREATE TABLE Conversations (
            Id TEXT NOT NULL PRIMARY KEY,
            NodeId TEXT NOT NULL,
            Kind INTEGER NOT NULL CHECK (Kind IN (0, 1, 2, 3)),
            ContactPublicKey BLOB CHECK (ContactPublicKey IS NULL OR length(ContactPublicKey) = 32),
            ChannelId TEXT,
            UnknownIdentity BLOB,
            Title TEXT,
            IsArchived INTEGER NOT NULL CHECK (IsArchived IN (0, 1)),
            LastReadSequence INTEGER NOT NULL DEFAULT 0 CHECK (LastReadSequence >= 0),
            CreatedUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            FOREIGN KEY (NodeId) REFERENCES Nodes(Id) ON DELETE CASCADE,
            FOREIGN KEY (NodeId, ContactPublicKey) REFERENCES Contacts(NodeId, PublicKey) ON DELETE RESTRICT,
            FOREIGN KEY (ChannelId, NodeId) REFERENCES Channels(Id, NodeId) ON DELETE RESTRICT,
            CHECK (
                (Kind = 0 AND ContactPublicKey IS NOT NULL AND ChannelId IS NULL AND UnknownIdentity IS NULL)
                OR (Kind = 1 AND ContactPublicKey IS NULL AND ChannelId IS NOT NULL AND UnknownIdentity IS NULL)
                OR (Kind = 2 AND ContactPublicKey IS NULL AND ChannelId IS NULL AND UnknownIdentity IS NOT NULL)
                OR (Kind = 3 AND ContactPublicKey IS NULL AND ChannelId IS NULL AND UnknownIdentity IS NOT NULL)
            )
        );
        CREATE UNIQUE INDEX UX_Conversations_Contact ON Conversations(NodeId, ContactPublicKey) WHERE Kind = 0;
        CREATE UNIQUE INDEX UX_Conversations_Channel ON Conversations(NodeId, ChannelId) WHERE Kind = 1;
        CREATE UNIQUE INDEX UX_Conversations_Unknown ON Conversations(NodeId, Kind, UnknownIdentity) WHERE Kind IN (2, 3);

        CREATE TABLE Messages (
            LocalSequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            Id TEXT NOT NULL UNIQUE,
            EventId TEXT UNIQUE,
            ConversationId TEXT NOT NULL,
            SessionId TEXT,
            Direction INTEGER NOT NULL CHECK (Direction IN (0, 1)),
            MessageKind INTEGER NOT NULL CHECK (MessageKind IN (0, 1)),
            Text TEXT,
            Payload BLOB,
            WirePayload BLOB,
            ReceivedUtc TEXT NOT NULL,
            WireTimestamp INTEGER,
            OriginalPublicKeyPrefix BLOB CHECK (OriginalPublicKeyPrefix IS NULL OR length(OriginalPublicKeyPrefix) = 6),
            OriginalChannelSlot INTEGER CHECK (OriginalChannelSlot IS NULL OR OriginalChannelSlot BETWEEN 0 AND 255),
            ChannelBindingId TEXT,
            ResolutionState INTEGER NOT NULL DEFAULT 0 CHECK (ResolutionState IN (0, 1, 2)),
            Snr REAL,
            Path BLOB,
            FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE,
            FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE SET NULL,
            FOREIGN KEY (ChannelBindingId) REFERENCES ChannelBindings(Id) ON DELETE RESTRICT,
            CHECK (Text IS NOT NULL OR Payload IS NOT NULL OR WirePayload IS NOT NULL)
        );
        CREATE INDEX IX_Messages_Conversation_Sequence ON Messages(ConversationId, LocalSequence);

        CREATE TABLE SendAttempts (
            Id TEXT NOT NULL PRIMARY KEY,
            MessageId TEXT NOT NULL,
            SessionId TEXT,
            AttemptNumber INTEGER NOT NULL CHECK (AttemptNumber > 0),
            State INTEGER NOT NULL CHECK (State BETWEEN 0 AND 5),
            StartedUtc TEXT NOT NULL,
            AcceptedUtc TEXT,
            CompletedUtc TEXT,
            WireTimestamp INTEGER,
            ExpectedAck BLOB,
            RoundTripMilliseconds INTEGER CHECK (RoundTripMilliseconds IS NULL OR RoundTripMilliseconds >= 0),
            ErrorCode TEXT,
            FOREIGN KEY (MessageId) REFERENCES Messages(Id) ON DELETE CASCADE,
            FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE SET NULL,
            UNIQUE (MessageId, AttemptNumber),
            CHECK (AcceptedUtc IS NULL OR AcceptedUtc >= StartedUtc),
            CHECK (CompletedUtc IS NULL OR CompletedUtc >= StartedUtc)
        );
        CREATE INDEX IX_SendAttempts_Message_StartedUtc ON SendAttempts(MessageId, StartedUtc);

        CREATE TABLE Drafts (
            ConversationId TEXT NOT NULL PRIMARY KEY,
            Text TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE
        );
        """;

    private const string OutgoingMessagesSql = """
        ALTER TABLE Messages ADD COLUMN TransmissionText TEXT;
        CREATE TABLE SendAttempts_v3 (
            Id TEXT NOT NULL PRIMARY KEY,
            MessageId TEXT NOT NULL,
            SessionId TEXT,
            AttemptNumber INTEGER NOT NULL CHECK (AttemptNumber > 0),
            State INTEGER NOT NULL CHECK (State BETWEEN 0 AND 6),
            StartedUtc TEXT NOT NULL,
            AcceptedUtc TEXT,
            CompletedUtc TEXT,
            WireTimestamp INTEGER,
            ExpectedAck BLOB,
            RoundTripMilliseconds INTEGER CHECK (RoundTripMilliseconds IS NULL OR RoundTripMilliseconds >= 0),
            ErrorCode TEXT,
            AckExpectation INTEGER NOT NULL DEFAULT 0 CHECK (AckExpectation BETWEEN 0 AND 2),
            FOREIGN KEY (MessageId) REFERENCES Messages(Id) ON DELETE CASCADE,
            FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE SET NULL,
            UNIQUE (MessageId, AttemptNumber),
            CHECK (AcceptedUtc IS NULL OR AcceptedUtc >= StartedUtc),
            CHECK (CompletedUtc IS NULL OR CompletedUtc >= StartedUtc)
        );
        INSERT INTO SendAttempts_v3
            (Id, MessageId, SessionId, AttemptNumber, State, StartedUtc, AcceptedUtc,
             CompletedUtc, WireTimestamp, ExpectedAck, RoundTripMilliseconds, ErrorCode)
        SELECT Id, MessageId, SessionId, AttemptNumber, State, StartedUtc, AcceptedUtc,
               CompletedUtc, WireTimestamp, ExpectedAck, RoundTripMilliseconds, ErrorCode
        FROM SendAttempts;
        DROP TABLE SendAttempts;
        ALTER TABLE SendAttempts_v3 RENAME TO SendAttempts;
        CREATE INDEX IX_SendAttempts_Message_StartedUtc ON SendAttempts(MessageId, StartedUtc);
        """;

    private const string IncomingMessageMetadataSql = """
        ALTER TABLE Messages ADD COLUMN TextType INTEGER;
        ALTER TABLE Messages ADD COLUMN PathLength INTEGER CHECK (PathLength BETWEEN 0 AND 255);
        ALTER TABLE Messages ADD COLUMN BinaryDataType INTEGER CHECK (BinaryDataType BETWEEN 0 AND 65535);
        ALTER TABLE Messages ADD COLUMN OriginalSenderPrefix BLOB CHECK (OriginalSenderPrefix IS NULL OR length(OriginalSenderPrefix) = 4);
        """;
}
