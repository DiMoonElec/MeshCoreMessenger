using Microsoft.Data.Sqlite;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal static class DatabaseMigrator
{
    public const int CurrentVersion = 5;

    private static readonly Migration[] Migrations =
    [
        new(1, "Initial local storage", InitialSchemaSql),
        new(2, "Incoming message metadata", IncomingMessageMetadataSql),
        new(3, "Durable outgoing messages", OutgoingMessagesSql),
        new(4, "Contact route descriptors", ContactRoutesSql),
        new(5, "Private delivery cycles and route evidence", PrivateDeliverySql),
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

    private const string ContactRoutesSql = """
        ALTER TABLE Contacts ADD COLUMN OutPathLength INTEGER CHECK (OutPathLength IS NULL OR OutPathLength BETWEEN 0 AND 255);
        ALTER TABLE Contacts ADD COLUMN RouteObservedUtc TEXT;
        """;

    private const string PrivateDeliverySql = """
        CREATE TABLE PrivateTimestampFloors (
            NodeId TEXT NOT NULL PRIMARY KEY REFERENCES Nodes(Id) ON DELETE CASCADE,
            LastTimestamp INTEGER NOT NULL CHECK (LastTimestamp BETWEEN 0 AND 4294967295)
        );
        INSERT INTO PrivateTimestampFloors (NodeId,LastTimestamp)
        SELECT c.NodeId, MAX(a.WireTimestamp) FROM SendAttempts a JOIN Messages m ON m.Id=a.MessageId
        JOIN Conversations c ON c.Id=m.ConversationId
        WHERE c.Kind=0 AND m.Direction=1 AND a.WireTimestamp BETWEEN 0 AND 4294967295
        GROUP BY c.NodeId;

        CREATE TABLE PrivateDeliveryCycles (
            MessageId TEXT NOT NULL PRIMARY KEY REFERENCES Messages(Id) ON DELETE CASCADE,
            NodeId TEXT NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
            SessionId TEXT REFERENCES Sessions(Id) ON DELETE SET NULL,
            ContactPublicKey BLOB NOT NULL CHECK (length(ContactPublicKey)=32),
            RetryMode INTEGER NOT NULL CHECK (RetryMode IN (0,1)),
            InitialRouteKind INTEGER NOT NULL CHECK (InitialRouteKind BETWEEN 0 AND 2),
            PlannedAttemptCount INTEGER NOT NULL CHECK (PlannedAttemptCount IN (3,5)),
            PreparedAttemptCount INTEGER NOT NULL DEFAULT 0 CHECK (PreparedAttemptCount BETWEEN 0 AND PlannedAttemptCount),
            CurrentPhase INTEGER CHECK (CurrentPhase BETWEEN 0 AND 2),
            State INTEGER NOT NULL DEFAULT 0 CHECK (State BETWEEN 0 AND 5),
            StartedUtc TEXT NOT NULL,
            ConfirmedUtc TEXT,
            ErrorCode TEXT,
            PolicyVersion INTEGER NOT NULL DEFAULT 1 CHECK (PolicyVersion=1),
            CHECK ((InitialRouteKind=0 AND PlannedAttemptCount=3) OR (InitialRouteKind<>0 AND PlannedAttemptCount=5))
        );
        CREATE INDEX IX_PrivateDeliveryCycles_Node_State ON PrivateDeliveryCycles(NodeId,State);

        CREATE TABLE PrivateWireMessages (
            Id TEXT NOT NULL PRIMARY KEY,
            MessageId TEXT NOT NULL REFERENCES PrivateDeliveryCycles(MessageId) ON DELETE CASCADE,
            Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 1 AND 5),
            Phase INTEGER NOT NULL CHECK (Phase BETWEEN 0 AND 2),
            WireTimestamp INTEGER NOT NULL CHECK (WireTimestamp BETWEEN 0 AND 4294967295),
            UNIQUE (MessageId,Ordinal)
        );
        CREATE TRIGGER TR_PrivateWireMessages_Immutable BEFORE UPDATE ON PrivateWireMessages
        BEGIN SELECT RAISE(ABORT,'Private wire identity is immutable'); END;

        ALTER TABLE SendAttempts ADD COLUMN PrivatePreparationId TEXT;
        ALTER TABLE SendAttempts ADD COLUMN WireMessageId TEXT REFERENCES PrivateWireMessages(Id) ON DELETE CASCADE;
        ALTER TABLE SendAttempts ADD COLUMN WireAttempt INTEGER CHECK (WireAttempt BETWEEN 0 AND 3);
        ALTER TABLE SendAttempts ADD COLUMN RouteDescriptor INTEGER CHECK (RouteDescriptor BETWEEN 0 AND 255);
        ALTER TABLE SendAttempts ADD COLUMN RoutePath BLOB CHECK (length(RoutePath)<=64);
        ALTER TABLE SendAttempts ADD COLUMN RouteObservedUtc TEXT;
        ALTER TABLE SendAttempts ADD COLUMN PcPreparedUtc TEXT;
        ALTER TABLE SendAttempts ADD COLUMN PcUtcOffsetMinutes INTEGER CHECK (PcUtcOffsetMinutes BETWEEN -840 AND 840);
        ALTER TABLE SendAttempts ADD COLUMN PcTimeZoneId TEXT;
        ALTER TABLE SendAttempts ADD COLUMN PcSentUtc TEXT;
        ALTER TABLE SendAttempts ADD COLUMN PcSentUtcOffsetMinutes INTEGER CHECK (PcSentUtcOffsetMinutes BETWEEN -840 AND 840);
        ALTER TABLE SendAttempts ADD COLUMN PcSentTimeZoneId TEXT;
        ALTER TABLE SendAttempts ADD COLUMN ModeReportedByMsgSent INTEGER CHECK (ModeReportedByMsgSent IN (0,1));
        ALTER TABLE SendAttempts ADD COLUMN AckDeadlineUtc TEXT;
        CREATE UNIQUE INDEX UX_SendAttempts_PrivatePreparation ON SendAttempts(PrivatePreparationId) WHERE PrivatePreparationId IS NOT NULL;
        CREATE INDEX IX_SendAttempts_PrivateAck ON SendAttempts(SessionId,ExpectedAck) WHERE WireMessageId IS NOT NULL;
        CREATE TRIGGER TR_SendAttempts_PrivateCaptureImmutable BEFORE UPDATE ON SendAttempts
        WHEN OLD.WireMessageId IS NOT NULL AND (
            NEW.WireMessageId IS NOT OLD.WireMessageId OR NEW.WireAttempt IS NOT OLD.WireAttempt OR
            NEW.WireTimestamp IS NOT OLD.WireTimestamp OR NEW.PrivatePreparationId IS NOT OLD.PrivatePreparationId OR
            NEW.RouteDescriptor IS NOT OLD.RouteDescriptor OR NEW.RoutePath IS NOT OLD.RoutePath OR
            NEW.RouteObservedUtc IS NOT OLD.RouteObservedUtc OR NEW.PcPreparedUtc IS NOT OLD.PcPreparedUtc OR
            NEW.PcUtcOffsetMinutes IS NOT OLD.PcUtcOffsetMinutes OR NEW.PcTimeZoneId IS NOT OLD.PcTimeZoneId)
        BEGIN SELECT RAISE(ABORT,'Private attempt capture is immutable'); END;

        CREATE TABLE ContactDeliveryHistory (
            Id TEXT NOT NULL PRIMARY KEY,
            NodeId TEXT NOT NULL REFERENCES Nodes(Id) ON DELETE CASCADE,
            ContactPublicKey BLOB NOT NULL CHECK (length(ContactPublicKey)=32),
            MessageId TEXT UNIQUE REFERENCES Messages(Id) ON DELETE SET NULL,
            SessionId TEXT REFERENCES Sessions(Id) ON DELETE SET NULL,
            FirstAckReceivedUtc TEXT NOT NULL,
            PcUtcOffsetMinutes INTEGER NOT NULL CHECK (PcUtcOffsetMinutes BETWEEN -840 AND 840),
            PcTimeZoneId TEXT NOT NULL CHECK (length(PcTimeZoneId)>0),
            WasLate INTEGER NOT NULL CHECK (WasLate IN (0,1)),
            Attribution INTEGER NOT NULL CHECK (Attribution BETWEEN 0 AND 2)
        );
        CREATE INDEX IX_ContactDeliveryHistory_Contact_Time ON ContactDeliveryHistory(NodeId,ContactPublicKey,FirstAckReceivedUtc,Id);
        CREATE TABLE ContactDeliveryEvidence (
            Id TEXT NOT NULL PRIMARY KEY,
            DeliveryId TEXT NOT NULL REFERENCES ContactDeliveryHistory(Id) ON DELETE CASCADE,
            AckTag BLOB NOT NULL CHECK (length(AckTag)=4),
            AckReceivedUtc TEXT NOT NULL,
            RoundTripMilliseconds INTEGER CHECK (RoundTripMilliseconds BETWEEN 0 AND 4294967295),
            Attribution INTEGER NOT NULL CHECK (Attribution BETWEEN 0 AND 2),
            LearnedRouteDescriptor INTEGER CHECK (LearnedRouteDescriptor BETWEEN 0 AND 255),
            LearnedRoutePath BLOB CHECK (length(LearnedRoutePath)<=64),
            LearnedRouteObservedUtc TEXT,
            UNIQUE (DeliveryId,AckTag)
        );
        CREATE TABLE ContactDeliveryCandidates (
            EvidenceId TEXT NOT NULL REFERENCES ContactDeliveryEvidence(Id) ON DELETE CASCADE,
            AttemptNumber INTEGER NOT NULL CHECK (AttemptNumber>0),
            AttemptId TEXT REFERENCES SendAttempts(Id) ON DELETE SET NULL,
            WireMessageOrdinal INTEGER NOT NULL CHECK (WireMessageOrdinal BETWEEN 1 AND 5),
            WireTimestamp INTEGER NOT NULL CHECK (WireTimestamp BETWEEN 0 AND 4294967295),
            WireAttempt INTEGER NOT NULL CHECK (WireAttempt BETWEEN 0 AND 3),
            Phase INTEGER NOT NULL CHECK (Phase BETWEEN 0 AND 2),
            SentUtc TEXT,
            PcUtcOffsetMinutes INTEGER CHECK (PcUtcOffsetMinutes BETWEEN -840 AND 840),
            PcTimeZoneId TEXT,
            RouteDescriptor INTEGER CHECK (RouteDescriptor BETWEEN 0 AND 255),
            RoutePath BLOB CHECK (length(RoutePath)<=64),
            RouteObservedUtc TEXT,
            ModeReportedByMsgSent INTEGER CHECK (ModeReportedByMsgSent IN (0,1)),
            PRIMARY KEY (EvidenceId,AttemptNumber)
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
