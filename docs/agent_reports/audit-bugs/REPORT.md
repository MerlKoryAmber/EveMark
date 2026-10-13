# АУДИТ багов — независимые агенты

Дата: 13.10.2026 МСК. Статус: **РЕАЛИЗОВАНО НО НЕ ПРИНЯТО** (P0 + F5 + часть P1). Приёмки нет.

Три независимых субагента (UI/API, портфель/рейс/watch, доска/сигналы). Детали UI: `docs/agent_reports/audit-ui-api-settings/REPORT.md`.

## P0

| # | Что | Где | Воспроизвести | Статус |
|---|-----|-----|---------------|--------|
| 0 | F5 всегда открывает Закуп, а не последнюю вкладку | `index.html` showView / boot; hash только signal/watch | Сигнал/Портфель → F5 → Закуп | исправлено |
| 1 | Портфель: форма не гидрируется из БД; каждый `loadPortfolio` → `savePortfolioSettings` пишет HTML-дефолты поверх `pf_*` | `index.html` loadPortfolio / savePortfolioSettings | Капитал ≠ 1e9 → F5 → Портфель → снова 1e9 | исправлено |
| 2 | Watch: после remove+add тот же type+region даёт 304 по старому ETag → `watch_order` пуст, fills/movers молчат | `WatchPollService`, `MarketDb.Watch` etag | Слежение → опрос → удалить → добавить → 304 → пустая книга | исправлено |
| 3 | Закуп: фильтр «Часов» до `FillHoursAsync` (Hours=0) — `hoursMin>0` обнуляет доску | `MarketDb.GetBoardAsync` | Закуп → Часов от 1 → пусто | исправлено |

## P1

| # | Что |
|---|-----|
| 1 | Сигнал «Дёргают»: Нетто — sort/filter UI есть, API/`SortKey` нет | исправлено |
| 2 | Закуп: Конкур./Доля/Станция 24ч сорт → fallback `dailyIsk` | исправлено |
| 3 | Haul settings: POST затирает `gank*` / `minUnitPrice` / `courierMaxM3` / `maxMarketShare`; race до hydrate | исправлено |
| 4 | Портфель: «выбрать все» → через 400 мс `schedulePortfolio` снимает галки | исправлено |
| 5 | Портфель: доля ≠ скелет/`price_mover` (берёт max ордеров полосы) | хвост |
| 6 | Нетто: `movesPerDay` = streak шагов (≤5), не «в день» как в FeeMath/портфеле | хвост |
| 7 | List stale ask: только histCap, `NeighborSellCap` не в min | исправлено; затем пустой list — NeighborSellCap включал хаб A (post≤ask → после налога ≤0); исключены A и B |
| 8 | Partial портфель: полный `RelistCostDay` при урезанном cap | хвост |
| 9 | `Anomaly.IsSuspicious(row, null)` — ветки истории не работают | хвост |
| 10 | Rebuild reject: cosmetic поверх `no_trade` → теряется no_trade | хвост |

## P2 (кратко)

Деньги `,xx` — `isk` = целые (13.10); фильтр Аном. пустышка; fromHub exact key; нет `resp.ok` на exclude/haul save; полный fill watch не в `watch_fill`; `maxMarketShare`/`maxRelistsDay` зашиты в UI; док 1.5× vs код 2× ask.

## Чисто / закрыто

- Portfolio exclude wipe — закрыт (`Exclude != null` only).
- Fee `POST /api/settings` не трогает `pf_*`/`hl_*`/`tray_notify`.
- SignalRule ворота, полосы 1%, медиана 7д, hours gap 3ч / churn 90м, issued в `location_quote` — совпадают со скелетом.
- List gates V≥10 / spread / MaxListAskMultiple=2; Margin Trading не в капитал; дедуп Jita/Perimeter.

## Дальше

План фиксов по P0→P1 согласовать отдельно. Автор аудита себе «принято» не ставит.
