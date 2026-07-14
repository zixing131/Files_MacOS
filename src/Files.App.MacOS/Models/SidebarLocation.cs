namespace Files.App.MacOS.Models;

public sealed record SidebarLocation(
	string Name,
	string Path,
	string Glyph,
	bool IsNetworkServer = false,
	bool IsHeader = false,
	string SectionId = "",
	bool IsExpanded = true)
{
	public string DisplayGlyph => IsHeader ? IsExpanded ? "⌄" : "›" : Glyph;

	public static SidebarLocation Header(string name, string sectionId, bool isExpanded) =>
		new(name, string.Empty, string.Empty, IsHeader: true, SectionId: sectionId, IsExpanded: isExpanded);
}
