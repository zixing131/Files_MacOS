using System.Globalization;
using System.Text.Json;
using Files.App.MacOS.Models;
using Windows.Globalization;

namespace Files.App.MacOS.Services;

internal static class AppLanguageManager
{
	public static AppLanguagePreference LoadPreference()
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(JsonAppSettingsService.DefaultSettingsPath));
			if (document.RootElement.TryGetProperty(nameof(AppSettings.Language), out JsonElement value) &&
				value.TryGetInt32(out int preference) &&
				Enum.IsDefined((AppLanguagePreference)preference))
			{
				return (AppLanguagePreference)preference;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
		}

		return AppLanguagePreference.System;
	}

	public static void Apply(AppLanguagePreference preference)
	{
		string? diagnosticOverride = Environment.GetEnvironmentVariable("FILES_MACOS_LANGUAGE");
		ApplicationLanguages.PrimaryLanguageOverride = diagnosticOverride switch
		{
			"en" => "en",
			"zh-Hans" => "zh-Hans",
			_ => preference switch
			{
				AppLanguagePreference.English => "en",
				AppLanguagePreference.SimplifiedChinese => "zh-Hans",
				_ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en",
			},
		};
	}
}
