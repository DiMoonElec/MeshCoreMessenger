# macOS: запуск native UI audit при недоступном дисплее

Анализ 04.10.2026, отчёт пользователя `tmp/app_fail.txt` (локальный, не коммитится).
Падал процесс `MeshCoreMessenger.ViewportAudit`, примерно через 0,65 секунды
после запуска, с `EXC_CRASH (SIGABRT)`. Стек содержит завершение процесса .NET
после managed exception; самого текста исключения macOS-отчёт не содержит.

В предыдущих запусках этой утилиты консоль показывала
`Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661`
при `AppBuilder.SetupWithoutStarting`. Сопоставление с отчётом — вероятная причина,
а не извлечённый из crash report текст исключения. CoreVideo определяет -6661
как `kCVReturnInvalidArgument`. В Avalonia есть [сообщение о таком же сбое при
недоступном дисплее](https://github.com/AvaloniaUI/Avalonia/issues/18895).

По словам пользователя, дисплей мог быть погашен тайм-аутом, а экран заблокирован.
Это согласуется с гипотезой недоступного display link. Запись `Display ... Online`
в crash report не доказывает, что дисплей был бодрствующим. Влияние блокировки
отдельно не установлено; контролируемое сравнение awake/asleep/locked не проводилось.

## Защита диагностической утилиты

Перед инициализацией Avalonia на macOS выполняется read-only preflight:
`CGMainDisplayID`, `CGGetActiveDisplayList`, создание и немедленное освобождение
`CVDisplayLinkCreateWithActiveCGDisplays`. Display link не запускается; экран
не пробуждается, блокировка и настройки питания не изменяются. При недоступности
link утилита печатает результат и завершает работу с кодом 1.

Весь managed startup/execution теперь охвачен try/catch: если дисплей станет
недоступен после preflight или возникнет другая managed ошибка, она попадёт в stderr,
а утилита вернёт 1. Это не защита от native faults и не исправление Avalonia.
Успешный preflight не гарантирует успешность последующих UI проверок.
Production Desktop startup и зависимости не изменены.

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --startup-check-only
```

## Проверка 04.10.2026

- Release сборка и `--startup-check-only`: exit 0,
  `main=1, active=1, CoreGraphics=0, CVDisplayLink=0`.
- Следом `--history-clear-only`: exit 0, все восемь сочетаний
  Public/Private × Light/Dark × 420/960 прошли cancel/confirm и сохранение черновика.
  Screenshots: `$TMPDIR/meshcore-history-clear/`.
- Только временная SQLite; аппаратная нода и пользовательская БД не использовались.
- Ветка отказа при погашенном/заблокированном дисплее не воспроизводилась намеренно;
  состояние экрана при проверке не менялось.
