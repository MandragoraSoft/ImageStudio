# Dopasowanie czatu do możliwości modelu

1. Zachować w katalogach rozróżnienie: obsługa narzędzi potwierdzona, odrzucona lub nieznana. Uwzględnić jawne `supports_tools` UberLLM i obsługę obrazów; nie uznawać braku danych za potwierdzenie.
2. Po wyborze modelu aktualizować panel narzędzi i stan wysyłania na podstawie dostępnych metadanych. Dla braku lub nieznanej obsługi wyłączyć narzędzia i wyjaśnić przyczynę, bez zmieniania zapisanych wyborów użytkownika.
3. Nie wysyłać definicji narzędzi do modelu bez potwierdzonej obsługi. Zachować ważną historię rozmowy po przełączeniu modelu, w tym wcześniejsze wywołania i wyniki narzędzi, bez wykonywania ich ponownie.
4. Gdy model nie obsługuje obrazów, zablokować wysyłanie z załączonym obrazem, zachowując tekst i załączniki; model z nieznaną obsługą obrazów zachowuje dotychczasowe działanie.
5. Zastosować zasady także w rozmowie edytora promptu. Dodać testy katalogów, przełączania modeli, historii narzędzi, obrazów i widoków, zaktualizować tłumaczenia oraz dokumentację.
