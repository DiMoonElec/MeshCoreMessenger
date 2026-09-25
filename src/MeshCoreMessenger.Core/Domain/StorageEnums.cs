namespace MeshCoreMessenger.Core.Domain;

public enum ChannelAccessKind
{
    Unknown = 0,
    PublicOrHashtag = 1,
    SharedSecret = 2,
}

public enum ConversationKind
{
    Contact = 0,
    Channel = 1,
    UnknownContact = 2,
    UnknownChannel = 3,
}

public enum MessageDirection
{
    Incoming = 0,
    Outgoing = 1,
}

public enum StoredMessageKind
{
    Text = 0,
    Binary = 1,
}

public enum MessageResolutionState
{
    Unresolved = 0,
    Resolved = 1,
    Ambiguous = 2,
}

public enum SendAttemptState
{
    Prepared = 0,
    Accepted = 1,
    Delivered = 2,
    Unconfirmed = 3,
    Failed = 4,
    Unknown = 5,
}
