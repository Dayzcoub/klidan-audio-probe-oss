# KLIDAN Audio Probe

Независимая Windows x64-утилита для диагностики аудиоинтерфейсов и квалификации WASAPI Exclusive как целевого транспорта KLIDAN; WASAPI Shared рассматривается отдельно как fallback.

**Статус:** experimental preview. Реализованы пассивное endpoint discovery, WASAPI Exclusive format query и Initialize-only, bounded Capture/Render stream checks, Duplex, Period Discovery, Repeatability, Sustained Validation, Capture meter, Evidence Library, versioned device profiles и consent-gated Automatic Scan. WPF GUI использует отдельный Native Worker и Named Pipe IPC; рабочий путь Device → Test → Result включает реальный scan orchestration и отдельные synthetic Developer Tools.

**Preview 0.1.0-preview.1:** это предварительное ПО, не прошедшее физическую аудио-проверку в текущем QA-сеансе. Установщик не подписан; Windows SmartScreen может показать предупреждение. Физическая сертификация RTL не выполнялась, `AudioHost` остаётся `NOT_CERTIFIED`. Подробности и ограничения приведены в [release notes](RELEASE_NOTES_v0.1.0-preview.1.md) и [Final QA report](L10N_VISUAL_QA_REPORT.md).

Приложение не является сертификационным инструментом. Оно показывает наблюдения Windows/WASAPI и сохраняет их происхождение; `AudioHost` остаётся `NOT_CERTIFIED`, физические RTL-измерения отсутствуют, а программные transport/clock результаты не следует трактовать как ADC/DAC или акустическую задержку.

### GUI Design Kit v0.1 — D0

Визуальный baseline и WPF boundary документированы в [docs/gui](docs/gui/README.md), [D1 architecture](docs/gui/D1_ARCHITECTURE.md), [IPC](docs/gui/D1_IPC_PROTOCOL.md) и [lifecycle](docs/gui/D1_LIFECYCLE.md). Developer Tools остаются synthetic/diagnostic-only; они не являются доказательством физических возможностей устройства.

Device profiles агрегируют exact endpoint-bound evidence, query/Initialize/stream/Duplex observations, historical recovery и sanitized export. Профиль не является KLIDAN import, AudioHost configuration или физической сертификацией; см. [profile format](docs/PROFILE_FORMAT.md) и [profile schemas](docs/schemas/).

## Реализованные GUI workflows

- **Automatic Scan** — свежий discovery, immutable consent-bound plan, bounded Query/Initialize/Stream/Duplex stages и честные `UNKNOWN`/`NOT_RUN` outcomes.
- **Period Discovery** — exact current-endpoint period candidates с read-back validation; unsupported или incomplete results не продвигаются.
- **Duplex** — независимые Capture/Render WASAPI Exclusive sessions с отдельными lifecycle, quality, coverage и cleanup facts.
- **Repeatability** — bounded repeated Duplex operations, per-run identity и aggregate без усреднения статусов.
- **Sustained Validation** — длительные bounded observations с fail-closed selection; benign intermediate waits остаются diagnostic counters, но material faults блокируют selection.
- **Evidence Library** — локальный просмотр, сравнение и экспорт завершённых evidence bundles без публикации raw endpoint identifiers.
- **Device profiles** — versioned JSON profiles, local atomic save/read-back, strict validation, partial Capture/Render states и sanitized export.

Физический QA устройств отражён только в [Hardware Compatibility Matrix](docs/HARDWARE_COMPATIBILITY.md); сырые отчёты и персональные/машинные идентификаторы не входят в OSS snapshot.

## MVP и принципы

- Определять реальные Windows Capture/Render endpoints, версии драйверов, mix/device formats, матрицу точных Exclusive-форматов.
- Раздельно показывать `IsFormatSupported`, `Initialize`, `Start` и завершение короткого потока; длительную стабильность проверять отдельно.
- Формировать воспроизводимые локальные JSON-отчёты и профили совместимости **с происхождением доказательств**.
- Не менять системные настройки, не записывать аудио и не воспроизводить тестовый сигнал без явной команды пользователя.
- Работать отдельно от KLIDAN, без AudioHost, медиатеки, VST, обязательных онлайн-сервисов и сторонних драйверов.
- Позднее обмениваться с KLIDAN **версионированным JSON**, но не связывать процессы жёсткой зависимостью.

