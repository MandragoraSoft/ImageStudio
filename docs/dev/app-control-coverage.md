# Narzędzia MCP aplikacji: pokrycie funkcji i droga do CLI

Narzędzia dla modelu w czacie i dla klientów MCP (`McpAppServer`). Stan na 2026-11-02. Czat pomijamy (model w czacie nie steruje czatem).

## Dwa zestawy, jedna lista

`CompositeToolSet` łączy dwa zestawy (`IMcpToolSet`), więc czat i serwer MCP widzą jedną listę (`imagestudio__...` w czacie):

| Zestaw | Co robi | Zależności |
|---|---|---|
| `AppControlTools` (`IAppControl`) | Steruje **stanem okna**: wybrany model, prompt, rozmiar, referencje, załączniki wideo, `generate` z bieżącymi ustawieniami | `MainViewModel` |
| `StudioTools` (`IStudioApi`) | **Bez okna**: jawne żądania, wynik to plik w magazynie i wpis w galerii. Nie zmienia zakładek, nie używa podglądu ani odtwarzacza | tylko `IAiProvider`, magazyn, biblioteka (`IStudioLibrary`), zestawy |

Zasada dla nowych funkcji: **najpierw `StudioApi`/`StudioTools`** (jawne parametry, bez stanu okna). Do `AppControlTools` trafia tylko to, co z natury dotyczy stanu okna (np. zmiana zaznaczonego modelu). Narzędzia kosztowne i nieodwracalne mają flagę `NeedsApproval`/`Costly` (zgoda w oknie albo już w czacie). Narzędzia ukrywają się, gdy aktywny dostawca nie ma modeli danego rodzaju (`Needs`; katalog liczy `StudioApi.AvailableKinds`, `AppControlTools` pyta `IAppControl.AvailableKinds`).

## Kontrakty (`Services/StudioContracts.cs`)

- `IStudioApi`: jedyna powierzchnia operacji bez okna. Metody przyjmują niezmienne żądania (`ImageRequest`, `EditRequest`, `VideoGenerationRequest`, `SpeechRequest`, `TranscriptionRequest`, `PromptRebuildRequest`, `DescribeRequest`, `SavePresetRequest`) i zwracają `StudioResult` (wpis galerii + bajty obrazu).
- `StudioDefaults`: stałe i słowniki wspólne dla okna i narzędzi (polecenie przebudowy promptu, limit audio, języki mowy, proporcje wideo, zakresy).
- `IStudioLibrary`: gdzie lądują wyniki (w oknie to kolekcje galerii, w CLI mogą to być pliki).
- Okno używa tych samych metod: `MainViewModel` i `VoiceViewModel` wywołują `StudioApi` z `Publish: false`, a potem same dodają miniaturę, podgląd i wpis do galerii. Tłumaczenie (`StudioApi.TranslateAsync`) jest wspólne. W ViewModelach zostają tylko stan, komunikaty (`Loc`) i podgląd.
- Walidacja zakresów pikseli leży w `StudioTools` (okno ma własne granice); `StudioApi` pilnuje tylko tego, co obowiązuje wszędzie (czas i proporcje wideo, format i tempo mowy, istnienie plików, modele z katalogu).

Dodając narzędzie do `StudioTools`: metoda w `IStudioApi`/`StudioApi` + żądanie w kontraktach + test w `StudioTests.cs` (`StudioApiTests`, `StudioToolsTests`, `StudioToolsContractTests` z atrapą `RecordingStudio`, która sprawdza samo mapowanie argumentów na żądania). `FakeProvider` ma hooki `OnGenerateImage`, `OnEditImage`, `OnGenerateVideo`, `OnSynthesize`, `OnTranscribe`.

## Narzędzia

| Obszar | Narzędzia |
|---|---|
| Stan okna (`AppControlTools`) | `get_state`, `list_models`, `select_model`, `set_prompt`, `set_size`, `set_aspect`, `set_video_options`, `switch_tab`, `add_reference` (plik lub `url`), `remove_reference`, `clear_references`, `add_video_attachment`, `clear_video_attachments`, `generate`, `reload_models` |
| Obraz i wideo (`StudioTools`) | `generate_image`, `edit_image` (referencje: pliki lub adresy), `generate_video` (załączniki: pliki lub adresy) |
| Głos | `list_audio_models`, `list_voices`, `speak` (opcjonalne tłumaczenie `language`), `transcribe` |
| Prompty i zestawy | `rebuild_prompt` (nic nie zmienia, zwraca nowe pola), `describe_images`, `list_presets`, `save_preset`; `generate_image` / `edit_image` przyjmują `preset` |
| Galeria (obrazy, wideo, głos) | `list_gallery` (filtr `kind`), `remove_gallery_entry`, `delete_gallery_entry` (z plikiem, zgoda), `clear_gallery` (zgoda), `export_gallery_entry` (zgoda) |

## Ustawienia: pliki są źródłem prawdy, okno tylko je wiąże

