# Czat: strumień, sesje, tokeny, rozumowanie, narzędzia MCP

_Kiedy czytać: dotykasz `ChatViewModel`, odpowiedzi strumieniowych, sesji, zużycia tokenów, rozumowania albo MCP._

## Odpowiedzi strumieniowe w czacie

Ustawienie `AppSettings.UseStreaming` (Ustawienia → Aplikacja → "Używaj odpowiedzi strumieniowej", domyślnie włączone). `IAiProvider.ChatStreamAsync(..., onDelta)` ma domyślną implementację (jedno `ChatAsync` i jeden kawałek); trzej dostawcy nadpisują ją przez `OpenAiCompat.ChatStreamAsync` (SSE, `stream_options.include_usage`, składanie `tool_calls` po `index`, kawałki rozumowania). `ChatMessage` jest obserwowalną klasą: `ChatViewModel` dodaje pustą wiadomość asystenta od razu i dopisuje do niej kawałki (`MessageUpdated` przewija widok), a na końcu uzupełnia pełnym wynikiem (`Complete`). Strumień jest w stopce, ale nie przyciemnia okna odpowiedzi (`IActivityTracker.Begin(..., dimResponse: false)`). Uwaga praktyczna: czy tekst napływa stopniowo, zależy od dostawcy i modelu (część modeli UberLLM wysyła cały strumień naraz po zakończeniu generowania).

## Sesje czatu i Stop

Każda rozmowa to sesja zapisywana po każdej odpowiedzi przez `IChatSessionStore` (`FileChatSessionStore`: `sessions\<id>.json` z rozmową razem z obrazami, rozumowaniem i zużyciem oraz `<id>.meta.json` do listy; id musi być szesnastkowe). `ChatViewModel` ma listę `Sessions`, `NewChatCommand` (nowa sesja), `NewIncognitoChatCommand` (sesja incognito: nie jest zapisywana ani listowana, znika po przejściu do innej), `DeleteSessionCommand` i otwieranie przez `SelectedSession`. `StopCommand` anuluje trwającą odpowiedź (token idzie do `ChatAsync`/`ChatStreamAsync` i narzędzi); napłynięty tekst zostaje, a brak tekstu przywraca pytanie do pola. Pole `IsSending` blokuje zmianę sesji.

## Zużycie tokenów w czacie

`ChatReply.Usage` (`TokenUsage`: wejście, wyjście, z cache) czyta `OpenAiCompat` z `usage` w kilku formatach (`prompt_tokens_details.cached_tokens`, `prompt_cache_hit_tokens`, `cache_read_input_tokens`); trafia do `ChatMessage.Usage` i jest pokazywane pod odpowiedzią (`UsageText`). Aplikacja sama niczego nie cachuje: wysyła pełną historię, a cache działa po stronie dostawcy.

## Rozumowanie w czacie

`IAiProvider.ChatAsync(..., ChatOptions? options)`; `ChatOptions.Parameter` to parametr, który model deklaruje w `supported_parameters` (`ReasoningSupport.ParameterFor`: `reasoning_effort` > `reasoning` > `include_reasoning`), a `OpenAiCompat` wysyła go wg stylu i czyta `reasoning_content`/`reasoning`/`reasoning_details` do `ChatReply.Reasoning` (`ChatMessage.Reasoning`, tylko podgląd, nie wraca do API). `ChatViewModel.SupportsReasoning` pokazuje sterowanie (włącz/wyłącz, poziom low/medium/high) tylko dla takich modeli; wybór jest w `UserState`.

## Czat i narzędzia MCP

`ChatViewModel` + `ToolsViewModel` jako klient narzędzi MCP (`McpToolHost`, konfiguracja w `McpConfig`). Osobno `McpAppServer` (lokalny serwer MCP) wystawia sterowanie aplikacją: stan okna przez `IAppControl` / `AppControlTools` (`MainViewModel.AppControl`) i operacje bez okna przez `StudioApi` / `StudioTools`; oba zestawy łączy `CompositeToolSet`. Pokrycie funkcji i droga do CLI: [app-control-coverage.md](app-control-coverage.md).

## Markdown w odpowiedziach

