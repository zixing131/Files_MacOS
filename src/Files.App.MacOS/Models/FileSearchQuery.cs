using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Files.App.MacOS.Models;

public enum FileSearchKind
{
	File,
	Folder,
	Image,
	Audio,
	Video,
	Document,
	Archive,
}

public sealed class FileSearchQuery
{
	private static readonly Regex TokenPattern = new(
		@"(?:(?<key>[A-Za-z]+):)?(?:""(?<quoted>[^""]*)""|(?<bare>\S+))",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex SizePattern = new(
		@"^(?<operator>>=|<=|>|<|=)?(?<value>\d+(?:\.\d+)?)(?<unit>B|KB|MB|GB|TB)?$",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
	private static readonly HashSet<string> ImageExtensions = CreateExtensions("avif bmp gif heic heif ico jpeg jpg png psd svg tiff tif webp");
	private static readonly HashSet<string> AudioExtensions = CreateExtensions("aac aiff alac flac m4a mp3 ogg opus wav wma");
	private static readonly HashSet<string> VideoExtensions = CreateExtensions("avi m4v mkv mov mp4 mpeg mpg webm wmv");
	private static readonly HashSet<string> DocumentExtensions = CreateExtensions("csv doc docx epub html md numbers odf ods odt pages pdf ppt pptx rtf tex txt xls xlsx xml");
	private static readonly HashSet<string> ArchiveExtensions = CreateExtensions("7z bz2 dmg gz iso rar tar tgz xz zip");

	private FileSearchQuery(string originalText)
	{
		OriginalText = originalText;
	}

	public string OriginalText { get; }

	public List<string> Terms { get; } = [];

	public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);

	public HashSet<FileSearchKind> Kinds { get; } = [];

	public List<HashSet<string>> TagGroups { get; } = [];

	public bool RequiresFinderTags => TagGroups.Count > 0;

	public long? MinimumSize { get; private set; }

	public bool MinimumSizeInclusive { get; private set; } = true;

	public long? MaximumSize { get; private set; }

	public bool MaximumSizeInclusive { get; private set; } = true;

	public DateTimeOffset? ModifiedAfter { get; private set; }

	public bool ModifiedAfterInclusive { get; private set; } = true;

	public DateTimeOffset? ModifiedBefore { get; private set; }

	public bool ModifiedBeforeInclusive { get; private set; } = true;

	public static FileSearchQuery Parse(string query)
	{
		string normalized = query.TrimStart().TrimStart('$').Trim();
		var result = new FileSearchQuery(query);
		foreach (Match match in TokenPattern.Matches(normalized))
		{
			string key = match.Groups["key"].Value;
			string value = match.Groups["quoted"].Success
				? match.Groups["quoted"].Value
				: match.Groups["bare"].Value;
			if (!result.TryAddFilter(key, value))
			{
				string term = string.IsNullOrEmpty(key) ? value : match.Value;
				if (!string.IsNullOrWhiteSpace(term))
				{
					result.Terms.Add(term);
				}
			}
		}

		return result;
	}

	public bool MatchesMetadata(FileSystemInfo info, bool isDirectory, IReadOnlyCollection<string>? finderTags = null)
	{
		if (Extensions.Count > 0 && (isDirectory || !Extensions.Contains(Path.GetExtension(info.Name).TrimStart('.'))))
		{
			return false;
		}

		if (Kinds.Count > 0 && !Kinds.Any(kind => MatchesKind(kind, info.Name, isDirectory)))
		{
			return false;
		}

		if (TagGroups.Count > 0 &&
			(finderTags is null || TagGroups.Any(group => !group.Any(tag => finderTags.Contains(tag, StringComparer.CurrentCultureIgnoreCase)))))
		{
			return false;
		}

		if (MinimumSize.HasValue || MaximumSize.HasValue)
		{
			if (isDirectory || info is not FileInfo file || !MatchesRange(file.Length, MinimumSize, MinimumSizeInclusive, MaximumSize, MaximumSizeInclusive))
			{
				return false;
			}
		}

		return MatchesRange(
			info.LastWriteTimeUtc,
			ModifiedAfter?.UtcDateTime,
			ModifiedAfterInclusive,
			ModifiedBefore?.UtcDateTime,
			ModifiedBeforeInclusive);
	}

	public IReadOnlyList<string> GetContentTerms(string name)
	{
		return Terms
			.Where(term => !name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
			.ToArray();
	}

	public string ToSpotlightJson()
	{
		return JsonSerializer.Serialize(new
		{
			terms = Terms,
			extensions = Extensions,
			kinds = Kinds.Select(static kind => kind.ToString().ToLowerInvariant()),
			tagGroups = TagGroups.Select(static group => group.ToArray()),
			minimumSize = MinimumSize,
			minimumSizeInclusive = MinimumSizeInclusive,
			maximumSize = MaximumSize,
			maximumSizeInclusive = MaximumSizeInclusive,
			modifiedAfter = ModifiedAfter?.ToUnixTimeSeconds(),
			modifiedAfterInclusive = ModifiedAfterInclusive,
			modifiedBefore = ModifiedBefore?.ToUnixTimeSeconds(),
			modifiedBeforeInclusive = ModifiedBeforeInclusive,
		});
	}

	private bool TryAddFilter(string key, string value)
	{
		switch (key.ToLowerInvariant())
		{
			case "ext":
			case "extension":
				foreach (string extension in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					string normalized = extension.TrimStart('*').TrimStart('.');
					if (normalized.Length > 0)
					{
						Extensions.Add(normalized);
					}
				}
				return Extensions.Count > 0;

			case "kind":
			case "type":
				foreach (string kind in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				{
					if (Enum.TryParse(kind, ignoreCase: true, out FileSearchKind parsed))
					{
						Kinds.Add(parsed);
					}
				}
				return Kinds.Count > 0;

			case "size":
				return TryAddSize(value);

			case "tag":
				var tags = new HashSet<string>(
					value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
					StringComparer.CurrentCultureIgnoreCase);
				if (tags.Count > 0)
				{
					TagGroups.Add(tags);
					return true;
				}
				return false;

			case "modified":
			case "date":
				return TryAddModified(value);

			default:
				return false;
		}
	}

	private bool TryAddSize(string value)
	{
		Match match = SizePattern.Match(value);
		if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount))
		{
			return false;
		}

		double multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
		{
			"KB" => 1024d,
			"MB" => 1024d * 1024,
			"GB" => 1024d * 1024 * 1024,
			"TB" => 1024d * 1024 * 1024 * 1024,
			_ => 1d,
		};
		if (amount * multiplier > long.MaxValue)
		{
			return false;
		}

		long bytes = (long)Math.Ceiling(amount * multiplier);
		string comparison = match.Groups["operator"].Value;
		if (comparison is "<" or "<=")
		{
			MaximumSize = bytes;
			MaximumSizeInclusive = comparison is "<=";
		}
		else if (comparison is ">" or ">=")
		{
			MinimumSize = bytes;
			MinimumSizeInclusive = comparison is ">=";
		}
		else
		{
			MinimumSize = MaximumSize = bytes;
			MinimumSizeInclusive = MaximumSizeInclusive = true;
		}

		return true;
	}

	private bool TryAddModified(string value)
	{
		string comparison = value.StartsWith(">=", StringComparison.Ordinal) || value.StartsWith("<=", StringComparison.Ordinal)
			? value[..2]
			: value.StartsWith('>') || value.StartsWith('<') || value.StartsWith('=')
				? value[..1]
				: string.Empty;
		string dateText = value[comparison.Length..];
		DateTimeOffset start;
		DateTimeOffset end;
		DateTimeOffset today = new(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
		switch (dateText.ToLowerInvariant())
		{
			case "today":
				start = today;
				end = today.AddDays(1);
				break;
			case "yesterday":
				start = today.AddDays(-1);
				end = today;
				break;
			case "thisweek":
				int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
				start = today.AddDays(-daysSinceMonday);
				end = start.AddDays(7);
				break;
			case "thismonth":
				start = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, today.Offset);
				end = start.AddMonths(1);
				break;
			default:
				if (!DateTimeOffset.TryParse(dateText, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out start) &&
					!DateTimeOffset.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out start))
				{
					return false;
				}
				start = start.Date;
				end = start.AddDays(1);
				break;
		}

		if (comparison is ">" or ">=")
		{
			ModifiedAfter = start;
			ModifiedAfterInclusive = comparison is ">=";
		}
		else if (comparison is "<" or "<=")
		{
			ModifiedBefore = start;
			ModifiedBeforeInclusive = comparison is "<=";
		}
		else
		{
			ModifiedAfter = start;
			ModifiedAfterInclusive = true;
			ModifiedBefore = end;
			ModifiedBeforeInclusive = false;
		}

		return true;
	}

