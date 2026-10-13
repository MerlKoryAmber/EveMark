# haul-ask-age

## Запрос
Забирать возраст ордера (`issued`) и встроить в расчёт list / «лучшее».

## Сделано
- ESI `OrderDto.issued` → `Book.BestSellIssued` / `BestBuyIssued` (самый старый на цене best).
- `location_quote.best_sell_issued`, `best_buy_issued` (миграция `EnsureColumn`).
- List: `sell_post = min(best_sell−tick, max avg истории за hl_list_hist_days=14)`; если возраст ask > `hl_list_max_ask_age_days=7` — только история (без истории list нет).
- Корзина: колонка «Возраст ask»; CSV `ask_age_days`.
- Страховки list сохранены: V≥10, ≥3 sell, вилка≤2×, ≤1.5×ask.
- `--test-haul` OK (`list_age`).

## Проверка (живой снимок)
- `location_quote`: 35935 строк, все с `best_sell_issued`.
- Aoede Hek: sell 2.11B, issued **2026-09-18** (~25 сут) → в корзине dodixie→hek **нет**.
- list-строки: 16/16 с `ask_age_days`; max в корзине 6.5д (&lt;7).
- `--test-haul` OK.

## Добивка «остального»
- Потолок list: мин. `best_sell` других хабов (`NeighborSellCap`).
- UI: `hl-ask-age`, `hl-hist-days` в «Курьерка и прочее»; сохранение в `hl_*`.
- Страховка `MaxListAskMultiple` = 2 (после возраста/истории/соседей).
- `OPISANIE.md` / скелет обновлены.
- Проверка: `--test-haul` OK; settings age=7 hist=14; Aoede не в корзине; UI поля на месте; stale list (>7д) идёт с ценой от истории, не с 2B ask.

## Статус
РЕАЛИЗОВАНО НО НЕ ПРИНЯТО.
