---
name: 'WPF i interfejs'
description: 'Zasady widoków WPF, motywów, selektorów i lokalizacji interfejsu ImageStudio.'
applyTo: '**/*.xaml, **/*.xaml.cs'
---

# Widoki WPF

- Teksty widoczne użytkownikowi dodawaj do obu słowników `ImageStudio.Shared/Localization/Strings.pl.json` i `Strings.en.json`, z tymi samymi parametrami. W XAML używaj `{loc:T klucz}`, w kodzie `Loc.T(...)`; zachowaj zmianę języka bez restartu.
- Selektor modeli to `ModelPicker` z `HasFilters="True"` i osobnym `ICollectionView`/filtrem z `MainViewModel`. W `ComboBox` z obiektami stosuj `ItemTemplate`, nie `DisplayMemberPath`.
- Nowe okno używa `Style="{StaticResource WindowBase}"`. Nowe kolory dodawaj do obu palet (`ThemeService` i `App.xaml`); sprawdź kontrast w obu motywach.
- W XAML dla typów z innego projektu dodawaj `;assembly=...`. Odwołania do wtyczek zakładek i dostawców zachowują `Private="$(PluginsInBundle)"`.
- Przy nowej zakładce głównej lub Ustawień użyj skilla `new-main-tab`, jeśli jest dostępny; w przeciwnym razie przeczytaj `docs/dev/tabs-and-settings.md` i sprawdź rejestrację, `DataTemplate`, filtry, indeksy i testy.
- Przeczytaj `docs/dev/ui-shell.md` i `docs/dev/localization.md` dla zmian w odpowiednim obszarze. Sprawdź tłumaczenia przez `pwsh -NoProfile -File tools/Check-Translations.ps1 -Strict`.
