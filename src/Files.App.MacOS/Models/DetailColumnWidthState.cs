using CommunityToolkit.Mvvm.ComponentModel;

namespace Files.App.MacOS.Models;

public sealed partial class DetailColumnWidthState : ObservableObject
{
	private static readonly (string Column, double DefaultWidth)[] Defaults =
	[
		("Modified", 150), ("Created", 150), ("LastOpened", 160), ("Added", 150), ("Size", 100),
		("Kind", 130), ("Version", 100), ("Comments", 160), ("Tags", 140),
	];

	[ObservableProperty]
	public partial double Modified { get; set; } = 150;

	[ObservableProperty]
	public partial double Created { get; set; } = 150;

	[ObservableProperty]
	public partial double LastOpened { get; set; } = 160;

	[ObservableProperty]
	public partial double Added { get; set; } = 150;

	[ObservableProperty]
	public partial double Size { get; set; } = 100;

	[ObservableProperty]
	public partial double Kind { get; set; } = 130;

	[ObservableProperty]
	public partial double Version { get; set; } = 100;

	[ObservableProperty]
	public partial double Comments { get; set; } = 160;

	[ObservableProperty]
	public partial double Tags { get; set; } = 140;

	public double GetWidth(string column) => column switch
	{
		"Modified" => Modified,
		"Created" => Created,
		"LastOpened" => LastOpened,
		"Added" => Added,
		"Size" => Size,
		"Kind" => Kind,
		"Version" => Version,
		"Comments" => Comments,
		"Tags" => Tags,
		_ => 0,
	};

	public void SetWidth(string column, double width)
	{
		switch (column)
		{
			case "Modified": Modified = width; break;
			case "Created": Created = width; break;
			case "LastOpened": LastOpened = width; break;
			case "Added": Added = width; break;
			case "Size": Size = width; break;
			case "Kind": Kind = width; break;
			case "Version": Version = width; break;
			case "Comments": Comments = width; break;
			case "Tags": Tags = width; break;
		}
	}

	public void Apply(IEnumerable<DetailColumnWidthSetting>? widths)
	{
		var configuredWidths = (widths ?? [])
			.GroupBy(static item => item.Column, StringComparer.Ordinal)
			.ToDictionary(static group => group.Key, static group => group.Last().Width, StringComparer.Ordinal);
		foreach ((string column, double defaultWidth) in Defaults)
		{
			SetWidth(column, Math.Clamp(configuredWidths.GetValueOrDefault(column, defaultWidth), 72, 480));
		}
	}

	public DetailColumnWidthSetting[] Capture() => Defaults
		.Select(item => new DetailColumnWidthSetting(item.Column, GetWidth(item.Column)))
		.ToArray();
}
