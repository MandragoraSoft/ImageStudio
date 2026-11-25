using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImageStudio.Localization;
using ImageStudio.Services;
using Microsoft.Win32.SafeHandles;

namespace ImageStudio.Configuration;

[Flags]
public enum UserDataCategory
{
    None = 0,
    SettingsAndPrompts = 1,
    ChatHistoryAndMemory = 2,
    Presets = 4,
    GalleryIndexes = 8,
    McpConfiguration = 16,
    WindowLayouts = 32,
    ProviderLoginsAndKeys = 64,
    All = SettingsAndPrompts | ChatHistoryAndMemory | Presets | GalleryIndexes |
          McpConfiguration | WindowLayouts | ProviderLoginsAndKeys,
}

public sealed record UserDataCleanupSelection(
    bool SettingsAndPrompts = true,
    bool ChatHistoryAndMemory = true,
    bool Presets = true,
    bool GalleryIndexes = true,
    bool McpConfiguration = true,
    bool WindowLayouts = true,
    bool ProviderLoginsAndKeys = false)
{
    public bool AnySelected => SettingsAndPrompts || ChatHistoryAndMemory || Presets ||
                               GalleryIndexes || McpConfiguration || WindowLayouts || ProviderLoginsAndKeys;

    public UserDataCategory Categories =>
        (SettingsAndPrompts ? UserDataCategory.SettingsAndPrompts : 0) |
        (ChatHistoryAndMemory ? UserDataCategory.ChatHistoryAndMemory : 0) |
        (Presets ? UserDataCategory.Presets : 0) |
        (GalleryIndexes ? UserDataCategory.GalleryIndexes : 0) |
        (McpConfiguration ? UserDataCategory.McpConfiguration : 0) |
        (WindowLayouts ? UserDataCategory.WindowLayouts : 0) |
        (ProviderLoginsAndKeys ? UserDataCategory.ProviderLoginsAndKeys : 0);
}

public sealed record UserDataCleanupResult(int DeletedFiles);

public interface IUserDataCleanupService
{
    string RootPath { get; }
    UserDataCleanupResult Clean(UserDataCleanupSelection selection);
}

public sealed class UserDataCleanupException(IReadOnlyList<string> failures)
    : IOException(string.Join(Environment.NewLine, failures))
{
    public IReadOnlyList<string> Failures { get; } = failures;
}

// Serializes guarded file-store writes with cleanup. Once blocked, only the selected targets stay
// unwritable for the process lifetime, preventing in-flight work from recreating deleted data.
public static class UserDataMutationGate
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, BlockedPaths> BlockedRoots = new(StringComparer.OrdinalIgnoreCase);

    private sealed class BlockedPaths
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> JsonDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool StorageSecrets { get; set; }
    }

    public static bool IsBlocked(string root, string path)
    {
        lock (Gate) return IsBlockedUnsafe(Normalize(root), Path.GetFullPath(path));
    }

    public static bool TryWrite(string root, string path, Action write)
    {
        lock (Gate)
        {
            if (IsBlockedUnsafe(Normalize(root), Path.GetFullPath(path))) return false;
            write();
            return true;
        }
    }

    internal static void BlockWrites(string root, UserDataCleanupSelection selection)
    {
        lock (Gate) AddBlockedTargets(Normalize(root), selection);
    }

    internal static T CleanUnderLock<T>(string root, UserDataCleanupSelection selection, Func<T> cleanup)
    {
        lock (Gate)
        {
            AddBlockedTargets(Normalize(root), selection);
            return cleanup();
        }
    }

    private static void AddBlockedTargets(string root, UserDataCleanupSelection selection)
    {
        if (!BlockedRoots.TryGetValue(root, out var blocked))
            BlockedRoots[root] = blocked = new BlockedPaths();

        void File(string name) => blocked.Files.Add(Path.GetFullPath(Path.Combine(root, name)));
        void JsonDirectory(string name) => blocked.JsonDirectories.Add(Path.GetFullPath(Path.Combine(root, name)));

        if (selection.SettingsAndPrompts)
        {
            File("settings.json");
            File("state.json");
            File("models.json");
        }
        if (selection.ChatHistoryAndMemory)
        {
            File("memory.json");
            File("prompts.json");
            JsonDirectory("sessions");
            JsonDirectory("prompt-chats");
        }
        if (selection.Presets) JsonDirectory("presets");
        if (selection.GalleryIndexes)
        {
            File("gallery.json");
            File("voice.json");
        }
        if (selection.McpConfiguration)
        {
            File("mcp.json");
            File("secrets-mcp-server.bin");
        }
        if (selection.WindowLayouts) File("windows.json");
        if (selection.ProviderLoginsAndKeys)
        {
            foreach (var name in UserDataCleanupService.ProviderSecretFileNames)
                File(name);
            blocked.StorageSecrets = true;
        }
    }

    private static bool IsBlockedUnsafe(string root, string path)
    {
        if (!BlockedRoots.TryGetValue(root, out var blocked)) return false;
        if (blocked.Files.Contains(path)) return true;
        var directory = Path.GetDirectoryName(path);
        if (blocked.StorageSecrets && directory is not null &&
            string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) &&
            UserDataCleanupService.StorageSecretFileName.IsMatch(Path.GetFileName(path)))
            return true;
        return directory is not null && blocked.JsonDirectories.Contains(directory) &&
               string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
}