- `settings.json` (`Ai.Provider`, adresy, motyw, język) i `state.json` (wybrane modele obrazów, wideo, mowy, rozpoznawania i tekstu, prompty) w `%LOCALAPPDATA%\ImageStudio`; klucze w `SecretStore` (DPAPI).
- `IStudioDefaults` (kontrakt) daje `StudioApi` dostawcę i domyślne modele: w oknie `MainViewModel.WindowDefaults` (zmiana modelu narzędziem = zmiana zaznaczenia w oknie, które zapisuje `state.json`), w CLI `StateFileDefaults` (czyta i zapisuje `state.json` wprost).
- Narzędzia `get_defaults` (dostawca i modele per rodzaj) oraz `set_default_model` (model musi być w katalogu dostawcy).
- Model wideo też jest w `state.json` (`UserState.VideoModel`) i wraca po restarcie.
- Wywołanie bez parametru `model` używa modelu z pliku; gdy zniknął z katalogu, `StudioApi` bierze pierwszy z listy.
- Okno zapisuje plik przy zmianie wyboru i zamknięciu, więc zmiana przez CLI przy otwartym oknie bywa nadpisana jego stanem w pamięci: używaj jednego naraz.

## Świadomie poza zasięgiem MCP

Funkcje, które istnieją tylko po to, by coś pokazać w oknie. Zamiast nich model dostaje ścieżkę pliku:

- Podgląd wyniku: zapis jako, kopiowanie do schowka, czyszczenie ekranu, powiększenie i dopasowanie (`SaveImageAs`, `CopyImage`, `ClearResult`, `ResetZoom`, `FitImage`). Odpowiednik: `export_gallery_entry`.
- Odtwarzanie dźwięku i mikrofon (`Play`, `ToggleRecording`): model nie odtwarza ani nie nagrywa; `speak` zapisuje plik, `transcribe` czyta gotowy plik.
- Schowek (`PasteReference`, `PasteVideoAttachment`) i okna wyboru plików.
- Przeglądarka modeli z filtrami, okno Ustawień, zmiana dostawcy i kluczy API: dotykają konfiguracji i sekretów.
- Edytor promptu w oknie (`EditGeneratePrompt` i pokrewne): odpowiednik bez okna to `rebuild_prompt`.
- Kopiowanie promptu wpisu: prompt jest w wyniku `list_gallery`.

## Uruchomienie z CLI

`ImageStudio.exe mcp ...` (kod: `Cli/McpCli.cs`, `Cli/McpCliHost.cs`, wejście w `Program.cs`) uruchamia narzędzia `StudioTools` bez okna. Używa tych samych ustawień, kluczy API, dostawcy, magazynu wyników i ostatnio wybranych modeli co okno (`McpCliHost.CreateStudioAsync` składa kontener bez `MainViewModel`).

```
ImageStudio.exe mcp [serve] [--stdio | --http] [--port N] [--token T] [--yes]   # serwer MCP; domyślnie stdio
ImageStudio.exe mcp tools                                                         # narzędzia dostępne przy aktywnym dostawcy
ImageStudio.exe mcp call <narzędzie> [json] [--yes]                               # jedno wywołanie, wynik na stdout
```

- **stdio** to tryb dla klientów, które same uruchamiają proces (np. Claude Desktop: `command` = ścieżka do exe, `args` = `["mcp", "--stdio", "--yes"]`). Na stdout idzie wyłącznie protokół; dziennik (`AppLog`) idzie zawsze na stderr.
- **--http** nasłuchuje na `127.0.0.1:<port>` (domyślnie 8765) z tokenem: `--token`, zmienna `IMAGESTUDIO_MCP_TOKEN` albo wylosowany i wypisany na stderr.
- **Zgoda:** operacje kosztowne i nieodwracalne są odrzucane, dopóki nie podasz `--yes` (kod wyjścia `call` = 3).
- **Kody wyjścia:** 0 sukces, 1 błąd narzędzia lub dostawcy, 2 zły argument, 3 odrzucone bez `--yes`.
- **Kodowanie:** wejście i wyjście konsoli to UTF-8 bez BOM.
- **Wersja okienkowa (WinExe):** przy uruchomieniu z terminala `EnsureConsole` dołącza się do konsoli rodzica. Przy przekierowanym stdout (potok, plik, klient MCP) nic nie trzeba. Dołączanie do konsoli terminala nie było sprawdzone w teście automatycznym.
- **Galeria:** CLI zapisuje wyniki w `gallery.json` / `voice.json` (`FileStudioLibrary`). Okno trzyma galerię w pamięci, więc równoległe użycie okna i CLI nadpisuje sobie wpisy: używaj jednego naraz.
- Narzędzia sterujące stanem okna (`AppControlTools`) w CLI nie istnieją. `list_models` jest w obu zestawach; w oknie wygrywa wersja z `AppControlTools` (kolejność preferowanych), w CLI z `StudioTools`.

Testy: `McpCliTests` (parsowanie, `tools`, `call`, zgoda, serwer HTTP na atrapie dostawcy) i `FileStudioLibraryTests`.
