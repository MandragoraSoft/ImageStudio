using ImageStudio.Configuration;

namespace ImageStudio.Services;

// Biblioteka wyników oparta o pliki galerii (gallery.json, voice.json) dla trybu bez okna (CLI). Każda zmiana zapisuje plik od razu.
// Okno aplikacji trzyma galerię w pamięci, więc równoległe użycie okna i CLI nadpisuje sobie wpisy: używaj jednego naraz.
public sealed class FileStudioLibrary : IStudioLibrary
{
    private readonly IGalleryStore _media;
    private readonly IVoiceGalleryStore _voice;
    private readonly List<GalleryEntry> _mediaItems;
    private readonly List<GalleryEntry> _voiceItems;

    public FileStudioLibrary(IGalleryStore media, IVoiceGalleryStore voice)
    {
        _media = media;
        _voice = voice;
        _mediaItems = media.Load().ToList();
        _voiceItems = voice.Load().ToList();
    }

    public IReadOnlyList<GalleryEntry> Media => _mediaItems;
    public IReadOnlyList<GalleryEntry> Voice => _voiceItems;

    public void AddMedia(GalleryEntry entry)
    {
        _mediaItems.Insert(0, entry);
        if (_mediaItems.Count > GalleryStore.MaxEntries) _mediaItems.RemoveRange(GalleryStore.MaxEntries, _mediaItems.Count - GalleryStore.MaxEntries);
        _media.Save(_mediaItems);
    }

    public void AddVoice(GalleryEntry entry)
    {
        _voiceItems.Insert(0, entry);
        if (_voiceItems.Count > FileVoiceGalleryStore.MaxEntries)
            _voiceItems.RemoveRange(FileVoiceGalleryStore.MaxEntries, _voiceItems.Count - FileVoiceGalleryStore.MaxEntries);
        _voice.Save(_voiceItems);
    }

    public bool Remove(string id)
    {
        if (_mediaItems.RemoveAll(e => e.Id == id) > 0)
        {
            _media.Save(_mediaItems);
            return true;
        }

        if (_voiceItems.RemoveAll(e => e.Id == id) > 0)
        {
            _voice.Save(_voiceItems);
            return true;
        }

        return false;
    }

    public void Clear(bool voice)
    {
        if (voice)
        {
            _voiceItems.Clear();
            _voice.Save(_voiceItems);
        }
        else
        {
            _mediaItems.Clear();
            _media.Save(_mediaItems);
        }
    }
}
