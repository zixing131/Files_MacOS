namespace Files.App.MacOS.Models;

public sealed record SidebarLocationOption(string Id, string Name, string Path, string Glyph);

public sealed record SidebarLocation(
	string Name,
	string Path,
	string Glyph,
	bool IsNetworkServer = false,
	bool IsHeader = false,
	string SectionId = "",
	bool IsExpanded = true,
	string AccessibilityState = "")
{
	public string DisplayGlyph => IsHeader ? IsExpanded ? "⌄" : "›" : Glyph;
	public string AutomationName => IsHeader && !string.IsNullOrWhiteSpace(AccessibilityState)
		? $"{Name}. {AccessibilityState}"
		: Name;

	public static SidebarLocation Header(string name, string sectionId, bool isExpanded, string accessibilityState) =>
		new(
			name,
			string.Empty,
			string.Empty,
			IsHeader: true,
			SectionId: sectionId,
			IsExpanded: isExpanded,
			AccessibilityState: accessibilityState);
}
