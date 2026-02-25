# Avatar Desktop Prototype (Windows, WPF, LM Studio, USD-ready)

Тестовый Windows-проект desktop-аватара:

- UI на `WPF` (`.NET 8`, C#)
- локальное подключение к `LM Studio` по OpenAI-compatible API (`/v1/chat/completions`)
- строгий JSON-протокол команд для аватара (`text + mood + action + duration_ms`)
- контроллер состояний анимации (`Idle/Listening/Thinking/Speaking/Acting`)
- 3D-заглушка (куб через `Viewport3D`) с архитектурой под дальнейшую подстановку USD-рендера

## Что реализовано

- Окно приложения с:
  - полем ввода текста
  - кнопкой `Send`
  - кнопкой `Health Check (/models)`
  - логами
  - статусом подключения к LM Studio
  - параметрами `base_url`, `model`, `temperature`, `max_tokens`
  - областью 3D-сцены (заглушка-куб)
- `Offline Demo Mode` (без LM Studio, локальная эмуляция команд)
- окно `ChatGPT Voice...` для голосовых turn-based диалогов через OpenAI API:
  - `OpenAI STT` (`/v1/audio/transcriptions`)
  - `Chat Completions` (`/v1/chat/completions`, JSON-команда аватара)
  - `OpenAI TTS` (`/v1/audio/speech`)
- `Realtime Dialog` через OpenAI Realtime API (`gpt-realtime`) для низкой задержки и разговора “как в ChatGPT app”
- Клиент LM Studio:
  - `GET /v1/models` (health check)
  - `POST /v1/chat/completions`
  - таймауты и обработка ошибок
- Системный промпт для строгого JSON-ответа
- Fallback-парсер, если модель вернула невалидный JSON
- Интерфейс `IAvatarRenderer` для будущей USD-интеграции:
  - `LoadUsd(path)`
  - `SetAnimation(name)`
  - `SetBlendshape(name, value)`
  - `Update(dt)`
- Заглушка TTS: `ITextToSpeech` + логгер `"(TTS) ..."`

## Структура

- `apps/AvatarDesktop` — WPF приложение
- `assets` — заглушки под USD-ассеты
- `docs/SETUP.md` — пошаговая инструкция
- `scripts` — сборка/запуск/health-check

## Требования

- Windows 10/11
- `.NET 8 SDK` (не только runtime)
- LM Studio с запущенным Local Server (OpenAI-compatible)

Важно: по умолчанию в конфиге используется `http://127.0.0.1:1234/v1`.

## Как включить LM Studio server

1. Откройте LM Studio.
2. Загрузите локальную модель.
3. Перейдите в раздел Local Server / Developer.
4. Запустите сервер OpenAI-compatible API.
5. Убедитесь, что endpoint доступен по:
   - `http://127.0.0.1:1234/v1/models`

## Сборка и запуск

PowerShell (из корня репозитория):

```powershell
dotnet restore .\apps\AvatarDesktop\AvatarDesktop.csproj
.\scripts\build.ps1
.\scripts\run.ps1
```

Или напрямую:

```powershell
dotnet restore .\apps\AvatarDesktop\AvatarDesktop.csproj
dotnet build .\apps\AvatarDesktop\AvatarDesktop.csproj -c Debug
dotnet run --project .\apps\AvatarDesktop\AvatarDesktop.csproj
```

## Быстрый запуск без ИИ (offline demo)

После запуска приложения ничего дополнительно поднимать не нужно:

1. Оставьте включенным `Offline Demo Mode` (включен по умолчанию).
2. Введите любой текст.
3. Нажмите `Send` (или `Enter`).

Приложение сгенерирует локальную JSON-команду, покажет текст, запишет лог и запустит демо-“анимацию” куба.

## ChatGPT Voice (OpenAI API, микрофон -> ChatGPT -> голос)

1. Запустите приложение
2. Нажмите `ChatGPT Voice...`
3. Введите `API Key`
4. Нажмите `Test API (/models)`
5. Нажмите `Start Recording`, скажите фразу в микрофон
6. Нажмите `Stop + Send`

Что происходит:

- аудио уходит в OpenAI STT
- текст отправляется в ChatGPT с JSON-протоколом для аватара
- кубик получает команду и анимируется
- ответ озвучивается голосом через OpenAI TTS

## Realtime Dialog (рекомендуется для минимальной задержки)

В окне `ChatGPT Voice...` есть блок `Realtime Dialog (Low Latency)`:

1. Укажите `API Key`
2. Оставьте дефолты:
   - `Realtime Model = gpt-realtime`
   - `Realtime Voice = marin`
3. Нажмите `Connect Live`
4. Нажмите `Mic On`
5. Говорите как в диалоге

Это режим с меньшей задержкой, чем turn-based запись (`Start Recording -> Stop + Send`).

## Протокол ответа модели (строго JSON)

Модель должна отвечать только JSON-объектом:

```json
{
  "text": "короткий ответ пользователю",
  "mood": "neutral|happy|sad|angry|curious",
  "action": "idle|wave|dance_01|think|nod|shrug",
  "duration_ms": 500
}
```

Если модель вернет невалидный формат, приложение применит fallback:

- `action = think`
- `mood = neutral`
- `duration_ms = 800`
- `text = сырой ответ модели`

## Где подключать USD позже

Точка расширения: `apps/AvatarDesktop/Rendering/IAvatarRenderer.cs`

Текущая реализация: `apps/AvatarDesktop/Rendering/CubeAvatarRenderer.cs` (заглушка).

Позже можно заменить на адаптер рендера USD (Omniverse/OpenUSD/native viewer) без изменения UI/LM Studio/AnimationController логики.
