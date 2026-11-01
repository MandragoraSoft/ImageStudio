# Przebudowa promptu przez AI i zestawy (presety)

_Kiedy czytać: dotykasz edytora promptu, przebudowy promptu, opisu obrazu przez AI albo zestawów._

## Przebudowa promptu przez AI

(`IAiProvider.EnhancePromptAsync` -> `OpenAiCompat.EnhanceAsync`): prompt ma dwie osobne części, `system` (konfiguracja generowania: jak wykonać) i `concept` (oczekiwany wynik: co pokazać). Model odpowiada JSON-em o stałej strukturze `{system, concept, changes}`; OpenAI i OpenRouter dostają `response_format` (z ponowieniem bez niego, gdy endpoint go odrzuci), UberLLM nie, bo API odrzuca nieznane klucze. Poprzednie przebudowy (`PromptRevision`, ostatnie 6) są dołączane jako kontekst rozmowy; historia żyje w `PromptEdit.History` i w `MainViewModel` osobno dla zakładek Generowanie i Edycja.

## Zestawy (presety)

To pliki JSON w katalogu użytkownika (`PresetStore`, rodzaj `generate` albo `edit`). Edytor promptu (`PromptEditorWindow`) dostaje listę zestawów i delegat zapisu jako parametry (nie zna `PresetStore`). Okno zapisu zestawu proponuje nazwę i opis przez AI (`PresetSaveViewModel`). Edytor promptu ma też "Opisz obraz (AI)" (delegat `describeImages` z `IDialogService.ShowPromptEditor`; `MainViewModel.DescribeImagesAsync` wysyła obrazy zakładki albo wskazane pliki do modelu tekstowego z obsługą obrazów).
