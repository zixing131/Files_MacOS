using Files.App.MacOS.Models;
using Files.App.MacOS.Services;

namespace Files.App.MacOS;

public sealed partial class MainPage
{
	internal void RefreshLanguage(AppLanguagePreference language)
	{
		currentSettings = currentSettings with { Language = language };
		persistedSettingsBaseline = persistedSettingsBaseline with { Language = language };
		ViewModel.RefreshLanguage();
		UpdateSidebarSelection();
		LiveLocalization.Refresh(this);
		LiveLocalization.Refresh(PrimaryBackgroundContextFlyout);
		LiveLocalization.Refresh(SecondaryBackgroundContextFlyout);
		MoreSelectionSubItem.Text = GetResource("MoreSelectionSubItem/Text");
		MoreArchiveSubItem.Text = GetResource("MoreArchiveSubItem/Text");
		LocalizeContextMenuSubItems();
		ConfigureIconButton(ToggleSidebarButton, "ToggleSidebarTooltip");
		ConfigureIconButton(BackButton, "BackNavigationTooltip");
		ConfigureIconButton(ForwardButton, "ForwardNavigationTooltip");
		ConfigureIconButton(UpButton, "UpNavigationTooltip");
		ConfigureIconButton(RefreshButton, "RefreshNavigationTooltip");
		ConfigureIconButton(NewTabButton, "NewTabTooltip");
		ConfigureIconButton(ClosePreviewPaneButton, "ClosePreviewPaneTooltip");
		ConfigureIconButton(GridViewStatusButton, "GridViewTooltip");
		ConfigureIconButton(DetailsViewStatusButton, "DetailsViewTooltip");
		ConfigureIconButton(ColumnViewStatusButton, "ColumnViewTooltip");
		ConfigureIconButton(SearchOptionsButton, "SearchOptionsTooltip");
		ConfigureIconButton(MoreCommandsButton, "MoreCommandsTooltip");
		ConfigureIconButton(SidebarDivider, "SidebarResizeTooltip");
		ConfigureIconButton(SplitDivider, "SplitResizeTooltip");
		ConfigureAccessibleName(Tabs, "TabStripAutomationName");
		ConfigureAccessibleName(SidebarList, "LocationsAutomationName");
		ConfigureAccessibleName(AddressBox, "AddressBarAutomationName");
		ConfigureAccessibleName(SearchBox, "SearchBoxAutomationName");
		ConfigureAccessibleName(GridItems, "PrimaryFilesAutomationName");
		ConfigureAccessibleName(DetailsItems, "PrimaryFilesAutomationName");
		ConfigureAccessibleName(PrimaryColumnItems, "PrimaryFilesAutomationName");
		ConfigureAccessibleName(SecondaryGridItems, "SecondaryFilesAutomationName");
		ConfigureAccessibleName(SecondaryDetailsItems, "SecondaryFilesAutomationName");
		ConfigureAccessibleName(SecondaryColumnItems, "SecondaryFilesAutomationName");
		ConfigureAccessibleName(FileOperationProgressBar, "FileOperationProgressAutomationName");
		ConfigureAccessibleName(StatusTextBlock, "StatusBarAutomationName");
		ConfigureAccessibleName(PreviewSelectionSummary, "PreviewSelectionAutomationName");
		ConfigureCommandButtonAccessibility(
			UndoButton,
			RedoButton,
			NewCommandButton,
			CutButton,
			CopyButton,
			PasteButton,
			RenameButton,
			ShareButton,
			DeleteButton,
			RevealButton,
			PropertiesButton,
			SelectionButton,
			ArchiveButton,
			FavoriteButton,
			TerminalButton,
			PreviewPaneButton,
			SplitViewButton,
			SortCommandButton);
		ToolTipService.SetToolTip(SearchBox, GetResource("SearchSyntaxHelp"));
		UpdateAddressBar();
		UpdateSortHeaderVisuals();
		UpdatePreviewPaneContent();
	}
}
