# Centralna obsługa żądań sieciowych

1. Ustalić punkty wstrzyknięcia transportu wszystkich klientów HTTP aplikacji, w tym dostawców, logowania, magazynów chmurowych, pobierania obrazów i MCP. Nie przechwytywać ruchu zewnętrznej przeglądarki ani procesów MCP.
2. Wprowadzić wspólny, wstrzykiwany rejestr wychodzących żądań z osobnym tokenem anulowania dla każdego żądania. Zapewnić współbieżność, bezpieczne usuwanie wpisów, brak ujawniania danych z URL lub nagłówków i zachowanie tokenów oraz limitów wywołującego.
3. Utrzymać rejestrację i możliwość anulowania także podczas odczytu treści oraz strumienia odpowiedzi; zwalniać zasoby po zakończeniu, błędzie i anulowaniu. Zachować obecny tracker operacji logicznych oraz istniejące Stop czatu.
4. Podłączyć centralny transport we wszystkich ścieżkach i rozróżnić anulowanie przez użytkownika od timeoutu; nie przerywać niepowiązanych operacji i nie gubić synchronizacji chmury.
5. Umieścić w stopce spinner i listę aktywnych żądań z oddzielnym przyciskiem anulowania, z lokalizacją PL/EN i aktualizacjami na wątku UI. Nie duplikować wizualnie istniejącego wskaźnika operacji logicznych.
6. Dodać testy transportu, odczytu strumieniowego, równoległych żądań i selektywnego anulowania, integracji klientów oraz stopki. Zaktualizować dokumentację i uruchomić testy oraz kontrolę tłumaczeń.