Odpowiedzi modelu (`ChatMessage.IsAssistant`) pokazuje `MarkdownView` (`RichTextBox` tylko do odczytu), a tekst użytkownika i narzędzi zostaje w zwykłym `TextBox`. `MarkdownRenderer` (Markdig) zamienia Markdown na `FlowDocument`: akapity, nagłówki, bloki kodu (Consolas, bez znaczników ```), listy, cytaty, tabele, listy zadań, pogrubienia, linki. Dokument budowany jest od nowa przy każdej zmianie tekstu (kawałki strumienia). Kolory idą przez `SetResourceReference` (motyw działa bez odświeżania), linki otwierają tylko `http(s)`, a obrazy z sieci nie są pobierane. Wyłączona lista (np. sesje w czasie odpowiedzi) nie może być biała: `ListBox` w `App.xaml` ma własny szablon, bo domyślny maluje stan wyłączony na biało.

## Wbudowane narzędzia czatu (builtin__*)

`BuiltinTools` (`Services`) działa obok serwerów MCP i narzędzi aplikacji (`ToolsViewModel` składa trzy źródła; przełącznik `BuiltinEnabled`, zapis w `UserState.BuiltinToolsEnabled`, domyślnie włączone). Nazwy dla modelu mają przedrostek `builtin__`. Zestaw: `get_datetime` (data, godzina, dzień tygodnia, opcjonalna strefa), `memory_save` / `memory_list` / `memory_delete` (pamięć między sesjami w `memory.json` za `IMemoryStore`, do 200 wpisów po 500 znaków, bez duplikatów) oraz `files_list` / `files_search` (po nazwie i treści, wzorzec `*`/`?`, do 50 wyników i 5 tys. plików, bez dowiązań, plików binarnych i większych niż 2 MB) / `files_read` / `files_write` w jednym folderze roboczym (`Dokumenty\ImageStudio\Workspace`). Pamięć trafia też do instrukcji systemowej: `IToolHost.SystemNote` → `ChatViewModel.BuildHistory`, jako dane (nie polecenia); wpis zapisany przez model nie może więc zmienić zasad, ale użytkownik widzi je w pliku (przycisk "Pamięć") i może je usunąć. Zapis pliku zawsze wymaga zgody (zgoda czatu albo `ConfirmToolCall`). Pliki: tylko ścieżki względne, bez `..`, dysków, strumieni NTFS (`:`) i dowiązań (junction/symlink w drodze do pliku jest odrzucane), bez zapisu plików wykonywalnych i skryptów (`.exe`, `.bat`, `.ps1`, `.lnk`...), odczyt do 100 tys. znaków, zapis do 1 mln, pliki binarne nie są czytane.

Układ projektów jak u dostawców: kontrakt `IChatToolGroup` (`ChatToolInfo`, `ToolArgs`) i `IMemoryStore` są w `ImageStudio.Tools.Core`, a każda grupa w osobnej bibliotece: `ImageStudio.Tools.DateTime` (`DateTimeTools`), `ImageStudio.Tools.Memory` (`MemoryTools`, tu też `SystemNote`) i `ImageStudio.Tools.Files` (`FileTools`: list, search, read, write). `BuiltinToolRegistry` (aplikacja) składa grupy, a `BuiltinTools` kieruje wywołania po nazwie i pyta o zgodę przed narzędziami z `Writes` (nazwy muszą być unikalne między grupami). DLL-e grup trafiają do podfolderu `tools\` (build i publish, jak `providers\` i `tabs\`; `PluginsInBundle` wkłada je do exe), a `App` wczytuje je przez `AssemblyLoadContext.Resolving`. Nowa grupa: projekt `ImageStudio.Tools.<Nazwa>` z referencją do `Tools.Core` (namespace `ImageStudio.Services`), klasa `IChatToolGroup`, wpis w `BuiltinToolRegistry`, referencje w `ImageStudio.csproj` (`Private="$(PluginsInBundle)"`), `ImageStudio.Tests.csproj` i `ImageStudio.slnx` oraz testy.

## Rozumowanie ChatGPT

`ChatGptProvider` deklaruje `reasoning_effort` dla modeli konta, chyba że katalog (`supported_reasoning_levels`) podaje pustą listę, i wysyła `reasoning: {effort, summary: "auto"}`; podsumowanie wraca jako `response.reasoning_summary_text.delta` do podglądu rozumowania. Model, który odrzuci parametr (błąd z "reasoning"), dostaje to samo żądanie ponowione bez niego.

## Lista grup narzędzi (włącz/wyłącz)

Panel "Narzędzia" w zakładce Czat pokazuje `ToolsViewModel.ToolGroups`: rozwijane grupy (zestawy wbudowane `datetime`/`memory`/`files`, "Sterowanie aplikacją" i każdy połączony serwer MCP, z `McpToolHost.ServerTools`), z polem przy grupie (włącza i wyłącza wszystkie jej narzędzia, stan częściowy = `null`) i polem przy każdym narzędziu. Wyłączone narzędzia nie trafiają do modelu (`Tools`), a gdyby model o nie poprosił, `CallAsync` odmawia; grupa bez włączonego narzędzia nie dopisuje też swojej notatki do instrukcji systemowej (`BuiltinTools.SystemNoteFor`). Stan to zbiór nazw wyłączonych narzędzi (`UserState.DisabledTools`, zapis przy każdej zmianie): nowe narzędzia, także z serwerów MCP, są domyślnie włączone. Przy pierwszym uruchomieniu (brak zapisu) zbiór wynika z dawnych flag: sterowanie aplikacją domyślnie wyłączone, wbudowane włączone; `AppControlEnabled` i `BuiltinEnabled` zostały jako właściwości pochodne ("choć jedno narzędzie grupy włączone", ustawienie zmienia wszystkie). Główny przełącznik "Pozwól modelowi używać narzędzi" (`UseTools`) wyłącza wszystko naraz, a bez niego model nie dostaje żadnych narzędzi.
