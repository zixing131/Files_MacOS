using CommunityToolkit.Mvvm.ComponentModel;

namespace Files.App.MacOS.Models;

public sealed partial class GridItemSizeState : ObservableObject
{
	public const int MinimumLevel = 0;
	public const int MaximumLevel = 3;
	public const int DefaultLevel = 1;

	private static readonly (double ItemWidth, double ItemHeight, double IconSize, double GlyphSize)[] Levels =
	[
		(88, 84, 36, 24),
		(116, 108, 64, 44),
		(148, 140, 96, 64),
		(192, 180, 128, 88),
	];

	[ObservableProperty]
	public partial double ItemWidth { get; set; } = 116;

	[ObservableProperty]
	public partial double ItemHeight { get; set; } = 108;

	[ObservableProperty]
	public partial double IconSize { get; set; } = 64;

	[ObservableProperty]
	public partial double GlyphSize { get; set; } = 44;

	[ObservableProperty]
	public partial int Level { get; set; } = DefaultLevel;

	public void Apply(int level)
	{
		int clamped = Math.Clamp(level, MinimumLevel, MaximumLevel);
		(double itemWidth, double itemHeight, double iconSize, double glyphSize) = Levels[clamped];
		ItemWidth = itemWidth;
		ItemHeight = itemHeight;
		IconSize = iconSize;
		GlyphSize = glyphSize;
		Level = clamped;
	}
}
