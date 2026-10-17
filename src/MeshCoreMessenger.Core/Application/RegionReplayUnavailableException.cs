namespace MeshCoreMessenger.Core.Application;

public sealed class RegionReplayUnavailableException() : InvalidOperationException(
    "The original flood scope is unknown. Send as new to use current settings.");
