namespace Files.App.MacOS.Services;

public interface IDirectoryChangeMonitor : IDisposable
{
	event EventHandler? Changed;

	void Watch(string path, bool includeSubdirectories = false);
}
