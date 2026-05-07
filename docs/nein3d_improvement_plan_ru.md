# План улучшения Nein3D до рабочей 3D-студии

Дата: 07.05.2026

## Что я увидел в проекте

- Основа проекта - Kimodo: генерация motion по тексту, таймлайн, constraints, экспорт NPZ/BVH/CSV/AMASS/USD/FBX.
- Windows-запуск уже вынесен в `packaging/windows/nein3d_launcher.py`: поднимает text encoder, UI и открывает WebView-окно.
- В интерфейсе есть вкладки генерации, файлов, экспорта, просмотра и USD 3D.
- USD loader сейчас читает polygon mesh из `.usd/.usda/.usdc/.usdz`, но пока не использует материалы, скининг, анимацию и иерархические материалы.
- В проекте уже есть демо-motion-примеры, но не было отдельного набора тестовых 3D-ассетов уровня "стандартный mannequin для проверки сцены".

## Что я добавил сейчас

- Встроенные тестовые USD-ассеты:
  - `Nein3D Manny (UE-style test)`
  - `Nein3D Quinn (UE-style test)`
  - `Nein3D Greybox test set`
- В UI в блоке `Вид -> USD 3D` добавлен выбор тестовой модели.
- Путь к выбранной модели автоматически подставляется в поле `Путь к файлу`.
- Ассеты процедурные, не копируют Epic/Unreal geometry и безопасны для хранения в репозитории.

## Ближайшие улучшения

1. Assets panel
   - Сделать отдельную вкладку `Ассеты`.
   - Показывать список встроенных и пользовательских USD/GLB/FBX.
   - Добавить превью, размер, количество mesh-объектов, bounding box.

2. USD loader v2
   - Читать `displayColor`, материалы и прозрачность из USD.
   - Поддержать transform hierarchy без потери имен.
   - Отдельно грузить meshes, lights, cameras и simple collision/debug shapes.
   - Добавить GLB/GLTF loader как быстрый путь для игровых ассетов.

3. Unreal/Blender pipeline
   - Пресеты экспорта: Unreal, Blender, Unity.
   - Проверка осей, масштаба и frame rate перед экспортом.
   - Для FBX оставить Blender bridge, но добавить диагностику установки Blender.
   - Для USD добавить вариант с motion + reference mesh.

4. Тестовые сцены
   - Набор mannequin-сцен: T-pose, walking scale, doorway, stairs, platform.
   - Автотест: открыть каждый USD и проверить число meshes/faces/vertices.
   - Smoke-test UI: загрузка тестовой модели без падения.

5. Продуктовая оболочка
   - Формат проекта `.nein3d` или папка проекта.
   - Autosave и список последних проектов.
   - Экран диагностики: GPU, RAM, VRAM, активная модель, порты, Blender, USD runtime.

## Важное решение по лицензиям

Оригинальные Unreal Engine Manny/Quinn/UE mannequin assets не кладем в git без отдельной проверки лицензии. Для стандартных тестов используем собственные процедурные модели или явно открытые CC0/MIT/CC-BY ассеты с attribution-файлом.
