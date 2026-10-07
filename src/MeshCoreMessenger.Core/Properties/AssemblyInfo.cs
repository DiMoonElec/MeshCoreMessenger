using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshCoreMessenger.Core.Tests")]

// Desktop policy tests control in-memory reception boundaries without expanding the public API.
[assembly: InternalsVisibleTo("MeshCoreMessenger.Desktop.Tests")]
