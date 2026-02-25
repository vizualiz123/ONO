# SETUP (Windows / VS Code)

## A) Ты (Codex) создаёшь файлы/код

Сделано в этом репозитории:

1. Создана структура:
   - `README.md`
   - `docs/SETUP.md`
   - `apps/AvatarDesktop`
   - `assets`
   - `scripts`
2. Реализовано WPF-приложение `.NET 8` с UI:
   - ввод текста
   - `Send`
   - лог
   - статус LM Studio
   - настройки `base_url/model/temperature/max_tokens`
   - `Health Check (/models)`
3. Реализован клиент LM Studio (OpenAI-compatible API):
   - `GET /v1/models`
   - `POST /v1/chat/completions`
   - таймауты и обработка ошибок
4. Реализован протокол JSON-команд и fallback-парсер
5. Реализован `AnimationController`:
   - state machine `Idle/Listening/Thinking/Speaking/Acting`
   - логирование переходов
   - таймер действия по `duration_ms`
6. Реализован `IAvatarRenderer` и заглушка `CubeAvatarRenderer`
7. Добавлен `appsettings.json` с дефолтным `base_url = http://127.0.0.1:1234/v1`
8. Добавлены скрипты:
   - `scripts/build.ps1`
   - `scripts/run.ps1`
   - `scripts/health-check.ps1`
9. Добавлена заготовка TTS:
   - `ITextToSpeech`
   - `LoggingTextToSpeech`

## B) Пользователь: ставит зависимости, включает LM Studio server, запускает build, проверяет тест

### 1. Установить зависимости

Установить:

1. `.NET 8 SDK` (именно SDK, не только runtime)
2. LM Studio (если ещё не установлен)
3. VS Code + C# extension (рекомендуется)
4. (Для ChatGPT Voice) работающий микрофон в Windows

Проверка:

```powershell
dotnet --list-sdks
```

Ожидается хотя бы одна строка `8.0.xxx`.

### 2. Запустить LM Studio Local Server

1. Открыть LM Studio
2. Загрузить модель
3. Включить Local Server / OpenAI-compatible API
4. Проверить endpoint:

```powershell
.\scripts\health-check.ps1
```

По умолчанию скрипт проверяет `http://127.0.0.1:1234/v1/models`.

### 3. Собрать проект

Из корня репозитория:

```powershell
.\scripts\build.ps1
```

Или напрямую:

```powershell
dotnet build .\apps\AvatarDesktop\AvatarDesktop.csproj -c Debug
```

### 4. Запустить приложение

```powershell
.\scripts\run.ps1
```

Или:

```powershell
dotnet run --project .\apps\AvatarDesktop\AvatarDesktop.csproj
```

### 5. Проверить тестовый сценарий

#### Вариант 1: без LM Studio (быстрый офлайн-тест)

1. Запустить приложение
2. Убедиться, что включен `Offline Demo Mode` (по умолчанию включен)
3. Ввести текст
4. Нажать `Send` / `Enter`
5. Проверить:
   - появляется текст ответа `[DEMO] ...`
   - состояние переключается (`Listening -> Thinking -> Speaking -> Acting -> Idle`)
   - куб меняет поведение/цвет
   - в логах есть `DemoCmd`

#### Вариант 2: с LM Studio

1. Нажать `Health Check (/models)`:
   - статус должен стать `connected` (если LM Studio работает)
   - если включен `Offline Demo Mode`, проверка будет пропущена (это нормально)
2. Ввести текст в поле ввода
3. Нажать `Send` (или `Enter`)
4. Проверить:
   - в логах есть запрос/ответ
   - в блоке `Avatar Response Text` показан `text`
   - в логах есть `AvatarCmd` с `mood/action/duration_ms`
   - состояние аватара переключается (`Listening -> Thinking -> Speaking -> Acting -> Idle`)
   - куб в 3D-окне меняет поведение/цвет (заглушка “анимации”)

#### Вариант 3: ChatGPT Voice (OpenAI API, без Windows TTS/Speech)

1. Запустить приложение
2. Нажать `ChatGPT Voice...`
3. Ввести OpenAI `API Key`
4. Нажать `Test API (/models)`
5. Нажать `Start Recording`, сказать фразу
6. Нажать `Stop + Send`
7. Проверить:
   - в логах окна Voice есть `STT`, `Chat`, `TTS`
   - кубик анимируется по JSON-команде
   - слышен голос, сгенерированный OpenAI TTS

#### Вариант 4: Realtime Dialog (OpenAI Realtime API, низкая задержка)

1. Открыть `ChatGPT Voice...`
2. Вставить OpenAI `API Key`
3. Убедиться, что:
   - `Realtime Model = gpt-realtime`
   - `Realtime Voice = marin`
4. Нажать `Connect Live`
5. Нажать `Mic On`
6. Говорить голосом как в диалоге

Проверка:

1. В логах есть строки `[Realtime] ...`
2. Статус realtime становится `connected/listening/assistant speaking`
3. Кубик переключает состояния `Listening/Thinking/Speaking`

### 6. Что подставить позже (USD pipeline)

Менять реализацию в:

- `apps/AvatarDesktop/Rendering/IAvatarRenderer.cs`
- текущая заглушка: `apps/AvatarDesktop/Rendering/CubeAvatarRenderer.cs`

Можно подключить:

1. нативный OpenUSD/Omniverse renderer adapter (через C++/CLI или IPC)
2. отдельный viewer-процесс + bridge
3. ваш runtime, поддерживающий USD-анимации и blendshape

UI и LM Studio клиент переписывать не потребуется.
