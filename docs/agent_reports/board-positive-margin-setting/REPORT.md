# Галка «Только плюсовая маржа» на закупе

## Что сделано
- `setting.board_positive_margin` (по умолчанию `1`) → `FeeSettings.BoardPositiveMargin`.
- Фильтр `margin ≤ 0` снят из `LoadRankedAsync`; применяется только в `GetBoardAsync` при включённой галке.
- Портфель явно оставляет `MarginIsk > 0`. Сигнал/развоз не трогали.
- UI: Настройки → «Доска закупа». Скелет и OPISANIE обновлены.
- Publish + проверка: при `false` type `96789` Mutated Blood Sample виден с `marginIsk ≈ -3399`.

## Статус
РЕАЛИЗОВАНО НО НЕ ПРИНЯТО.