## Сборка (Windows 10/11 x64)

Нужны CMake 3.23+ и Visual Studio 2022 C++ Build Tools с Windows SDK.

Публичный Preview пока не опубликован как GitHub Release. Собранный для QA установщик — неподписанный; используйте только явно переданный вам артефакт и сверяйте SHA-256 из его manifest. Возможна предупреждающая страница SmartScreen.

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
.\build\Release\KlidanAudioProbe.exe --help
.\build\Release\KlidanAudioProbe.exe --list
```

Чтобы диагностировать устройство, скопируйте **точный endpoint ID** из вывода `--list`:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<endpoint id>'
.\build\Release\KlidanAudioProbe.exe --render-id '<endpoint id>'
```

Текущий `--list` **не открывает аудиопотоки**. Пассивные команды вызывают `IsFormatSupported`, но не `Initialize` / `Start`. Вход/выход, занятый другой программой, может повлиять на тест Exclusive.

### Матрица Exclusive-форматов (v0.2)

Выберите ровно один endpoint явным `--capture-id` или `--render-id`. Матрица проверяет 44,1/48 кГц, mono/stereo, PCM16, PCM24-in-32 и Float32 для периодов Default и Minimum:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<endpoint id>' --scan-formats --output-json '.\capture-matrix.json'
.\build\Release\KlidanAudioProbe.exe --render-id '<endpoint id>' --validate-formats --confirm-exclusive-init --output-json '.\render-matrix.json'
```

`--scan-formats` выполняет точный Exclusive `IsFormatSupported` один раз на уникальное сочетание формата/частоты/каналов и не вызывает `Initialize` или `Start`. `--validate-formats` требует `--confirm-exclusive-init` и отдельно вызывает `Initialize` для каждой пары кандидат/период; фиксирует одну допустимую alignment retry, размер буфера и готовность event handle. Он не вызывает `Start`, не получает аудиобуферы и не передаёт/сохраняет PCM. Exclusive Initialize может временно занять выбранный endpoint. Матрица никогда не выбирает или сканирует другие endpoint автоматически.

Статусы отделяют `SUPPORTED_REPORTED` от фактических `INITIALIZED`/`READY`; `UNSUPPORTED`, `FAILED`, `NOT_TESTED` и `INCOMPLETE` сохраняются отдельно. JSON — локальный диагностический отчёт schema v1 с происхождением `PASSIVE_QUERY` или `INITIALIZE_ONLY`; он не включает endpoint ID или путь и не является сертифицированным профилем. Существующий файл не перезаписывается.

Для явной проверки физической инициализации одного точного формата используйте `--test-init` с одним endpoint ID:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<endpoint id>' --test-init --rate 48000 --channels 1 --format pcm16 --period default
.\build\Release\KlidanAudioProbe.exe --render-id '<endpoint id>' --test-init --rate 44100 --channels 2 --format float32 --period minimum
```

Допустимы частоты `44100` и `48000`, каналы `1` (mono) и `2` (stereo), форматы `pcm16`, `pcm24-in-32` и `float32`. `--period` принимает `default` или `minimum`; без параметра выбирается `default`. Выбор использует соответствующее значение из `GetDevicePeriod`, а после buffer alignment выполняется максимум одна повторная попытка на новом `IAudioClient` с размером, сообщённым WASAPI. В отчёте отдельно показаны результат `Initialize` и готовность event-driven клиента (`GetBufferSize` и `SetEventHandle`). Если подготовка клиента не удалась, итоговый код завершения ненулевой, даже когда `Initialize` прошёл. `Start` не вызывается, аудио не передаётся, устойчивость и реальная задержка не проверяются. Exclusive-инициализация может временно занять устройство; после теста клиент освобождается.

Для короткой проверки реального запуска используйте отдельный `--test-stream`. Длительность ограничена 1–30 секундами, тест принимает только 48 кГц / Stereo PCM16. Capture-пакеты учитываются и освобождаются без сохранения сэмплов. Render подаёт только штатную WASAPI тишину. Диагностика сообщает интервалы событий и оценку percentile после теста, а также сравнивает кадры после `Start` с ожидаемым объёмом за фактическое время. Допуск на начало/конец равен большей величине из 10% ожидаемых кадров и двух буферов; интервалы свыше 1,5 ожидаемого периода, discontinuity и другие некритические аномалии дают предупреждение. Результаты разделяются на `INITIALIZE_FAILED`, `START_FAILED`, `STREAM_FAILED`, `STREAM_COMPLETED_WITH_WARNINGS` и `STREAM_COMPLETED`. Недобор кадров сверх допуска считается `STREAM_FAILED`. Эти счётчики не измеряют физическую частоту ЦАП, акустическую задержку или длительную стабильность.

