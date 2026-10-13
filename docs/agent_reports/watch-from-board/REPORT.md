# watch-from-board — отчёт

Статус: РЕАЛИЗОВАНО НО НЕ ПРИНЯТО

## Что сделано

- На **Закупе** и в **Портфеле**: колонка чекбоксов слева, «выбрать все» в шапке, кнопка **«В слежение»** в тулбаре.
- `POST /api/watch/batch` — пакетное добавление на указанный хаб (UI шлёт Jita 4-4).
- Уже в слежении на закупе: `watched` → чекбокс checked+disabled.
- Ручной ввод typeId на вкладке Слежение оставлен.
- Publish: `publish\`, exe перезапущен. Проверка: batch 200, два typeId добавлены и сняты.

## Файлы

- `Program.cs` — `WatchBatchRequest`, `/api/watch/batch`
- `wwwroot/index.html` — UI + JS
- `docs/SKELETON.md`, `docs/OPISANIE.md`
