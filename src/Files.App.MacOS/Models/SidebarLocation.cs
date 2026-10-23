namespace Files.App.MacOS.Models;

public sealed record SidebarLocationOption(string Id, string Name, string Path, string Glyph, string ExternalUrl = "");

public sealed record SidebarLocation(
	string Name,
	string Path,
	string Glyph,
	bool IsNetworkServer = false,
	string ExternalUrl = "",
	bool CanEject = false,
	bool IsHeader = false,
	string SectionId = "",
	bool IsExpanded = true,
	string AccessibilityState = "",
	int Depth = 0,
	bool IsExpandableRoot = false)
{
	public Thickness Indent => new(Depth * 14, 0, 0, 0);
	public string ExpansionGlyph => IsExpanded ? "⌄" : "›";
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