// Deletes only named application-owned files under the isolated user-data root. It never recurses
// into directories, follows links, removes directories, or touches media referenced by an index.
public sealed class UserDataCleanupService : IUserDataCleanupService
{
    internal static readonly string[] ProviderSecretFileNames =
    [
        "secrets.bin", "secrets-openrouter.bin", "secrets-openai.bin", "secrets-mistral.bin",
        "secrets-kilo.bin", "secrets-opencodego.bin", "secrets-chatgpt.bin",
    ];

    private static readonly Regex SessionFileName = new(@"^[a-fA-F0-9]{1,64}(?:\.meta)?\.json$", RegexOptions.CultureInvariant);

    // Sekrety wtyczek magazynu (secrets-storage-<id>*.bin) po tym samym wzorcu, który przepuszcza StorageSecrets
    internal static readonly Regex StorageSecretFileName =
        new($"^secrets-{StorageSecrets.NamePattern}\\.bin$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _root;

    public UserDataCleanupService(string root) => _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public string RootPath => _root;

    public UserDataCleanupResult Clean(UserDataCleanupSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!selection.AnySelected) return new UserDataCleanupResult(0);

        return UserDataMutationGate.CleanUnderLock(_root, selection, () =>
        {
            var deleted = 0;
            var failures = new List<string>();
            try
            {
                EnsureSafeRoot();
                var categories = selection.Categories;
                if (categories.HasFlag(UserDataCategory.SettingsAndPrompts))
                    DeleteKnownFiles(["settings.json", "state.json", "models.json"], ref deleted, failures);
                if (categories.HasFlag(UserDataCategory.ChatHistoryAndMemory))
                {
                    DeleteKnownFiles(["memory.json", "prompts.json"], ref deleted, failures);
                    DeleteSessionDirectory("sessions", ref deleted, failures);
                    DeleteSessionDirectory("prompt-chats", ref deleted, failures);
                }
                if (categories.HasFlag(UserDataCategory.Presets))
                    DeletePresets(ref deleted, failures);
                if (categories.HasFlag(UserDataCategory.GalleryIndexes))
                    DeleteKnownFiles(["gallery.json", "voice.json"], ref deleted, failures);
                if (categories.HasFlag(UserDataCategory.McpConfiguration))
                {
                    DeleteKnownFiles(["mcp.json", "secrets-mcp-server.bin"], ref deleted, failures);
                }
                if (categories.HasFlag(UserDataCategory.WindowLayouts))
                    DeleteKnownFiles(["windows.json"], ref deleted, failures);
                if (categories.HasFlag(UserDataCategory.ProviderLoginsAndKeys))
                {
                    DeleteKnownFiles(ProviderSecretFileNames, ref deleted, failures);
                    DeleteStorageSecrets(ref deleted, failures);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add($"{Path.GetFileName(_root)}: {ex.Message}");
            }

            if (failures.Count > 0) throw new UserDataCleanupException(failures);
            return new UserDataCleanupResult(deleted);
        });
    }

    private void EnsureSafeRoot()
    {
        var root = Path.GetPathRoot(_root) ?? throw new IOException(Loc.T("settings.dataCleanup.path.invalid"));
        var current = root;
        foreach (var part in _root[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!TryGetAttributes(current, out var attributes)) continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Loc.T("settings.dataCleanup.path.reparseRoot", Path.GetFileName(current)));
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException(Loc.T("settings.dataCleanup.path.notDirectory", Path.GetFileName(current)));
        }
    }

    private void DeleteKnownFiles(IEnumerable<string> names, ref int deleted, List<string> failures)
    {
        foreach (var name in names)
            TryDelete(Path.Combine(_root, name), ref deleted, failures);
    }

