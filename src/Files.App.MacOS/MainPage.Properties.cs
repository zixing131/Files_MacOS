using Files.App.MacOS.Controls;
using Files.App.MacOS.Models;
using Files.App.MacOS.Services;
using Files.App.MacOS.ViewModels;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Files.App.MacOS;

public sealed partial class MainPage
{
	private sealed class PropertiesEditor
	{
		public CheckBox[]? Permissions;
		public TextBox? Tags, Owner, Group, Acl;
		public CheckBox? Hidden, Locked;
		public ComboBox? Applications, Scope;
		public string? OriginalApplication;
		public bool CanEditSecurity => Permissions is not null && Tags is not null && Hidden is not null && Locked is not null && Owner is not null && Group is not null && Acl is not null;
		public bool IsValid => !CanEditSecurity || AreFinderTagsValid(Tags!.Text) && AreSecurityFieldsValid(Owner!.Text, Group!.Text, Acl!.Text);
	}

	private async void PropertiesButton_Click(object sender, RoutedEventArgs e)
	{
		if (Browser is not DirectoryBrowserViewModel targetBrowser || selectedItems.Count == 0 || fileTransferCancellation is not null) return;
		// 固定打开属性时的选择，避免异步加载期间切换目录而编辑了另一组文件。
		LocalFileSystemItem[] items = selectedItems.ToArray();
		targetBrowser.StatusText = GetResource("CalculatingPropertiesMessage");
		PropertiesButton.IsEnabled = false;
		try
		{
			FilePropertiesSummary summary = await FilePropertiesService.GetSummaryAsync(items.Select(item => item.Path).ToArray());
			LocalFileSystemItem? item = items.Length == 1 ? items[0] : null;
			IReadOnlyList<OpenWithApplication> applications = item is { IsDirectory: false } && summary.LinkTarget is null
				? await WorkspaceService.GetOpenWithApplicationsAsync(item.Path) : [];
			if (item is not null && item.Thumbnail is null)
			{
				byte[]? png = await WorkspaceService.GetThumbnailPngAsync(item.Path, 96, 96, 2);
				if (png is not null)
				{
					using var stream = new InMemoryRandomAccessStream();
					using (var writer = new DataWriter(stream)) { writer.WriteBytes(png); await writer.StoreAsync(); await writer.FlushAsync(); }
					stream.Seek(0);
					var bitmap = new BitmapImage();
					await bitmap.SetSourceAsync(stream);
					item.Thumbnail = bitmap;
				}
			}
			FrameworkElement content = CreatePropertiesContent(summary, item, applications, out PropertiesEditor editor);
			bool canEdit = editor.CanEditSecurity || editor.Applications is not null;
			var dialog = new ContentDialog
			{
				Title = GetResource("PropertiesDialogTitle"), Content = content,
				PrimaryButtonText = canEdit ? GetResource("SaveButtonText") : string.Empty,
				CloseButtonText = GetResource("CloseButtonText"), DefaultButton = ContentDialogButton.Close,
				IsPrimaryButtonEnabled = false, XamlRoot = XamlRoot,
			};
			dialog.Resources["ContentDialogMaxWidth"] = Math.Min(680, Math.Max(320, (XamlRoot?.Size.Width ?? 720) - 40));
			void Validate()
			{
				bool securityChanged = HasPropertySecurityChanges(summary, editor);
				dialog.IsPrimaryButtonEnabled = (securityChanged || HasDefaultApplicationChange(editor)) && (!securityChanged || editor.IsValid);
			}
			if (editor.CanEditSecurity)
			{
				foreach (TextBox box in new[] { editor.Tags!, editor.Owner!, editor.Group!, editor.Acl! }) box.TextChanged += (_, _) => Validate();
				foreach (CheckBox box in editor.Permissions!.Concat(new[] { editor.Hidden!, editor.Locked! }))
				{
					box.Checked += (_, _) => Validate(); box.Unchecked += (_, _) => Validate();
				}
			}
			if (editor.Applications is not null) editor.Applications.SelectionChanged += (_, _) => Validate();
			if (editor.Scope is not null) editor.Scope.SelectionChanged += (_, _) => Validate();
			if (await dialog.ShowAsync() != ContentDialogResult.Primary || summary.Path is null) return;
			if (HasPropertySecurityChanges(summary, editor))
			{
				// 权限矩阵只编辑 rwx，保留原有的 sticky / setuid / setgid 位。
				UnixFileMode mode = GetUnixMode(editor.Permissions!) | (summary.UnixMode!.Value & ~(UnixFileMode)0x1ff);
				await FilePropertiesService.UpdateAsync(summary.Path, new(mode, ParseFinderTags(editor.Tags!.Text), editor.Hidden!.IsChecked == true,
					editor.Locked!.IsChecked == true, editor.Owner!.Text.Trim(), editor.Group!.Text.Trim(), editor.Acl!.Text.Trim()));
			}
			if (HasDefaultApplicationChange(editor) && editor.Applications!.SelectedItem is ComboBoxItem { Tag: string applicationPath })
				await WorkspaceService.SetDefaultApplicationAsync(summary.Path, applicationPath, editor.Scope?.SelectedIndex == 1);
			targetBrowser.StatusText = GetResource("PropertiesSavedMessage");
			await targetBrowser.RefreshAsync();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			await ShowErrorAsync(ex.Message);
		}
		finally { targetBrowser.UpdateSelection(items); UpdateCommandStates(); }
	}

