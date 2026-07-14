namespace Files.App.MacOS.Models;

public sealed record NetworkConnectionResult(IReadOnlyList<string> MountPaths, bool OpenedExternally);
