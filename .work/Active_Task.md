# Активная задача

Статус: в работе
Исходная задача: [T-002] Исправить S-02: не-GitHub push-эндпоинт получает Windows SSO-креды

Оценка сложности: средняя
Рекомендуемый effort: high

`src/SpecDesk.Git/LibGit2DocumentVersioning.cs:238-241`. Колбэк credentials возвращает
`new DefaultCredentials()` для любого URL, кроме `github.com` — это `GIT_CREDENTIAL_DEFAULT` (Windows
Negotiate/NTLM текущего пользователя), что прямо противоречит соседнему комментарию ("gets no
credential"). Если `.git/config` `pushurl` репозитория (полученного, например, как zip/расшаренная
папка) указывает на хост атакующего, "Отправить на проверку" инициирует NTLM-хэндшейк от имени автора —
NetNTLMv2 challenge/response может быть перехвачен или релеен.

Критерии готовности:
- Колбэк credentials возвращает `null` (или бросает исключение) вместо `DefaultCredentials()` для
  не-GitHub URL.
- Push на не-GitHub URL корректно и безопасно завершается неудачей (NTLM-хэндшейк не инициируется).
- Поведение push на GitHub не изменилось.
- Добавлен тест: push на не-GitHub URL безопасно проваливается.
- Комментарий у соответствующего кода приведён в соответствие с реальным поведением.
- Запись в `CHANGELOG.md`.

Затрагиваемый файл: `src/SpecDesk.Git/LibGit2DocumentVersioning.cs`. Обрати внимание: в этом же файле
недавно (задача T-001) уже был доработан метод `PushBranch` — добавлена проверка `OnPushStatusError` /
`ThrowIfRejected`. Учитывай текущее состояние файла, а не только описание из находки.
