# UberLLM proxy: notatki i stan prac

Dokument zbiera diagnozę, wykonane zmiany, wyniki testów i otwarte kwestie. Klucz API jest tylko w `.env`, nigdy w tym pliku.

## 1. Problem wyjściowy

Unsloth Studio dostawał z proxy:

```
HTTP 400: venice/uncensored makes text, not images. Send it to /v1/chat/completions.
```

**Przyczyna:** proxy jest przezroczystym przekaźnikiem. Nie zmienia ścieżki ani body. Klient (Unsloth) wysłał `POST /v1/images/generations` z `model: "venice/uncensored"`, czyli modelem tekstowym. Błąd zwrócił upstream `https://api.uberllm.dev`.

`MODEL_TYPE` filtruje wyłącznie `GET /v1/models`. Nie wpływa na żądania generowania.

## 2. Stan proxy (`proxy.mjs`)

| Funkcja | Opis | Linia (ok.) |
|---|---|---|
| Wczytanie `.env` | `process.loadEnvFile`, jeśli plik istnieje. Zmienne z powłoki mają pierwszeństwo. | 10-11 |
| Klucz API | Jeśli klient nie wysłał `Authorization`, dokładany jest `Bearer ${UBERLLM_API_KEY}`. | 37 |
| Usunięcie `Expect` | curl dla dużych plików wysyła `Expect: 100-continue`; `fetch` tego nie obsługuje (502 `fetch failed`). | 36 |
| Logowanie | Jedna linia na żądanie: czas, metoda, ścieżka, `model=`, status. Przy 4xx/5xx treść błędu z upstreamu (do 1000 znaków). Przy błędzie połączenia `ERROR`. | 4-8, 49, 62-65, 72 |
| `MODEL_TYPE` | `image` (domyślnie) filtruje `/v1/models`. Dotyczy tylko listy modeli. | 16, 29 |
| `ONLY_AVAILABLE` | Domyślnie `true`: lista modeli zawiera tylko `available: true`. | 17, 56 |
| Port | `PROXY_PORT`, domyślnie `8866`. | 14 |

**Usunięte:** wymuszanie modelu przez `IMAGE_MODEL` dla `/v1/images/generations`. Usunięto je, bo blokowało wybór modelu w kliencie. Wpis `IMAGE_MODEL` nie występuje już w `.env`.

Proxy nie wpływa na zakładkę Obrazy w Unsloth Studio (patrz sekcja 5).

## 3. Pliki

- `proxy.mjs`: kod proxy.
- `.env`: `UBERLLM_API_KEY=` (klucz użytkownika). **Zawiera sekret.** Jeśli folder trafi do repozytorium, dodać do `.gitignore`.
- `proxy.log`: log uruchomienia w tle (tworzony przez `node proxy.mjs > proxy.log 2>&1`).
- `README.md`: oryginalna instrukcja. Podaje port `8765`, a kod domyślnie `8866`. Trzeba ustawić zgodnie z tym, co wpisane w Unsloth.

## 4. Uruchamianie

```powershell
cd C:\Users\przem\Downloads\uberllm-proxy
node .\proxy.mjs
```

Podgląd logu na żywo:

```powershell
Get-Content .\proxy.log -Wait
```

Lista modeli obrazowych:

```powershell
curl.exe http://127.0.0.1:8866/v1/models?type=image
```

## 5. Unsloth Studio: zakładka Obrazy

Źródło: `C:\Users\przem\.unsloth\studio\unsloth_studio\Lib\site-packages\studio\` (kod wewnątrz zainstalowanego Studio).

- Zakładki Create, Edit, Transform, Inpaint, Extend, Upscale, Reference są zawsze widoczne. Są aktywne tylko wtedy, gdy **załadowany lokalnie** model zgłasza odpowiedni tryb w `workflows`.
- Tooltip „The loaded model cannot do this” oznacza brak trybu w `workflows`.
- Listę `workflows` wylicza `_family_workflows` w `backend/core/inference/diffusion.py:15015`.
- Wymagania (`diffusion_families.py`):

| Zakładka | Wymóg modelu |
|---|---|
| Create | zawsze (txt2img) |
| Edit | `edit`, `unified_edit` (np. Qwen-Image-2.1) lub `reference_edit` |
| Transform | `img2img_pipeline_class` |
| Inpaint | `inpaint_pipeline_class` |
| Extend | inpaint z zachowaniem rozmiaru (`outpaint`) |
| Upscale | `img2img_pipeline_class` |
| Reference | flaga `reference` (rodzina FLUX.2) |

- Modele UberLLM z listy `/v1/models` **nie wpływają** na te zakładki. Zakładka Obrazy opiera się wyłącznie o lokalnie załadowany model.
- Zrzut pokazał, że aktywne było tylko Create, więc załadowany model jest prawdopodobnie txt2img-only.
- W cache HF są pliki `unsloth/Qwen-Image-2.1` (także FP8 i GGUF). Ten model ma `unified_edit = True`, więc powinien odblokować Edit po załadowaniu w Studio (nie testowane w UI).
- Create w kliencie nadal wysyła model z listy. Jeśli wpisany jest `venice/uncensored`, zwróci 400. Trzeba wybrać model obrazowy.

## 6. Edycja obrazu przez API

Działający przykład (proxy uruchomione):

```powershell
curl.exe -X POST http://127.0.0.1:8866/v1/images/edits `
  -F "model=venice/grok-imagine-edit" `
  -F "prompt=OPIS ZMIANY" `
  -F "image=@747614082_18057541964770098_7004108407124322667_n.jpg"
