namespace Files.App.MacOS.Models;

/// <summary>虚拟浏览位置：永远不能作为文件操作的目标目录。</summary>
internal static class ComputerLocation
{
	internal const string Path = "files://computer";
	internal static bool IsComputer(string? path) => string.Equals(path?.Trim().TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase);
}

public sealed record ComputerVolume(string Name, string Path, bool IsNetwork, long? TotalBytes, long? AvailableBytes)
{
	public bool HasCapacity => TotalBytes is > 0 && AvailableBytes.HasValue;
	public double UsedPercentage => HasCapacity ? Math.Clamp(100d * (TotalBytes!.Value - AvailableBytes!.Value) / TotalBytes.Value, 0, 100) : 0;
	public string CapacityText => HasCapacity
		? string.Format(Files.App.MacOS.Services.LiveLocalization.GetString("VolumeCapacityFormat") ?? "{0} free of {1}", LocalFileSystemItem.FormatSize(AvailableBytes!.Value), LocalFileSystemItem.FormatSize(TotalBytes!.Value))
		: Files.App.MacOS.Services.LiveLocalization.GetString("VolumeCapacityUnavailable") ?? "Capacity unavailable";
	public string AccessibleName => $"{Name}, {CapacityText}";
}

public sealed record ComputerFolder(string Name, string Path, string Glyph = "★");