#### Изолированный Render channel test (C2)

Явный `--test-output-channel` подаёт на один выбранный Windows channel gated 997 Hz sine с цифровым peak не выше −36 dBFS. Нужны точный `IsFormatSupported(S_OK)`, успешный Exclusive Initialize и два подтверждения до Start:

```powershell
.\build\Release\KlidanAudioProbe.exe --render-id '<render endpoint id>' --test-output-channel --render-channel 1 --confirm-live-output --confirm-test-signal --duration 5 --period default --mmcss off --output-json '.\render-channel.json'
```

Поддерживается только 48 kHz / Stereo PCM16 Exclusive, длительность 1–10 секунд. Начальный Render buffer — цифровая тишина. При нормальном завершении тест переключается на нули на обычном event уведомлении, если до deadline осталось не более одного периода буфера. При отмене или ошибке генерация прекращается и Render останавливается без дополнительного запроса буфера. Невыбранный канал всегда заполняется нулями. Для физического запуска выставьте низкий уровень усилителя/активных колонок и убедитесь в выбранном Windows output; проба не меняет системные уровни и не определяет физические разъёмы. JSON schema v1 скрывает endpoint ID и путь, оставляет физическое соответствие `UNKNOWN`, отказывается перезаписывать файл и не сертифицирует устройство. `ReleaseBuffer` подтверждает только software buffer submission: физическое воспроизведение, DAC output и audibility не измеряются.

Параметр `--mmcss off|pro-audio` действует для `--test-stream`, `--test-duplex` и подтверждённого `--test-monitor`, по умолчанию `off`. `pro-audio` регистрирует обслуживающий поток с MMCSS task `Pro Audio` перед `Start`, снимает регистрацию после `Stop` и сообщает оба результата. Если регистрация не удалась, обычный поток не запускается. Capture выводит компактную сводку discontinuity и сравнивает device position соседних пакетов, сопоставляя скачки/перекрытия с `DATA_DISCONTINUITY`; `TIMESTAMP_ERROR` исключает пакет из сравнения. Отдельное `STREAM_QUALITY` бывает `CLEAN`, `ANOMALIES_PRESENT` или `SEVERE_ANOMALIES`: серьёзность означает неуспешный/недокормленный поток, отклонение позиции не меньше размера буфера или не менее трёх аномалий одного класса за запуск. Позиции Capture device/QPC — только диагностические данные; QPC выражен в 100-нс единицах. Ни одна из этих величин не измеряет RTL, физическую частоту ЦАП или акустическую задержку.

Для одновременного отдельного Capture и Render запустите явный `--test-duplex` с обоими endpoint ID:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<capture endpoint id>' --render-id '<render endpoint id>' --test-duplex --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum --mmcss pro-audio
```

Duplex готовит оба Exclusive-клиента в отдельных COM MTA-потоках и разрешает `Start` только после готовности обеих сторон. Capture-пакеты освобождаются без сохранения; Render передаёт только WASAPI silence. Потоки не соединяются, вход не мониторится. При сбое или отмене один поток останавливает второй. Вывод содержит отдельные статусы/качество, frame coverage и события каждой стороны, отличие Start, реальное перекрытие времени и разницу счётчиков кадров. Разница кадров не доказывает clock drift. Запуск занимает Exclusive-доступ к обоим endpoint.

Для дополнительной оценки темпа двух stream clocks включите `--clock-probe` вместе с `--test-duplex`:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<capture endpoint id>' --render-id '<render endpoint id>' --test-duplex --clock-probe --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum --mmcss pro-audio
```

