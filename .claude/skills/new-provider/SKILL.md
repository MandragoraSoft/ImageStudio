---
name: new-provider
description: Dodawanie nowego dostawcy AI do ImageStudio (projekt ImageStudio.Providers.*, IAiProvider) albo nowej metody w kontrakcie IAiProvider (np. nowa funkcja dźwięku, wideo, czatu). Użyj przy prośbie o nowego dostawcę, nowy endpoint API lub nową możliwość dostawcy.
---

# Nowy dostawca albo nowa metoda kontraktu `IAiProvider`

Przeczytaj najpierw `docs/dev/providers.md` (kontrakt, przeładowanie katalogu, domyślny model tekstowy, żądanie wideo, dźwięk). Wzór dostawcy z prostym API w formacie OpenAI: `ImageStudio.Providers.OpenAi`; wzór z bogatym katalogiem i schematami modeli: `ImageStudio.Providers.UberLlm`. Wspólny kod formatu OpenAI: `ImageStudio.Providers.Core` (`OpenAiCompat`, `OpenAiAudio`).

## Nowy dostawca

1. Projekt `ImageStudio.Providers.<Nazwa>` z referencją do `ImageStudio.Providers.Core`; namespace zostaje `ImageStudio.Services` (XAML odwołuje się do niego z `;assembly=ImageStudio.Providers.Core`).
2. Klasa implementująca `IAiProvider`: `Id`, `DisplayName`, `DefaultTextModel` (stała `DefaultTextModelId`), modele i cennik, `GenerateImageAsync` itd. Czego dostawca nie obsługuje, zostawia na domyślnych implementacjach (puste listy, `NotSupportedException`).
3. Wpis w `ImageStudio/Services/ProviderRegistry.cs`: pozycja w `Available` (id, nazwa, domyślny model tekstowy) i gałąź w `Create`. `ProviderFactory` sam opakowuje każdego dostawcę w `ActivityAiProvider` (spinner w stopce), więc dekoratora nie dodajesz ręcznie. W `Create` przekaż dostawcy `handlerFactory` (handlery z `OutgoingRequestRegistry`): każdy `HttpClient`, także pomocniczy, dostaje osobny handler, a klient pobierający pliki nie ma nagłówka autoryzacji.
4. Widok ustawień dostawcy w `ImageStudio/Views/Providers` (wzór: `OpenAiSettingsView`) i pola w `AppSettings`/`SettingsResult`, jeśli potrzebne są nowe adresy i klucze (klucze trafiają do `ISecretStore`, nie do pliku ustawień). Okno Ustawień wykrywa widok po typie (`ProviderSettingsTabView`).
5. Referencja `Private="$(PluginsInBundle)"` w `ImageStudio/ImageStudio.csproj` (publikacja do jednego pliku wkłada dostawców do exe; zwykły build układa je w `providers\`), referencja w testach, wpis w `ImageStudio.slnx`.
6. Katalog: jeśli dostawca ma własną pamięć podręczną, nadpisz `ReloadAsync` (najpierw czyści cache); częściowy błąd ląduje w `Warnings`, nie przerywa.
7. Testy: dostawca na lokalnym serwerze `HttpListener` (wzór: `AudioProviderTests`, `RejectedParameterTests`; porty przez `TestPorts.Free()`), opcjonalnie `PricingTests`.

## Nowa metoda w `IAiProvider`

Wszystkie punkty są obowiązkowe; brak któregoś kończy się cichym błędem (np. dekorator nie przekazuje domyślnych metod interfejsu):

1. Metoda w `IAiProvider` **z domyślną implementacją** (dostawca bez tej funkcji nie musi nic robić). Katalogi modeli dołóż też do `ProviderCatalog` (parametry opcjonalne na końcu) i do domyślnego `ReloadAsync`.
2. **`ActivityAiProvider`**: jawne przekazanie metody z etykietą dla stopki. Dekorator nie dziedziczy domyślnych metod, więc bez tego wywołanie nie pokaże spinnera, a przy delegowaniu dostanie domyślną implementację zamiast prawdziwej.
3. Implementacja w dostawcach, którzy tę funkcję mają. Uwaga: domyślne metody interfejsu są widoczne tylko przez typ `IAiProvider`, nie przez klasę dostawcy (testy rzutuj na `IAiProvider`).
4. **`FakeProvider`** w `ImageStudio.Tests/Fakes.cs` (konfigurowalne odpowiedzi i zapamiętanie ostatniego wywołania) i testy ViewModelu.
5. Żądania sieciowe idą przez wspólny kod, gdy format jest zgodny z OpenAI; formaty odpowiedzi przebudowy wybieraj przez `PromptResponseMode`. Ponawianie obsługuje wspólny kod po rozpoznaniu statusu i jednoznacznego odrzucenia formatu (`ApiErrors.RejectsResponseFormat`).
6. Dokumentacja w `docs/dev/providers.md`.