```

Odpowiedź: JSON z `data[0].url` (plik obrazu do pobrania z nagłówkiem `Authorization`) oraz `usage.cost_usd`.

Modele edycji z listy `type=image` (`endpoints` zawiera `/v1/images/edits`, `input_modalities` zawiera `image`):

- `venice/grok-imagine-edit` (przetestowany, ok. $0.012)
- `google/nano-banana-2-lite-edit`
- `qwen/qwen-image-2-edit`
- `venice/gpt-image-2-edit` (droższy)
- `bytedance/seedream-v5-lite-edit`
- `black-forest-labs/flux-2-max-edit`

Modele generowania (`/v1/images/generations`) to m.in. `venice/z-image-turbo` (ok. $0.004), `venice/chroma`, `venice/flux-2-pro`, `venice/gpt-image-2`. Wiele z nich ma `input_modalities: ["text"]`, więc nie przyjmują obrazu.

Uwaga: `/v1/images/edits` wysyła `multipart/form-data`. Proxy przekazuje body bez zmian.

## 7. Wyniki testów

Wszystkie testy na prawdziwym API, chyba że zaznaczono inaczej.

| Test | Wynik |
|---|---|
| `GET /v1/models` przez proxy | 200, lista z `type=image` |
| `POST /v1/images/generations` bez klucza | 401 `missing_api_key` (proxy przekazuje, auth po stronie upstreamu) |
| `POST /v1/images/generations` z `venice/uncensored` i kluczem w `.env` (wcześniej z rewrite) | 200, obraz PNG 256×256 |
| `POST /v1/chat/completions` z `venice/uncensored` | 200, odpowiedź „OK” |
| `POST /v1/images/edits` (curl, plik 1 MB) przed poprawką `Expect` | 502 `fetch failed` |
| Ten sam request po poprawce | 200, JPEG 1280×720 |
| Logowanie przy nieistniejącym modelu | 404 `model_not_found`, treść zapisana w logu |
| Test ze stubem (bez kosztów) | rewrite i przekazanie klucza działały zgodnie z oczekiwaniem |

Koszt testów przy prawdziwym API: ok. $0.036 (jedna generacja, dwie edycje, jedna przez Node).

## 8. Znane ograniczenia i otwarte kwestie

- **Obrazy w Unsloth:** nie zweryfikowano w UI, czy Edit odblokuje się po załadowaniu `Qwen-Image-2.1`.
- **Klient nie wysłał jeszcze `/v1/images/edits`:** w logu jest tylko `GET /v1/models` i jedno generowanie z `qwen/qwen-edit-uncensored` (200). Brak żądania edycji oznacza, że klient nie używa tego endpointu w tym scenariuszu.
- **Port:** README podaje `8765`, kod `8866`. Ustawić zgodnie z Unsloth.
- **Koszty:** każda generacja i edycja jest płatna w koncie UberLLM.
- **`IMAGE_MODEL` usunięty:** jeśli klient wysyła model tekstowy do generowania, dostanie 400. To celowe, żeby wybór był po stronie klienta.
- **Sekret w logach:** logi proxy nie zawierają klucza, ale testy ze stubem wypisywały nagłówek `Authorization` w outputach narzędzi. Rozważyć rotację klucza, jeśli te outputy były gdziekolwiek udostępniane.
- **Brak testów automatycznych:** zmiany sprawdzano ręcznie i przez `node --check`.

## 9. Co dalej (opcjonalnie)

- Skrypt `edit-image.ps1` do wygodnych edycji z zapisem wyniku na dysk.
- Dopisać do `README.md` zmienne `UBERLLM_API_KEY`, `PROXY_PORT`, `MODEL_TYPE` i opis logów.
- Dodać `.env` do `.gitignore`, jeśli folder będzie w repozytorium.
