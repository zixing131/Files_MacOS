using CommunityToolkit.Mvvm.ComponentModel;

namespace Files.App.MacOS.Models;

public sealed partial class DetailColumnOrderState : ObservableObject
{
	// 列表布局中 Grid.Column 0 是图标列、1 是名称列，可重排列从 2 开始
	public const int FirstColumnIndex = 2;

	private static readonly string[] SupportedColumns =
	[
		"Modified", "Created", "LastOpened", "Added", "Size", "Kind", "Version", "Comments", "Tags",
	];

	public static IReadOnlyList<string> Supported => SupportedColumns;

	[ObservableProperty]
	public partial int ModifiedOrder { get; set; } = FirstColumnIndex;

	[ObservableProperty]
	public partial int CreatedOrder { get; set; } = FirstColumnIndex + 1;

	[ObservableProperty]
	public partial int LastOpenedOrder { get; set; } = FirstColumnIndex + 2;

	[ObservableProperty]
	public partial int AddedOrder { get; set; } = FirstColumnIndex + 3;

	[ObservableProperty]
	public partial int SizeOrder { get; set; } = FirstColumnIndex + 4;

	[ObservableProperty]
	public partial int KindOrder { get; set; } = FirstColumnIndex + 5;

	[ObservableProperty]
	public partial int VersionOrder { get; set; } = FirstColumnIndex + 6;

	[ObservableProperty]
	public partial int CommentsOrder { get; set; } = FirstColumnIndex + 7;

	[ObservableProperty]
	public partial int TagsOrder { get; set; } = FirstColumnIndex + 8;

	public int GetOrder(string column) => column switch
	{
		"Modified" => ModifiedOrder,
		"Created" => CreatedOrder,
		"LastOpened" => LastOpenedOrder,
		"Added" => AddedOrder,
		"Size" => SizeOrder,
		"Kind" => KindOrder,
		"Version" => VersionOrder,
		"Comments" => CommentsOrder,
		"Tags" => TagsOrder,
		_ => 0,
	};

	private void SetOrder(string column, int order)
	{
		switch (column)
		{
			case "Modified": ModifiedOrder = order; break;
			case "Created": CreatedOrder = order; break;
			case "LastOpened": LastOpenedOrder = order; break;
			case "Added": AddedOrder = order; break;
			case "Size": SizeOrder = order; break;
			case "Kind": KindOrder = order; break;
			case "Version": VersionOrder = order; break;
			case "Comments": CommentsOrder = order; break;
			case "Tags": TagsOrder = order; break;
		}
	}

	public void Apply(IEnumerable<string>? columns)
	{
		var ordered = (columns ?? [])
			.Where(SupportedColumns.Contains)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		foreach (string column in SupportedColumns)
		{
			if (!ordered.Contains(column, StringComparer.Ordinal))
			{
				ordered.Add(column);
			}
		}
		ApplySequence(ordered);
	}

	// 拖拽重排时调用：visibleOrder 为全部可见列（含被拖拽列）的新顺序，隐藏列保持相对顺序附在末尾
	public void ApplyVisibleOrder(IReadOnlyList<string> visibleOrder, Func<string, bool> isVisible)
	{
		var hidden = SupportedColumns
			.OrderBy(GetOrder)
			.Where(column => !isVisible(column));
		ApplySequence(visibleOrder.Concat(hidden).ToList());
	}

	private void ApplySequence(IReadOnlyList<string> sequence)
	{
		for (int index = 0; index < sequence.Count; index++)
		{
			SetOrder(sequence[index], FirstColumnIndex + index);
		}
	}

	public string[] Capture() => SupportedColumns.OrderBy(GetOrder).ToArray();
}