Каждый worker опрашивает свой `IAudioClock` приблизительно раз в 250 мс после обслуживания audio event; до `Start` резервируется место максимум для 128 снимков. Clock position переводится в секунды делением на частоту, а связанные QPC timestamps сравниваются на общей шкале 100 нс. Анализ отбрасывает первые четыре и последний валидный снимок каждой стороны, требует не менее восьми точек на сторону, общего интервала не короче секунды и R² не ниже 0.95. `S_FALSE` и API errors считаются отдельно и не входят в fit; ошибки/повторы QPC и немонотонные позиции дают `UNRELIABLE_TIMESTAMPS`, а слабая выборка — `INSUFFICIENT_DATA`. Сводка Clock Probe не меняет статусы Duplex. Оценки относятся к наклонам stream-position относительно Windows/driver QPC и не доказывают аппаратный drift, абсолютную фазу, акустическую задержку или RTL. Позиция и QPC-семантика приведены в документации [IAudioClock](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudioclock) и [GetPosition](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition).

Для экспериментальной передачи живого Capture → Render используйте только явный режим `--test-monitor` и обязательное подтверждение `--confirm-live-output`:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<capture endpoint id>' --render-id '<render endpoint id>' --test-monitor --confirm-live-output --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum --mmcss pro-audio
.\build\Release\KlidanAudioProbe.exe --capture-id '<capture endpoint id>' --render-id '<render endpoint id>' --test-monitor --confirm-live-output --signal-levels --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum --mmcss pro-audio
```

Этот режим поддерживает только WASAPI Exclusive 48 кГц / Stereo PCM16, длительность 1–30 секунд. Перед физическим запуском оператор должен отдельно разрешить живой вывод. Используйте наушники, минимальную аппаратную громкость и акустически изолируйте выход от микрофона: программное усиление фиксировано на −24 dB, но это не защищает от акустической обратной связи. Capture не сохраняется: кадры существуют только в bounded SPSC ring. Начальный порог заполнения — три фактических буфера большей стороны, ёмкость — восемь; при переполнении удаляются самые старые кадры. До заполнения и при underrun Render подаёт тишину. Диагностика выводит транспортные счётчики, occupancy, prefill, статусы и `MONITOR_STATUS`. Ring depth и оценка времени буферизации не являются RTL или сквозной задержкой.

#### Измерение цифровой задержки Capture → Ring → Render (B4-B2A)

Этот отдельный режим не передаёт звук микрофона. После каждого Capture-пакета программа игнорирует WASAPI sample pointer и создаёт детерминированный тестовый маркер в заранее выделенном буфере; исходные frame/device-position метки проходят через per-slot provenance существующего SPSC ring. На Render отправляются только маркеры и нули, без физической петли. Нужны оба подтверждения; без них устройства не активируются:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<capture endpoint id>' --render-id '<render endpoint id>' --test-monitor-latency --confirm-live-output --confirm-test-signal --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum --mmcss pro-audio
```

Длительность фиксирована на 5 секундах, формат — 48 кГц Stereo PCM16. Маркер имеет −36 dBFS peak перед ring и примерно −60 dBFS после неизменяемого −24 dB gain; он выводится только в левом канале. Отключите аналоговый loopback кабель перед запуском. Статус `MEASURED` требует не менее пяти provenance-сопоставленных маркеров, пригодных сегментов временных меток, не более 6 кадров fit uncertainty и полной вариации, успешного окончания потоков и отсутствия ring loss. QPC segment boundary, маркер, который пересекает её, и причина исключения отображаются в отчёте. Результат — оценка цифрового Capture scheduling → Ring → Render transport. Это не B4-B1 физический аналоговый RTL и не задержка ADC/DAC или полного микрофонного мониторинга.

Необязательный `--signal-levels` измеряет Capture PCM16 до gain/ring и фактические Render PCM16 после фиксированного −24 dB непосредственно перед `ReleaseBuffer`. Для L/R выводятся peak и RMS в dBFS, ненулевые/проанализированные сэмплы, а также пакеты/буферы с флагом `SILENT` и с нулевыми данными. Для нулевого сигнала выводится `-inf dBFS`. `SIGNAL_STATUS` описывает только цифровые данные; `SINGLE_CHANNEL_SIGNAL_DETECTED` отмечает активность одного канала. Peak ниже −60 dBFS даёт только `LOW_SIGNAL_LEVEL` advisory и не меняет `MONITOR_STATUS` или `DUPLEX_STREAM_QUALITY`. Цифровое наличие сигнала не подтверждает слышимость; её по-прежнему проверяет оператор. Без `--signal-levels` существующая обработка PCM не выполняет дополнительного сканирования.

### Channel topology и Capture meter (v0.3 C1)