	private static bool MatchesKind(FileSearchKind kind, string name, bool isDirectory)
	{
		if (kind is FileSearchKind.Folder)
		{
			return isDirectory;
		}
		if (isDirectory)
		{
			return false;
		}
		if (kind is FileSearchKind.File)
		{
			return true;
		}

		string extension = Path.GetExtension(name).TrimStart('.');
		return kind switch
		{
			FileSearchKind.Image => ImageExtensions.Contains(extension),
			FileSearchKind.Audio => AudioExtensions.Contains(extension),
			FileSearchKind.Video => VideoExtensions.Contains(extension),
			FileSearchKind.Document => DocumentExtensions.Contains(extension),
			FileSearchKind.Archive => ArchiveExtensions.Contains(extension),
			_ => false,
		};
	}

	private static bool MatchesRange<T>(T value, T? minimum, bool minimumInclusive, T? maximum, bool maximumInclusive)
		where T : struct, IComparable<T>
	{
		if (minimum.HasValue)
		{
			int comparison = value.CompareTo(minimum.Value);
			if (comparison < 0 || (!minimumInclusive && comparison is 0))
			{
				return false;
			}
		}
		if (maximum.HasValue)
		{
			int comparison = value.CompareTo(maximum.Value);
			if (comparison > 0 || (!maximumInclusive && comparison is 0))
			{
				return false;
			}
		}

		return true;
	}

	private static HashSet<string> CreateExtensions(string values)
	{
		return new(values.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
	}
}
