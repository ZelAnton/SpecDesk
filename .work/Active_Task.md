# Активная задача

Статус: в работе
Исходная задача: [T-004] Исправить S-04: запрос app:// с закодированным NUL валит процесс

Оценка сложности: средняя
Рекомендуемый effort: high

`src/SpecDesk.Host/AppAssetResolver.cs:34` декодирует `uri.AbsolutePath` (`%00` → `\0`), а `:51`
передаёт результат в `Path.GetFullPath`, который бросает `ArgumentException` на встроенном null.
`Program.cs:100-123` (`ServeAsset`) перехватывает только `IOException`/`UnauthorizedAccessException`,
несмотря на собственный комментарий "must never let an exception escape into the message pump".
Спецификация с `![x](a%00.png)` при рендере вызывает запрос `app://repo/a%00.png` — исключение проходит
через обратный P/Invoke-колбэк WebView2 и валит процесс. Это DoS из содержимого документа, приходящего
извне (например, из шаренного репозитория).

Критерии готовности:
- `ServeAsset` (или `AppAssetResolver`) перехватывает/предотвращает некорректные символы в пути
  (встроенный NUL и т.п.), а не только `IOException`/`UnauthorizedAccessException`.
- Запрос `app://repo/a%00.png` возвращает безопасный ответ об ошибке, а не валит процесс.
- Добавлен тест на некорректные символы пути и тест "ServeAsset никогда не бросает исключение наружу".
- Запись в `CHANGELOG.md`.
