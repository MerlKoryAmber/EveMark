# SKELETON — EveMark

Актуальная карта. Читать перед задачей. Обновлять в том же шаге, если меняются структура, схема БД, пороги доски или сигналов.

Имя на экране и в трее: MK Jitawatch. Файл exe остаётся `JitaWatch.exe`. Каталог: `C:\Code\jita-watch`. Репозиторий: https://github.com/MerlKoryAmber/EveMark. Для человека: `README.md` (кратко) и `docs/OPISANIE.md` (подробно зачем и что умеет). Знак в шапке — `wwwroot/assets/brand-mark.png`. Вкладка и трей — готовый `wwwroot/favicon.ico` из squid-panel, он же `app.ico`.

## Статус
Локальный стол работает (после 0.2.2: слежение 5 мин, медиана 7д, доля захвата, станционные fills). Ордера только читаются. Приёмку ставит человек.

Хвост: история ESI для type 62593 отвечает 400, тип не помечается `no_trade`, запрос повторяется.

## Корень
| Путь | Роль |
|------|------|
| `Program.cs` | Kestrel `http://127.0.0.1:8741`, корень контента = каталог exe |
| `CollectorService.cs` | Часовой снимок, имена, история, отсев украшений |
| `WatchPollService.cs` | Watchlist каждые `WatchPollMinutes` (5), ETag, fills / price_mover |
| `EsiClient.cs` | ESI: регионы, type orders+ETag, история, имена |
| `MarketDb.cs` / `MarketDb.Watch.cs` | SQLite, доска, сигналы, watchlist |
| `FeeMath.cs` | Налог, брокер, маржа |
| `FileLog.cs` | Лог в `data/jita.log`, обрезка на 2 МБ, хвост 512 КБ |
| `TrayContext.cs` | Трей: Открыть / Сигнал / «Оповещения о заработке» / Выход. Шарик «Крик» отключается галкой (`setting.tray_notify`) |
| `MarketOptions.cs` | Регион, станция, порог цены, период снимка |
| `appsettings.json` | Значения `Market` |
| `wwwroot/index.html` | Доска, сигнал, настройки |
| `app.ico` | Иконка exe. Трей читает `wwwroot/favicon.ico`, тот же файл |
| `CLAUDE.md` | Метод агента. Читать до этой карты |
| `docs/SKELETON.md` | Эта карта |
| `CURSOR_HANDOFF.txt` | Короткий статус сессии |

## Не в git
`bin/`, `obj/`, `publish/`, `data/`, базы. Живая база и лог: `publish\data\jita.db`, `publish\data\jita.log`. Каталог `data\` у исходников — не живой.

## Запуск
Публикация самодостаточная, Windows x64, .NET на той машине не нужен: `dotnet publish JitaWatch.csproj -c Release -r win-x64 --self-contained true -o publish`. Перед этим остановить `JitaWatch.exe` (файл занят). Старт с рабочим каталогом `publish`. `publish\data` не удалять. Копировать на другой компьютер всю папку `publish`. Если `data\jita.db` рядом с exe нет, старт спрашивает, создать ли новую. Отказ — выход, файл не создаётся.

## Рынок
- Снимок раз в час по регионам хабов: The Forge, Domain, Sinq Laison, Heimatar, Metropolis.
- **Закуп** — книга из `location_quote` по галкам Jita / Perimeter (по умолчанию обе). Сигнал по-прежнему смотрит полную склейку снимка. Структуры игроков ESI без логина не отдаёт — хвост.
- **Развоз** — отдельный раздел. В `location_quote` sell от 500 тыс. ISK. Только выгодные мгновенные связки A→B (в скупку, без брокерской). Колонки «Выставить» / «Маржа выст.» — лучший sell на месте назначения и маржа с брокерской и налогом. «Объём/сутки» — вчерашняя история ESI региона назначения (не Forge всегда). Хабы: Jita, Perimeter, Amarr, Dodixie, Rens, Hek. Прыжки из ESI route, м³/шт из universe/types. `daily_history` / `history_sync` ключ `(region_id, type_id)`.
- В базе по типу на доске: лучшая покупка = max buy, лучшая продажа = min sell.
- Объём и число ордеров — внутри 1% от лучшей цены. Покупка: цена ≥ bestBuy×0.99. Продажа: цена ≤ bestSell×1.01.
- Пороги sell при съёме из `setting`: `min_sell_price` (закуп, по умолчанию 1e6), `min_haul_sell_price` (развоз, по умолчанию 5e5). Настраиваются в UI.
- Объём/сутки на закупе = **медиана** ESI The Forge за до 7 полных дней; оценка = маржа × min(медиана×доля_захвата, полосы 1%). Доля = 1/число уникальных order_id из `price_mover` за ~6 ч (иначе max ордеров в полосе). Станционный объём 24ч — `watch_fill`. Watchlist: таблицы `watchlist`, `watch_order`, `watch_fill`, `price_mover`, `esi_etag`. API: `GET|POST|DELETE /api/watch`, `POST /api/watch/batch`, `POST /api/watch/poll`. На закупе и в портфеле — чекбоксы строк + кнопка «В слежение» (пакет на Jita 4-4); ручной typeId на вкладке Слежение остаётся.
- Имена: sync тянет ESI `/universe/names` для типов закупа и развоза без имени или с заглушкой `#id`. Пока имени нет — в таблице `#typeId`, не голый номер.
- Часы — длительность серии живой вилки по `taken_at` (floor часов от свежего до старого): маржа > 0, обе стороны ≥ 2 шт.; разрыв > **3 ч** (`SignalRule.MaxHoursGap`) рвёт — запас на падение/рестарт. Не число волн (пять съёмов за 10 мин → 0). Дёргают/сжатие по-прежнему шаг ≤ 90 мин.
- Доска не показывает маржу выше 5 000% от покупки. Кнопка «Столбцы» на закупе и в сигнале прячет столбец вместе с фильтром. Выбор хранится в браузере.
- Таблица не шире окна. Предмет стоит на месте, остальные столбцы едут по нижней полосе.
- Деньги в UI целыми ISK. Объёмы, часы и проценты — как есть. В полях от/до у денег и объёмов те же группы разрядов, что в столбце. Процент в фильтре как на экране: 5 = 5%. Фильтр от/до, пустая граница не режет. Страница 200.
- Перестановок: сколько раз можно опустить продажу, пока кончится маржа с единицы. Формула EVE при Advanced Broker Relations 0: 50% × брокерская × цена продажи. Рост цены и минимум 100 ISK за приказ в это число не входят.
- На лучшей покупке и на лучшей продаже — штуки ровно по лучшей цене. Полоса 1% остаётся в «На покупке» и «На продаже». В старом снимке, где этого ещё не считали, стоит прочерк.
- Капитал = покупка × меньшая из двух полос 1%. Дней = этот объём / вчерашний объём The Forge. В день = оценка за сутки / капитал.