Пассивный Capture endpoint inspection показывает Windows mix-format channels, channel mask и Windows speaker labels, когда маска корректна. Эти labels не являются номерами физических гнёзд; неизвестное остаётся неизвестным. Для ограниченного PCM16 Capture meter требуется явный endpoint и подтверждение:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<capture endpoint id>' --meter-capture --confirm-live-capture --channels 2 --duration 5 --period default --mmcss off --output-json '.\capture-meter.json'
```

Режим ограничен 48 kHz PCM16 mono/stereo, выполняет точный support query и Initialize перед Start, выводит latest-completed-window peak/RMS, отдельный session peak hold и финальные показатели по всему захвату из bounded atomic snapshots. Отчёт сохраняет отдельно жизненный цикл Capture, stream status/quality, coverage и integrity-счётчики; наблюдения до запуска остаются `null`. Сэмплы не сохраняются, Render никогда не открывается. JSON отделяет topology от наблюдённых уровней, не включает endpoint ID или путь, не заявляет акустическую калибровку и не заполняет физическое соответствие каналов; файл создаётся только если он новый. Meter core рассчитан на 1–32 interleaved PCM16 каналов; WASAPI активный режим пока поддерживает только mono/stereo. Отменённый запуск останавливает Capture; наблюдённые dBFS описывают только цифровые PCM-значения.

### Физическая аналоговая петля B4-B1

`--test-analog-loopback` измеряет отдельный путь **WASAPI Render → DAC → аналоговый кабель → ADC → WASAPI Capture**. Это не задержка B4-A Live Monitor Capture → Ring → Render. Режим принимает только 48 кГц Stereo PCM16, ровно 5 секунд, выбранные `--render-channel 1|2` и `--capture-channel 1|2`. Обязательны `--confirm-test-signal` и `--confirm-physical-loopback`; без обоих подтверждений устройства не открываются и сигнал не создаётся. Генерируется фиксированная оконная PRBS-последовательность с peak ceiling −36 dBFS только на выбранном Render-канале; второй канал цифрово нулевой. Capture хранится ограниченно в памяти на время анализа и стирается после него; файлы не создаются.

Первое подключение Audient iD4: **Audient Line Out L → TRS cable → Audient Line Input 1**. До запуска отключите микрофоны, 48V phantom power, акустические колонки и прямой аппаратный мониторинг входа. Не подключайте линейный выход к XLR mic input. Программный Audient Loopback Mixer не заменяет физическую аналоговую петлю. Несмотря на ограниченный уровень тестового сигнала, сначала проверьте минимальный аппаратный уровень и безопасное кабельное подключение.

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<capture endpoint id>' --render-id '<render endpoint id>' --test-analog-loopback --confirm-test-signal --confirm-physical-loopback --capture-channel 1 --render-channel 1 --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum --mmcss pro-audio
```

Render marker sample index привязан к stream-relative `IAudioClock::GetPosition` position/frequency. Парная QPC timestamp функция задаёт его время на общей 100-нс шкале; Capture marker привязывается регрессией packet device positions и QPC timestamps из `IAudioCaptureClient::GetBuffer`. Регрессия использует полный центрированный вид `QPC = intercept + slope × devicePosition`, а преобразование применяет найденный intercept. B4-B1.2 сохраняет индивидуальный диагностический порог 0.80 и отдельно проверяет согласованную последовательность: порядок, интервалы 12 000 кадров, знаки полярности и согласованность offsets. Порог последовательности: кандидат ≥0.35, минимум пять совпадений, средняя корреляция ≥0.45, ноль противоречий полярности, интервал в пределах ±24 кадров, RMS offset residual ≤8 кадров, coherence score ≥0.12 и запас уникальности ≥0.02; совпадение кандидата допускает ±32 кадра. Пороговые значения проверяются синтетическими положительными и отрицательными сценариями, не являются настройкой под конкретное устройство. Даже `SEQUENCE_STATUS: MATCHED` не означает измеренную задержку: для `MEASURED` по-прежнему обязательны пригодные clock fits, непрерывные Capture positions/timestamps, отсутствие clipping и остальные временные проверки. Clock fits требуют минимум 8 валидных снимков и R² не ниже 0.95; Capture packet fit дополнительно ограничивает расхождение темпа ±500 ppm. Эти пороги — фильтры пригодности данных, а не калиброванная оценка неопределённости.

