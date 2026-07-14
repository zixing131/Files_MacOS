namespace Files.App.MacOS.Models;

internal sealed record PathBreadcrumbItem(string Title, string Path, bool IsHome = false);