## Комиссии
Налог 7.5%×(1−0.11×Accounting). Брокер 3%−0.3%×BrokerRelations−0.03%×фракция−0.02%×корп, пол 1%. По умолчанию Accounting V, Broker Relations V, стендинги 0. Хранятся в `setting`. Маржа = sell×(1−брокер−налог) − buy×(1+брокер). Перестановка в маржу не входит.

## Сигналы
Общий вход: маржа после комиссий 3–100%, вчерашний объём ≥ 10.

| Раздел | Условие |
|--------|---------|
| Крик | ≥ 6 снимков, часы ≥ 6, оценка ≥ 10 млн, вчерашняя средняя внутри спреда. Тонко: «покупка», если buy×2 < sell; «продажа», если sell×2 < buy |
| Мимо цены | Те же ворота, средняя вне спреда |
| Дёргают | ≥ 4 снимка. Лучшая покупка или продажа менялась каждый шаг, серия ≥ 3, смотрим до 5 шагов, разрыв ≤ 90 минут |
| Сжимается | Маржа > 0 и строго падает 3 часа подряд. «Маржа была» — маржа в начале падения |

Шарик трея только когда есть Крик. У заголовков столбцов в сигнале подсказка при наведении, простым языком. Ссылка на предмет: `https://evetycoon.com/market/{typeId}`, карточка EveTycoon.

## Что не грузится
`reject.reason`: `cosmetic`, `special_edition`, `blueprint` (галочки в Настройках, по умолчанию все вкл.), `no_trade` (история без сделок). Старые скины могут лежать как `skin`. `Unreject` снимает только `no_trade`. Сохранение настроек пересобирает name-фильтры.

- `IsCosmetic`: ` SKIN`; Men's/Women's; Apparel; Camo…; слова Jacket, Boots, Suit…
- `IsSpecialEdition` (категория ESI 63): `Special Edition`; `Tournament Card`; префикс `NEO YC`; датированный `YC1xx.` (не строка «Special Edition Assets» в имени — её у типов нет)
- `IsBlueprint`: имя содержит `Blueprint`

## База
SQLite WAL. Деньги в TEXT. Таблицы: `snapshot`, `quote`, `type_name`, `daily_history`, `history_sync`, `setting`, `reject`, `status`, `location_*`, `watchlist`, `watch_order`, `watch_fill`, `price_mover`, `esi_etag`. Срок хранения 1–400 дней, по умолчанию 120 (`setting.keep_days`). Чистка после снимка без VACUUM. Сохранение настроек чистит и делает VACUUM.

## API
`GET /api/status`, `GET|POST /api/settings`, `POST /api/collect`, `GET /api/recommendations`, `GET /api/signals`, `GET /api/haul`, `GET /api/haul/trips`, `GET /api/haul/trip`, `GET|POST /api/haul/settings`, `POST /api/haul/pack` (старый рюкзак), `GET /api/portfolio`, `GET /api/contracts`, `GET|POST /api/notes`, `GET /api/backtest/shout`, `POST /api/refine`, `GET /api/refine/ores`, `GET|POST|DELETE /api/watch`, `POST /api/watch/batch`, `POST /api/watch/poll`.
Файлы: `Portfolio.cs`, `HaulTrip.cs`, `HaulPro.cs`, `Anomaly.cs`, `Refine.cs`, `MarketDb.Extra.cs`, `MarketDb.HaulTrip.cs`.
Портфель (ТЗ): кандидаты с доски закупа; оптимизатор лагранж+trim; `pf_*` (в т.ч. `pf_min_volume_day`, по умолчанию 10 — медиана объёма/сутки); `--test-portfolio`.
Развоз «Рейс» (ТЗ `HAULING_SPEC`): подвкладка рядом с доской; `hl_*`; лоты instant/list/best из `location_quote` (полного стакана нет); глубина = touch; `best_sell_issued`/`best_buy_issued`. List: sell_post = min(ask−tick, max avg истории `hl_list_hist_days`=14, мин. best_sell хабов кроме A и B); ask старше `hl_list_max_ask_age_days`=7 — ask не якорь, остаётся min(история, те же соседи); V_B≥10, ≥3 sell, вилка≤2×, sell≤2×ask; `days_to_sell`≤горизонт; skip reject; жадный §5.2; `--test-haul`. UI: «Возраст ask», поля возраста/истории в «прочее»; меню столбцов доски не трогает рейс.
Навигация: Закуп · Сигнал · Портфель · Развоз · Слежение · Ещё · Настройки. Последняя вкладка (и подвкладка Рейс) — в `localStorage` / hash, F5 не сбрасывает в Закуп.
