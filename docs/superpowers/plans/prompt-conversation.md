# Rozmowa przy przebudowie promptu

1. Osadzić w edytorze osobną, tymczasową rozmowę opartą na istniejących `ChatViewModel` i widoku czatu: model, strumień, Stop, załączniki, narzędzia i zgody korzystają z obecnej mechaniki. Nie zmieniać ani nie zapisywać sesji głównego czatu.
2. Przekazać aktualne pola promptu jako kontekst i umożliwić wiele tur rozmowy. Zdefiniować sposób przekazania kompletnej propozycji `system`, `concept`, `negative`; rozmowa może zawierać zwykły tekst. Zastosowanie odbywa się wyłącznie na jawne żądanie użytkownika, po istniejącej walidacji. Błędy, niekompletna odpowiedź, przerwanie i anulowanie nie zmieniają pól.
3. Udostępnić włączone narzędzia i istniejące zasady zgód. Wyłączyć wyłącznie narzędzie `set_prompt` w tej rozmowie (także przy próbie bezpośredniego wywołania), żeby nie zmieniało głównego okna przed zatwierdzeniem; nie ograniczać pozostałych.
4. Zachować zachowanie edytora, ukrytych pól, historii/cofania, presetów i istniejących klientów `rebuild_prompt`. Zamknięcie okna ma anulować trwającą pracę i nie pozostawiać podpiętych zdarzeń ani oczekujących zgód.
5. Dodać tłumaczenia PL/EN, dokumentację oraz testy izolacji, wyboru i walidacji wyniku, zgód, blokady narzędzia, cofania, anulowania i istniejących przepływów. Sprawdzić build, testy i kontrolę tłumaczeń.
