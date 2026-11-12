using System.Reflection;

namespace ImageStudio.ViewModels;

public sealed class AboutViewModel
{
    public string Version { get; } = ReadVersion(typeof(App).Assembly);

    internal static string ReadVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "";
}
