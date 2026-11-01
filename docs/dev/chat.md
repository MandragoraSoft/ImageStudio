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

`ChatViewModel` + `ToolsViewModel` jako klient narzędzi MCP (`McpToolHost`, konfiguracja w `McpConfig`). Osobno `McpAppServer` (lokalny serwer MCP) wystawia sterowanie aplikacją przez `IAppControl` / `AppControlTools`, implementowane przez `MainViewModel.AppControl`.

## Markdown w odpowiedziach

Odpowiedzi modelu (`ChatMessage.IsAssistant`) pokazuje `MarkdownView` (`RichTextBox` tylko do odczytu), a tekst użytkownika i narzędzi zostaje w zwykłym `TextBox`. `MarkdownRenderer` (Markdig) zamienia Markdown na `FlowDocument`: akapity, nagłówki, bloki kodu (Consolas, bez znaczników ```), listy, cytaty, tabele, listy zadań, pogrubienia, linki. Dokument budowany jest od nowa przy każdej zmianie tekstu (kawałki strumienia). Kolory idą przez `SetResourceReference` (motyw działa bez odświeżania), linki otwierają tylko `http(s)`, a obrazy z sieci nie są pobierane. Wyłączona lista (np. sesje w czasie odpowiedzi) nie może być biała: `ListBox` w `App.xaml` ma własny szablon, bo domyślny maluje stan wyłączony na biało.

## Wbudowane narzędzia czatu (builtin__*)

`BuiltinTools` (`Services`) działa obok serwerów MCP i narzędzi aplikacji (`ToolsViewModel` składa trzy źródła; przełącznik `BuiltinEnabled`, zapis w `UserState.BuiltinToolsEnabled`, domyślnie włączone). Nazwy dla modelu mają przedrostek `builtin__`. Zestaw: `get_datetime` (data, godzina, dzień tygodnia, opcjonalna strefa), `memory_save` / `memory_list` / `memory_delete` (pamięć między sesjami w `memory.json` za `IMemoryStore`, do 200 wpisów po 500 znaków, bez duplikatów) oraz `files_list` / `files_search` (po nazwie i treści, wzorzec `*`/`?`, do 50 wyników i 5 tys. plików, bez dowiązań, plików binarnych i większych niż 2 MB) / `files_read` / `files_write` w jednym folderze roboczym (`Dokumenty\ImageStudio\Workspace`). Pamięć trafia też do instrukcji systemowej: `IToolHost.SystemNote` → `ChatViewModel.BuildHistory`, jako dane (nie polecenia); wpis zapisany przez model nie może więc zmienić zasad, ale użytkownik widzi je w pliku (przycisk "Pamięć") i może je usunąć. Zapis pliku zawsze wymaga zgody (zgoda czatu albo `ConfirmToolCall`). Pliki: tylko ścieżki względne, bez `..`, dysków, strumieni NTFS (`:`) i dowiązań (junction/symlink w drodze do pliku jest odrzucane), bez zapisu plików wykonywalnych i skryptów (`.exe`, `.bat`, `.ps1`, `.lnk`...), odczyt do 100 tys. znaków, zapis do 1 mln, pliki binarne nie są czytane.