    // Sekrety wtyczek magazynu (logowania, sesje, kolejki wysyłek) po wzorcu nazwy: nowa wtyczka nie wymaga zmian tutaj
    private void DeleteStorageSecrets(ref int deleted, List<string> failures)
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "secrets-storage-*.bin", SearchOption.TopDirectoryOnly))
        {
            if (StorageSecretFileName.IsMatch(Path.GetFileName(file)))
                TryDelete(file, ref deleted, failures);
        }
    }

    private void DeleteSessionDirectory(string name, ref int deleted, List<string> failures)
    {
        var directory = SafeChildDirectory(name);
        if (directory is null) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (SessionFileName.IsMatch(Path.GetFileName(file)))
                TryDelete(file, ref deleted, failures);
        }
    }

    private void DeletePresets(ref int deleted, List<string> failures)
    {
        var directory = SafeChildDirectory("presets");
        if (directory is null) return;
        using var pinnedDirectories = PinPresetDirectories();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                EnsureInsideRoot(Path.GetFullPath(file));
                EnsureSafeRoot();
                SafeChildDirectory("presets");
                if (!TryGetAttributes(file, out var attributes)) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(Loc.T("settings.dataCleanup.path.reparseFile"));
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new IOException(Loc.T("settings.dataCleanup.path.expectedFile"));

                if (DeleteRecognizedPreset(file, failures)) deleted++;
            }
            catch (JsonException)
            {
                // Tak samo jak magazyn zestawów, pomijamy uszkodzone pliki.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private static bool HasStringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String;

    private IDisposable PinPresetDirectories()
    {
        // Uchwyty bez FileShare.Delete uniemożliwiają podmianę rodziców na łącze podczas odczytu.
        var handles = new List<SafeFileHandle>();
        try
        {
            var root = Path.GetPathRoot(_root) ?? throw new IOException(Loc.T("settings.dataCleanup.path.invalid"));
            var current = root;
            Pin(current);
            foreach (var part in _root[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                Pin(current);
            }
            Pin(Path.Combine(_root, "presets"));
            return new PinnedDirectories(handles);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }

        void Pin(string path)
        {
            const uint readAttributes = 0x80;
            const uint openExisting = 3;
            const uint openReparsePoint = 0x00200000;
            const uint backupSemantics = 0x02000000;
            var handle = CreateFile(path, readAttributes, FileShare.Read | FileShare.Write, IntPtr.Zero,
                openExisting, openReparsePoint | backupSemantics, IntPtr.Zero);
            handles.Add(handle);
            if (handle.IsInvalid) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var attributes = (FileAttributes)info.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Loc.T("settings.dataCleanup.path.reparseDirectory", Path.GetFileName(path)));
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException(Loc.T("settings.dataCleanup.path.notDirectory", Path.GetFileName(path)));
        }
    }

    private sealed class PinnedDirectories(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    private static bool DeleteRecognizedPreset(string path, List<string> failures)
    {
        const uint genericRead = 0x80000000;
        const uint delete = 0x00010000;
        const uint openExisting = 3;
        const uint openReparsePoint = 0x00200000;
        // Ten sam uchwyt służy do sprawdzenia zawartości i usunięcia; pliku nie można w międzyczasie podmienić.
        using var handle = CreateFile(path, genericRead | delete, FileShare.Read, IntPtr.Zero,
            openExisting, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        if (((FileAttributes)info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(Loc.T("settings.dataCleanup.path.reparseFile"));
        if (((FileAttributes)info.Attributes & FileAttributes.Directory) != 0)
            throw new IOException(Loc.T("settings.dataCleanup.path.expectedFile"));
        using var stream = new FileStream(handle, FileAccess.Read);
        using var reader = new StreamReader(stream);
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        var element = document.RootElement;
        if (element.Deserialize<Preset>() is null) return false;

        // Deserializacja ma wartości domyślne: nawet obcy obiekt {} staje się zestawem.
        if (element.ValueKind != JsonValueKind.Object ||
            !HasStringProperty(element, "Name") ||
            !HasStringProperty(element, "Kind") ||
            !HasStringProperty(element, "Prompt"))
        {
            failures.Add($"{Path.GetFileName(path)}: {Loc.T("settings.dataCleanup.path.ambiguousPreset")}");
            return false;
        }

        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public int DeleteFile;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        out FileAttributeTagInfo information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass,
        ref FileDispositionInfo information, uint bufferSize);

    private string? SafeChildDirectory(string name)
    {
        var directory = Path.GetFullPath(Path.Combine(_root, name));
        EnsureInsideRoot(directory);
        if (!TryGetAttributes(directory, out var attributes)) return null;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(Loc.T("settings.dataCleanup.path.reparseDirectory", name));
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException(Loc.T("settings.dataCleanup.path.notDirectory", name));
        return directory;
    }

    private void TryDelete(string path, ref int deleted, List<string> failures)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            EnsureInsideRoot(fullPath);
            EnsureSafeRoot();
            var parent = Path.GetDirectoryName(fullPath);
            if (parent is not null && !string.Equals(parent, _root, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Loc.T("settings.dataCleanup.path.reparseDirectory", Path.GetFileName(parent)));
            if (!TryGetAttributes(fullPath, out var attributes)) return;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Loc.T("settings.dataCleanup.path.reparseFile"));
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException(Loc.T("settings.dataCleanup.path.expectedFile"));
            File.Delete(fullPath);
            deleted++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private void EnsureInsideRoot(string path)
    {
        var prefix = _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.T("settings.dataCleanup.path.escaped"));
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
