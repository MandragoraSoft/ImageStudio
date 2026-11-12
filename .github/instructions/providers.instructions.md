---
name: 'Dostawcy AI'
description: 'Kontrakty, katalogi modeli i HTTP dostawców ImageStudio.'
applyTo: 'ImageStudio.Providers.*/**, ImageStudio/Services/ActivityAiProvider.cs, ImageStudio/Services/ProviderRegistry.cs'
---

# Dostawcy AI

- Przeczytaj `docs/dev/providers.md`. Przy nowym dostawcy lub metodzie `IAiProvider` użyj skilla `new-provider`, jeśli jest dostępny.
- Zachowuj wspólny kontrakt: metoda interfejsu z implementacją domyślną, jawne przekazanie przez `ActivityAiProvider`, implementacje wspierających dostawców i `FakeProvider` oraz odpowiednie testy. Domyślne metody interfejsu wywołuj przez typ `IAiProvider`.
- Jeśli API jest zgodne z OpenAI, wykorzystuj wspólny kod w `OpenAiCompat`; błędy HTTP twórz przez `ApiErrors.Failure`. Nie ukrywaj błędów ani anulowania użytkownika jako sukcesu lub zwykłego timeoutu.
- Metadane możliwości modeli zachowują trzy stany: `true`, `false`, `null` (brak potwierdzenia). Nie włączaj narzędzi dla nieznanej obsługi i nie zakładaj obsługi obrazów lub parametrów bez podstaw w kontrakcie dostawcy.
- Utrzymaj przeładowywanie katalogu i unieważnianie pamięci podręcznej (`ReloadAsync`), częściowe ostrzeżenia, ceny oraz właściwe zachowanie po zmianie dostawcy.
- Klientom HTTP dostarczaj osobne handlery z `OutgoingRequestRegistry`; nie przekazuj autoryzacji dostawcy klientom pobierającym pliki z obcych adresów.
- Testuj żądania i odpowiedzi na atrapach lub serwerze lokalnym, bez używania prawdziwych kluczy.