	private static bool HasDefaultApplicationChange(PropertiesEditor editor) =>
		editor.Applications?.SelectedItem is ComboBoxItem { Tag: string path } &&
		(path != editor.OriginalApplication || editor.Scope?.SelectedIndex == 1);

	private static bool HasPropertySecurityChanges(FilePropertiesSummary summary, PropertiesEditor editor) =>
		editor.CanEditSecurity && ((GetUnixMode(editor.Permissions!) | (summary.UnixMode!.Value & ~(UnixFileMode)0x1ff)) != summary.UnixMode ||
		!ParseFinderTags(editor.Tags!.Text).SequenceEqual(summary.FinderTags ?? []) ||
		(editor.Hidden!.IsChecked == true) != (summary.IsHidden == true) || (editor.Locked!.IsChecked == true) != (summary.IsLocked == true) ||
		editor.Owner!.Text.Trim() != (summary.Owner ?? "") || editor.Group!.Text.Trim() != (summary.Group ?? "") ||
		editor.Acl!.Text.Trim() != (summary.AccessControlList?.Trim() ?? ""));

	private FrameworkElement CreatePropertiesContent(FilePropertiesSummary summary, LocalFileSystemItem? item,
		IReadOnlyList<OpenWithApplication> applications, out PropertiesEditor editor)
	{
		var fields = editor = new PropertiesEditor();
		var root = new StackPanel { Spacing = 12, Width = Math.Min(600, Math.Max(280, (XamlRoot?.Size.Width ?? 720) - 112)) };
		var heading = new Grid { ColumnSpacing = 16, Margin = new Thickness(4, 0, 4, 8) };
		heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
		heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
		heading.Children.Add(new FileItemIcon { Item = item, Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Center });
		var title = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
		title.Children.Add(new TextBlock { Text = summary.Name ?? string.Format(GetResource("SelectedItemCountFormat"), summary.RootItemCount), FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
		title.Children.Add(new TextBlock { Text = LocalFileSystemItem.FormatSize(summary.TotalSize) + (string.IsNullOrWhiteSpace(summary.Details?.Kind) ? "" : " · " + summary.Details.Kind), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
		Grid.SetColumn(title, 1); heading.Children.Add(title); root.Children.Add(heading);
		StackPanel general = AddPropertiesSection(root, "GeneralPropertiesSection", true);
		AddDetail(general, "PropertyKindLabel", summary.Details?.Kind);
		AddDetail(general, "PropertyLocationLabel", summary.Path);
		AddDetail(general, "PropertySizeLabel", string.Format(GetResource("PropertyExactSizeFormat"), LocalFileSystemItem.FormatSize(summary.TotalSize), summary.TotalSize.ToString("N0")));
		if (item?.IsDirectory != false) AddDetail(general, "PropertyContainsLabel", string.Format(GetResource("PropertyContainsFormat"), summary.FileCount, summary.FolderCount));
		AddDetail(general, "PropertyCreatedLabel", summary.Created?.ToLocalTime().ToString("g"));
		AddDetail(general, "PropertyModifiedLabel", summary.Modified?.ToLocalTime().ToString("g"));
		AddDetail(general, "PropertyVersionLabel", summary.Details?.Version);
		AddDetail(general, "PropertyBuildLabel", summary.Details?.Build);
		AddDetail(general, "PropertyCopyrightLabel", summary.Details?.Copyright);
		AddDetail(general, "PropertyLinkTargetLabel", summary.LinkTarget);
		bool editable = summary.Path is not null && summary.LinkTarget is null;
		if (editable)
		{
			fields.Tags = AddEditablePropertyRow(general, GetResource("PropertyFinderTagsLabel"), string.Join(", ", summary.FinderTags ?? []));
			fields.Tags.PlaceholderText = GetResource("PropertyFinderTagsPlaceholder");
			fields.Hidden = AddCheckBoxPropertyRow(general, GetResource("PropertyHiddenLabel"), summary.IsHidden == true);
			fields.Locked = AddCheckBoxPropertyRow(general, GetResource("PropertyLockedLabel"), summary.IsLocked == true);
		}
		if (summary.Details is { } details)
		{
			StackPanel more = AddPropertiesSection(root, "PropertyMoreInformationSection", true);
			AddDetail(more, "PropertyContentTypeLabel", details.ContentType);
			if (details.PixelWidth > 0 && details.PixelHeight > 0)
				AddDetail(more, "PropertyDimensionsLabel", $"{details.PixelWidth:N0} × {details.PixelHeight:N0}");
			if (details.DurationSeconds is double seconds && double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds)
			{
				TimeSpan duration = TimeSpan.FromSeconds(seconds);
				AddDetail(more, "PropertyDurationLabel", $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
			}
			AddDetail(more, "PropertyCapturedLabel", details.CapturedAt);
			if (details.LastOpenedUnixSeconds is double last && last >= -62135596800 && last <= 253402300799)
				AddDetail(more, "PropertyLastOpenedLabel", DateTimeOffset.FromUnixTimeSeconds((long)last).ToLocalTime().ToString("g"));
			AddDetail(more, "PropertyAuthorsLabel", string.Join(", ", details.Authors));
			AddDetail(more, "PropertySourceLabel", string.Join("\n", details.WhereFroms));
			AddDetail(more, "PropertyCommentsLabel", details.Comments);
			if (more.Children.Count == 0) root.Children.RemoveAt(root.Children.Count - 1);
		}
		if (item is { IsDirectory: false } && editable)
		{
			StackPanel openWith = AddPropertiesSection(root, "PropertyOpenWithSection", true);
			var choices = fields.Applications = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
			foreach (var app in applications)
			{
				var choice = new ComboBoxItem { Content = app.Name + (app.IsDefault ? $" — {GetResource("DefaultApplicationLabel")}" : ""), Tag = app.ApplicationPath };
				choices.Items.Add(choice);
				if (app.IsDefault) { choices.SelectedItem = choice; fields.OriginalApplication = app.ApplicationPath; }
			}
			choices.PlaceholderText = GetResource("PropertyChooseAppPlaceholder");
			openWith.Children.Add(choices);
			var scope = fields.Scope = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
			scope.Items.Add(GetResource("PropertyOpenWithThisFile")); scope.Items.Add(GetResource("PropertyOpenWithAllFiles")); scope.SelectedIndex = 0;
			openWith.Children.Add(scope);
			var status = new TextBlock { Text = GetResource("PropertyOpenWithSaveHelp"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
			scope.SelectionChanged += (_, _) => status.Text = GetResource(scope.SelectedIndex == 1 ? "PropertyOpenWithAllHelp" : "PropertyOpenWithSaveHelp");
			var choose = new Button { Content = GetResource("ChooseApplicationButtonText"), HorizontalAlignment = HorizontalAlignment.Left };
			choose.Click += async (_, _) =>
			{
				choose.IsEnabled = false;
				try
				{
					string? path = await WorkspaceService.PickApplicationAsync();
					if (path is not null)
					{
						var choice = choices.Items.OfType<ComboBoxItem>().FirstOrDefault(option => Equals(option.Tag, path));
						if (choice is null) { choice = new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(path), Tag = path }; choices.Items.Add(choice); }
						choices.SelectedItem = choice;
					}
				}
				catch (IOException ex) { status.Text = ex.Message; }
				finally { choose.IsEnabled = true; }
			};
			openWith.Children.Add(choose); openWith.Children.Add(status);
		}
		if (summary.UnixMode is not null || summary.Owner is not null)
		{
			StackPanel permissions = AddPropertiesSection(root, "PermissionsPropertiesSection", false);
			if (editable && summary.UnixMode is { } mode) fields.Permissions = AddPermissionMatrix(permissions, mode);
			else AddDetail(permissions, "PropertyPermissionsLabel", summary.UnixMode?.ToString());
			if (editable)
			{
				StackPanel advanced = AddPropertiesSection(permissions, "PropertyAdvancedPermissionsSection", false);
				fields.Owner = AddEditablePropertyRow(advanced, string.Format(GetResource("PropertyOwnerWithIdFormat"), summary.UserId), summary.Owner ?? "");
				fields.Group = AddEditablePropertyRow(advanced, string.Format(GetResource("PropertyGroupWithIdFormat"), summary.GroupId), summary.Group ?? "");
				fields.Acl = AddEditablePropertyRow(advanced, GetResource("PropertyAclLabel"), summary.AccessControlList?.Trim() ?? "", multiline: true);
				fields.Acl.PlaceholderText = GetResource("PropertyAclPlaceholder");
				advanced.Children.Add(new TextBlock { Text = GetResource("PropertySecurityHelpText"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 });
			}
			else { AddDetail(permissions, "PropertyOwnerLabel", summary.Owner); AddDetail(permissions, "PropertyGroupLabel", summary.Group); }
		}
		var scroll = new ScrollViewer { Content = root, MaxHeight = Math.Clamp((XamlRoot?.Size.Height ?? 800) - 220, 180, 640), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 4) };
		RegisterContentWheelHandler(scroll);
		return scroll;
	}

	private StackPanel AddPropertiesSection(Panel root, string titleKey, bool expanded)
	{
		var content = new StackPanel { Spacing = 10, Padding = new Thickness(14, 8, 14, 14) };
		var expander = new Expander { Header = GetResource(titleKey), IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = content };
		root.Children.Add(expander);
		return content;
	}

	private void AddDetail(Panel panel, string labelKey, string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return;
		var row = new Grid { ColumnSpacing = 12 };
		row.ColumnDefinitions.Add(new() { Width = new(104) });
		row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
		row.Children.Add(new TextBlock { Text = GetResource(labelKey), Opacity = 0.65, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
		var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 };
		Grid.SetColumn(text, 1); row.Children.Add(text); panel.Children.Add(row);
	}
}
