namespace Files.App.MacOS.Services;

public enum NetworkServerAddressError
{
	None,
	Required,
	Invalid,
	UnsupportedScheme,
	MissingHost,
	CredentialsNotAllowed,
	QueryOrFragmentNotAllowed,
}

public static class NetworkServerAddress
{
	private static readonly HashSet<string> SupportedSchemes = new(StringComparer.OrdinalIgnoreCase)
	{
		"smb",
		"afp",
		"nfs",
		"ftp",
	};

	public static bool TryNormalize(string? value, out string normalized, out NetworkServerAddressError error)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
		{
			error = NetworkServerAddressError.Required;
			return false;
		}

		if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
		{
			error = NetworkServerAddressError.Invalid;
			return false;
		}
		if (!SupportedSchemes.Contains(uri.Scheme))
		{
			error = NetworkServerAddressError.UnsupportedScheme;
			return false;
		}
		if (string.IsNullOrWhiteSpace(uri.Host))
		{
			error = NetworkServerAddressError.MissingHost;
			return false;
		}
		if (!string.IsNullOrEmpty(uri.UserInfo))
		{
			error = NetworkServerAddressError.CredentialsNotAllowed;
			return false;
		}
		if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
		{
			error = NetworkServerAddressError.QueryOrFragmentNotAllowed;
			return false;
		}

		var builder = new UriBuilder(uri)
		{
			Scheme = uri.Scheme.ToLowerInvariant(),
			Host = uri.IdnHost.ToLowerInvariant(),
			UserName = string.Empty,
			Password = string.Empty,
			Query = string.Empty,
			Fragment = string.Empty,
		};
		normalized = builder.Uri.AbsoluteUri;
		if (builder.Path.Length > 1)
		{
			normalized = normalized.TrimEnd('/');
		}

		error = NetworkServerAddressError.None;
		return true;
	}

	public static string GetDisplayName(string address)
	{
		if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
		{
			return address;
		}

		string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
		return string.IsNullOrEmpty(path) ? uri.Host : $"{uri.Host}{path}";
	}
}
