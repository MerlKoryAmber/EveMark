# АУДИТ: UI + API + сохранение настроек

Каталог: `C:\Code\jita-watch`. Дата: 13.10.2026. Код не чинился.

Фокус: `wwwroot/index.html`, `Program.cs`, сохранение settings (fees / portfolio / haul). Паттерны как баг портфеля exclude.

## 1) Находки

### P0 — Портфель: форма не гидрируется, каждый `loadPortfolio` пишет stale в БД
- **Что:** При открытии вкладки / F5 поля `#pf-*` остаются HTML-дефолтами. `loadPortfolio` всегда сначала `savePortfolioSettings()` → `POST /api/portfolio/settings`, затем GET. Сохранённые `pf_capital`, слоты, горизонт и т.д. затираются дефолтами. `dto.settings` с сервера почти не используется (только длина exclude).
- **Где:** `wwwroot/index.html` — `loadPortfolio` (~2196), `savePortfolioSettings` (~2172); нет fill из GET. `MarketDb.Extra.cs` — `SavePortfolioSettingsAsync`.
- **Воспроизвести:** Выставить капитал ≠ 1e9, «Пересчитать», F5, снова «Портфель». В БД/`dto.settings` капитал снова 1e9.

### P1 — Сигнал «Дёргают» / Нетто: sort и filter не работают
- **Что:** UI шлёт `sort=netMarginIsk` и фильтры `netMarginIskMin/Max`. `SortKey` / `CompareRows` не знают ключ → fallback `dailyIsk`. Параметров `netMargin*` в `GET /api/signals` нет → фильтр молча игнорируется. Колонка рисуется, значение есть (`NetMarginIsk`).
- **Где:** `index.html` `signalViews.churn` / `buildSignalHead` (~1376, ~1752); `MarketDb.cs` `SortKey`/`CompareRows` (~1823–1856); `Program.cs` MapGet `/api/signals` (~364–421).
- **Воспроизвести:** Сигнал → Дёргают → клик «Нетто»; фильтр от/до по Нетто — порядок/отбор не по нетто.

### P1 — Закуп: сортировка Конкур./Доля/Станция 24ч → dailyIsk
- **Что:** Заголовки `competitors`, `captureShare`, `stationVolume24h` кликабельны, в `SortKey` отсутствуют → сервер сортирует как `dailyIsk`.
- **Где:** `index.html` th `data-k` (~566–568); `MarketDb.cs` `SortKey` (~1849–1856).
- **Воспроизвести:** Закуп → клик «Конкур.» — порядок как у оценки/сутки.

### P1 — Haul settings: POST затирает поля без UI + race со stale формой
- **Что:** `saveHaulSettings` каждый раз шлёт хардкод `gankSystems`, `gankWarnValue`, `minUnitPrice`, `courierMaxM3`, `maxMarketShare: 0.3`. Кастом в БД без полей UI сбрасывается при «Пересчитать»/автосейве. Плюс `fetch(/api/haul/settings)` асинхронный: ранний «Рейс» до fill пишет HTML-дефолты поверх БД.
- **Где:** `index.html` `saveHaulSettings` (~2380–2407), hydrate (~2663); `MarketDb.HaulTrip.cs` `SaveHaulSettingsAsync`.
- **Воспроизвести:** Поменять `hl_gank_systems` в setting вручную → Рейс → Пересчитать → значение снова дефолт. Или F5 → мгновенно Развоз→Рейс до ответа settings.

### P1 — Портфель: «выбрать все» сбрасывает галки через 400 мс
- **Что:** На `#pf-watch-all` висит и select-all, и общий `change → schedulePortfolio()`. Через 400 мс `loadPortfolio` → `renderPortfolioRows` обнуляет чекбоксы. Batch «В слежение» после select-all ломается, если не успеть.
- **Где:** `index.html` (~2296–2312, ~2271–2273).
- **Воспроизвести:** Портфель → галка в шапке → подождать >400 мс → галки строк сняты.

### P2 — Деньги с дробями при правиле «целыми ISK»
- **Что:** Портфель/рейс/контракты/бэктест рендерят ISK через `isk.format` (2 знака), не `iskWhole`.
- **Где:** `index.html` `renderPortfolioRows` (~2260–2266), `renderTripsRows`/`renderBasketRows` (~2478+), contracts (~2697).
- **Воспроизвести:** Портфель — колонки Покупка/Маржа/Капитал с `,xx`.

### P2 — Сигнал: фильтры Аном. (и любой non-text) без API
- **Что:** `buildSignalHead` для `kind=flag` рисует `anomalyMin/Max`; API не принимает → фильтр-пустышка.
- **Где:** `index.html` ~1751–1754; `Program.cs` `/api/signals`.
- **Воспроизвести:** Крик → фильтр под «Аном.» — список не меняется.

### P2 — Развоз доска: fromHub/toHub — exact hub, под колонкой имени станции
- **Что:** Placeholder «jita / perimeter…», сравнение `string.Equals(hub, filter)` без substring; под `fromName`/`toName` легко ввести имя станции — пусто.
- **Где:** `MarketDb.cs` ~1234–1238; `index.html` filters ~704–705.
- **Воспроизвести:** Фильтр «Откуда» = `Jita IV` → пусто; `jita` → ок.

### P2 — Кнопки без проверки ошибок
- **Что:** `watch-poll` всегда «Опрос завершён»; exclude портфеля / `saveHaulSettings` / `savePortfolioSettings` без `resp.ok`.
- **Где:** `index.html` ~2102–2107, ~2318–2336, ~2191, ~2405.

## 2) Проверено и чисто

- **Portfolio exclude overwrite:** `SavePortfolioSettingsAsync` пишет `pf_exclude` только если `Exclude != null`; UI exclude не шлёт — паттерн бага закрыт (`MarketDb.Extra.cs` ~80–85).
- **Fee `POST /api/settings`:** не трогает `tray_notify` / `pf_*` / `hl_*`.
- **Закуп фильтры data-f ↔ API:** money/volume/touch/relists/capital/days/roi совпадают с `Program.cs` `/api/recommendations`.
- **Haul доска фильтры** name/fromHub/toHub/margin/units/volume/jumps/dailyIsk — API принимает; пустые th у ask/bid/list — без ложных data-f.
- **applyColumns:** по таблице; меню haul на `#haul-board-pane`; рейс снимает чужой `col-off` (~2487).
- **Watch batch:** `addCheckedToWatch` проверяет `resp.ok`.
- **Сигнал вкладки** shout/off/churn/squeeze: kind доходит; thin/average/buyMoves/… фильтры API есть (кроме netMargin/anomaly).

## 3) Статус

РЕАЛИЗОВАНО НО НЕ ПРИНЯТО — только аудит. Код не менялся, коммита нет.
