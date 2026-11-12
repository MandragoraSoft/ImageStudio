# Kontrakt odpowiedzi strukturyzowanych dla przebudowy promptu

1. Zdefiniować w warstwie dostawców wspólny kontrakt odpowiedzi przebudowy i tryby: ścisły schemat JSON, obiekt JSON oraz instrukcja tekstowa. Zachować istniejący publiczny kontrakt przebudowy promptu i zgodność zapisanej historii.
2. Przygotować jeden schemat oraz wspólną walidację odebranych pól. Pola `system`, `concept` i `negative` muszą być tekstowe, a `concept` niepusty; `changes` pozostaje opcjonalne dla istniejących integracji. Ścisły schemat może wymagać `changes`, jeśli wymaga tego API.
3. Przypisać tryby dostawcom bez zakładania obsługi schematu dla dowolnego modelu lub serwera zgodnego z OpenAI. Użyć ścisłego schematu wyłącznie w potwierdzonej konfiguracji oficjalnego API OpenAI i modelu, który go obsługuje. Pozostałe dotychczasowe tryby zachować.
4. Ponawiać żądanie z mniej restrykcyjnym formatem tylko po jednoznacznym błędzie odrzucenia formatu przez serwer; nie ponawiać błędów transportu, autoryzacji, limitów ani błędnego schematu. Zawsze walidować wynik lokalnie i ujawniać błędy.
5. Rozszerzyć testy żądań, wyboru trybu, poprawnych i błędnych odpowiedzi oraz warunków ponawiania. Zaktualizować dokumentację dostawców i przebudowy. Uruchomić testy oraz kontrolę tłumaczeń i diffu.
