# Активная задача

Статус: в работе
Исходная задача: [T-008] Исправить S-09: некорректный {date:FMT} в .spectool.toml молча отключает "Редактировать"

Оценка сложности: средняя
Рекомендуемый effort: high

`src/SpecDesk.Core/Tokens.fs:32` (`ctx.Date.ToString(fmt, …)`) вызывается из
`WorkflowConfig.expandOrDefault` (`:63-68`), которая не обёрнута в `try/with` (только чтение TOML
обёрнуто). Например, `[branch] pattern = "spec/{docSlug}-{date:q}"` даёт `FormatException`, которое
проходит сквозь `branchNameForHost`/`commitMessageForHost`; catch в `OnEdit` фильтрует только
`LibGit2SharpException|InvalidOperationException`, поэтому "Редактировать" молча ничего не делает, а
`OnSuggestVersionNote`/`OnSuggestBranchName` вообще не отвечают (webview выжидает свои 30 секунд
таймаута). Подслучай: `{date:}` (пустой формат) разворачивается через "G" в
"07/04/2026 09:30:00 +00:00" — пробелы/двоеточия недопустимы в git-ref, но `expandOrDefault` это
пропускает (проверяет только остаточные `{` / пустую строку) → каждое "Редактировать" падает с общей
ошибкой. Это противоречит собственной документации модуля "invalid config must never break the
workflow".

Критерии готовности:
- Разворачивание `{date:FMT}` в `expandOrDefault` обёрнуто в такой же guard, какой уже есть в
  `ImageEngine.insertForHost`.
- Развёрнутый ref валидируется на допустимость символов git-ref (отклоняет пробелы/двоеточия и т.п.,
  включая случай пустого `{date:}`) до принятия; при ошибке — откат на паттерн по умолчанию.
- `OnEdit`/`OnSuggestVersionNote`/`OnSuggestBranchName` больше не молча зависают/no-op'ают на
  некорректном паттерне.
- Добавлены тесты на некорректный `{date:FMT}` и на `{date:}` (пустой формат).
- Запись в `CHANGELOG.md`.
