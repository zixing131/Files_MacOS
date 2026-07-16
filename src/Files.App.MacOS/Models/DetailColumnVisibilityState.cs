using CommunityToolkit.Mvvm.ComponentModel;

namespace Files.App.MacOS.Models;

public sealed partial class DetailColumnVisibilityState : ObservableObject
{
	private static readonly string[] SupportedColumns =
	[
		"Modified", "Created", "LastOpened", "Added", "Size", "Kind", "Version", "Comments", "Tags",
	];

	[ObservableProperty]
	public partial bool ShowModified { get; set; } = true;

	[ObservableProperty]
	public partial bool ShowCreated { get; set; }

	[ObservableProperty]
	public partial bool ShowLastOpened { get; set; }

	[ObservableProperty]
	public partial bool ShowAdded { get; set; }

	[ObservableProperty]
	public partial bool ShowSize { get; set; } = true;

	[ObservableProperty]
	public partial bool ShowKind { get; set; }

	[ObservableProperty]
	public partial bool ShowVersion { get; set; }

	[ObservableProperty]
	public partial bool ShowComments { get; set; }

	[ObservableProperty]
	public partial bool ShowTags { get; set; }

	public void Apply(IEnumerable<string>? columns)
	{
		var visible = (columns ?? ["Modified", "Size"]).ToHashSet(StringComparer.Ordinal);
		foreach (string column in SupportedColumns)
		{
			SetVisible(column, visible.Contains(column));
		}
	}

	public void SetVisible(string column, bool isVisible)
	{
		switch (column)
		{
			case "Modified": ShowModified = isVisible; break;
			case "Created": ShowCreated = isVisible; break;
			case "LastOpened": ShowLastOpened = isVisible; break;
			case "Added": ShowAdded = isVisible; break;
			case "Size": ShowSize = isVisible; break;
			case "Kind": ShowKind = isVisible; break;
			case "Version": ShowVersion = isVisible; break;
			case "Comments": ShowComments = isVisible; break;
			case "Tags": ShowTags = isVisible; break;
		}
	}

	public string[] Capture() => SupportedColumns.Where(IsVisible).ToArray();

	private bool IsVisible(string column) => column switch
	{
		"Modified" => ShowModified,
		"Created" => ShowCreated,
		"LastOpened" => ShowLastOpened,
		"Added" => ShowAdded,
		"Size" => ShowSize,
		"Kind" => ShowKind,
		"Version" => ShowVersion,
		"Comments" => ShowComments,
		"Tags" => ShowTags,
		_ => false,
	};
}
