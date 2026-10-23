using System.Xml.Linq;
using System.Collections.Concurrent;
using Files.App.MacOS.Controls;
using Microsoft.UI.Xaml.Media;
using Uno.UI.Helpers;
using Windows.Globalization;

namespace Files.App.MacOS.Services;

// 复用同一份 resw；只更新文案，不重建页面，不丢失选中项、历史记录和正在进行的操作。
internal static class LiveLocalization
{
	private static readonly ConcurrentDictionary<string, Dictionary<string, string>> catalogs = new();
	private static Dictionary<string, string> Catalog
	{
		get
		{
			string language = ApplicationLanguages.PrimaryLanguageOverride == "zh-Hans" ? "zh-Hans" : "en";
			if (!catalogs.TryGetValue(language, out var catalog))
			{
				using var stream = typeof(LiveLocalization).Assembly.GetManifestResourceStream($"Files.Strings.{language}")!;
				catalog = XDocument.Load(stream).Root!.Elements("data").ToDictionary(item => (string)item.Attribute("name")!, item => item.Element("value")!.Value);
				catalog = catalogs.GetOrAdd(language, catalog);
			}
			return catalog;
		}
	}
	internal static string GetString(string key) => Catalog.GetValueOrDefault(key.Replace('/', '.'), key);

	internal static void Refresh(DependencyObject root)
	{
		Refresh(root, new HashSet<DependencyObject>());
	}
	private static void Refresh(DependencyObject root, HashSet<DependencyObject> visited)
	{
		if (!visited.Add(root)) return;
		string? uid = MarkupHelper.GetXUid(root);
		if (!string.IsNullOrEmpty(uid))
		{
			if (Catalog.TryGetValue(uid + ".Text", out string? text))
			{
				switch (root)
				{
					case TextBlock label: label.Text = text; break;
					case MenuFlyoutItem item: item.Text = text; break;
					case MenuFlyoutSubItem sub: sub.Text = text; break;
				}
			}
			if (Catalog.TryGetValue(uid + ".Content", out string? content))
			{
				if (root is CommandLabel command) command.Content = content;
				else if (root is ContentControl control) control.Content = content;
			}
			if (Catalog.TryGetValue(uid + ".PlaceholderText", out string? placeholder) && root is TextBox box) box.PlaceholderText = placeholder;
			if (Catalog.TryGetValue(uid + ".AutomationProperties.Name", out string? name)) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(root, name);
			if (Catalog.TryGetValue(uid + ".ToolTipService.ToolTip", out string? tip)) ToolTipService.SetToolTip(root, tip);
		}
		if (root is FrameworkElement element)
		{
			foreach (var resource in element.Resources.Values.OfType<DependencyObject>().ToArray()) Refresh(resource, visited);
			if (element.ContextFlyout is { } context) Refresh(context, visited);
		}
		if (root is Button { Flyout: { } flyout }) Refresh(flyout, visited);
		if (root is MenuFlyout menu) foreach (var item in menu.Items) Refresh(item, visited);
		if (root is MenuFlyoutSubItem submenu) foreach (var item in submenu.Items) Refresh(item, visited);
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Refresh(VisualTreeHelper.GetChild(root, i), visited);
	}
}