Независимо от порога отчёт показывает глобальный максимум абсолютной корреляции, offset/polarity, число независимых кандидатов выше и ниже порога и до пяти сильнейших кандидатов. Если marker не обнаружен, выводится реальный максимум корреляции и причина отбраковки. Render счётчики показывают фактически сформированные ненулевые кадры/peak и сколько маркеров было начато и полностью отправлено. Это помогает отделить отсутствие сигнала на Capture от отсутствия маркера на Render. Субсэмпловая позиция оценивается параболической интерполяцией пика. Неудачная привязка выводит `TIMING_ALIGNMENT_UNRELIABLE` и не может дать `LOOPBACK_STATUS: MEASURED`. См. [IAudioClock::GetPosition](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition) и [IAudioCaptureClient::GetBuffer](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer). Результат — оценка физического аналогового пути по Windows/driver timestamps; он не является задержкой драйвера, акустикой громкоговорителя или задержкой Live Monitor.

Первые два аппаратных прогона Audient iD4 завершились Duplex/CLEAN, но не нашли маркеры: Capture peak/RMS были −80.77/−93.59 dBFS и −32.97/−67.40 dBFS; во втором прогоне packet-fit residual составил 46.829 frames. В следующем запуске Render передал 20/20 маркеров (10 180 ненулевых кадров, −36.01 dBFS peak); Capture peak/RMS составили −51.42/−75.25 dBFS. Максимум корреляции 0.591447 был ниже 0.80, но пять сильнейших кандидатов имели ожидаемые интервалы по 12 000 кадров и согласованные знаки. Предыдущая версия всё ещё выдала `SIGNAL_NOT_FOUND`; это свидетельство физического сигнала, не измерение задержки. Новая sequence detection ожидает физического повтора. Уровень генератора −36 dBFS и per-marker threshold 0.80 сохранены.

Таймер потока использует `steady_clock`: достижение заданной длительности завершает тест штатно, промежуточный `WAIT_TIMEOUT` лишь начинает новый ограниченный wait и не считается предупреждением, а отсутствие событий в течение 1 секунды с последнего события фиксируется как отказ watchdog. `Ctrl+C` отражается как отмена и не может дать успешный статус. Ошибка `WaitForSingleObject` сохраняется отдельно и не маскируется deadline-ом. Логика deadline одинакова при `--mmcss off` и `--mmcss pro-audio`.

Для фактической проверки короткого event-driven потока используйте только подтверждённую комбинацию 48 кГц / Stereo PCM16 и длительность 1–30 секунд:

```powershell
.\build\Release\KlidanAudioProbe.exe --capture-id '<endpoint id>' --test-stream --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum
.\build\Release\KlidanAudioProbe.exe --render-id '<endpoint id>' --test-stream --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum
.\build\Release\KlidanAudioProbe.exe --capture-id '<endpoint id>' --test-stream --duration 5 --rate 48000 --channels 2 --format pcm16 --period minimum --mmcss pro-audio
```

Capture-пакеты только учитываются и освобождаются; аудиосэмплы не сохраняются. Render передаёт только штатную WASAPI тишину. `STREAM_COMPLETED` подтверждает лишь ограниченный тест `Start`/event/block processing/`Stop`, не длительную стабильность и не сквозную задержку. Запуск может занять Exclusive endpoint.

## Документы

- [Архитектура](docs/ARCHITECTURE.md) — границы подсистем и независимость от KLIDAN.
- [План разработки](docs/ROADMAP.md) — версии 0.1–1.0 и критерии готовности.
- [Безопасность измерений](docs/TEST_SAFETY.md) — условия реального захвата/вывода и честная диагностика.
- [Формат профиля](docs/PROFILE_FORMAT.md) — versioned JSON profile contract и evidence boundaries.
- [Hardware Compatibility Matrix](docs/HARDWARE_COMPATIBILITY.md) — обезличенная матрица операторских наблюдений.
- [Windows installer](docs/WINDOWS_INSTALLER.md) — воспроизводимая Windows packaging procedure.
- [JSON schemas](docs/schemas/) — versioned report/profile schemas.

Проект распространяется под [Apache License 2.0](LICENSE). Обязательные notices для RapidJSON и Microsoft .NET runtime находятся в [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md); эти notices не заменяются Apache License.
