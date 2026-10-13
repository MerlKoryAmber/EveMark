# haul-titles-best

## Запрос
Подписи столбцов рейса (не спрашивать каждый раз), сортировка, откуда цифры режима «лучшее».

## Причина раздутых цифр
Режим `best` по типу берёт instant или list с большей прибылью. List ставил sell по `best_sell_B − tick`. Без полного стакана одиночный sell на B бывал в тысячи раз выше ask на A (Scale Model 3.5M → 9.99B). Плюс глубина бралась как `max(touch, volume)` вместо touch.

## Правки
- `HaulPlanner.MaxListAskMultiple = 3`: list пустой, если sell_post > 3× ask.
- Глубина у лучшей цены: sell_touch / buy_touch (как на доске).
- List только при `volume_day_B ≥ 1` и `sell_orders ≥ 2`.
- Skip: blacklist + `reject`.
- UI: `haulHint` + title на столбцах/режиме; подсказка под фильтрами; клик — сортировка.
- `--test-haul`: `list_fat` OK. Live: top best ~1.66B profit / ROI ~166% (было ~18B / 1877%).

## Дополнение: days_to_sell
Баг: в списке рейсов `days_to_sell` всегда 0 (считался только при полной корзине). «150 лет» — list qty без жёсткой сверки с горизонтом / формула qty/(V×share) на огромном qty.
Исправление: считать дни из корзины всегда; `TrimListToHorizon`; clamp ≤ `hl_list_horizon_days`; в UI `toFixed(1)` или «—». Live list: max days=3.0, over_horizon=0.

## Съезд столбцов («Продажа, дн» = миллионы)
Причина: `applyColumns(view-haul)` + общее меню столбцов доски прятало `th[data-k=jumps]` и на рейсе, а новые `td` оставались — под «Продажа, дн» оказывался ISK/час (~40M).
Фикс: `applyColumns` по каждой `table`; меню/apply только `#haul-board-pane` / `#haul-table`; после `renderTripsRows` снять `col-off` с рейса. Проверка: before jumpsTh=none + days показывает час; after days=`—`, map выровнен.

## «Лучшее» / лазер 2.1B на Hek
Источник цены: `sell_post = best_sell_Hek − tick` (OCR: Abyssal → в БД Aoede Mining Laser Upgrade). Buy Dodixie ~854M, sell Hek 2.109B (×2.47), volB=2 — одиночный ask, не «купят за 2B». Потолок 3× это пропускал.
Ужесточение list: ≤1.5× ask; V_B≥10; ≥3 sell; best_sell/best_buy≤2. Live dodixie→hek best: laser_rows=0, list_over_1.5x=0, profit ~70M (было ~1.3B).

## Статус
РЕАЛИЗОВАНО НО НЕ ПРИНЯТО. Exe+index в publish — Ctrl+F5.
