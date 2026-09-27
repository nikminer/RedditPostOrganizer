# Организация проекта

Постоянная папка: `C:\Users\nikmi\Documents\Projects\RedditGallery`.
Внутри — обычный локальный Git-репозиторий `.git`, решение `RedditGallery.sln`, исходники C#/Avalonia, тесты, документация и сценарии сборки. Прежние коммиты сохранены. GitHub remote не настроен: публикация репозитория отдельно не выполнялась.

Откройте решение в Rider или Visual Studio с поддержкой .NET 10. Команды выполняются из корня:

```powershell
./setup-dev.ps1
./test.ps1
./run-dev.ps1
./build.ps1
```

Для отдельной тестовой базы: `dotnet run --project src/Gallery.Desktop -- --data-dir "C:\Temp\GalleryDev"`.

Результат сборки: `dist/RedditGallery/Reddit Post Organizer.exe`; архив: `releases/Reddit-Post-Organizer-Portable.zip`. Старая заменённая сборка сохраняется в `work/previous-runtime-*`; старые релизы также остаются в `releases`. Эти каталоги не входят в Git. Пользовательские данные, OAuth-токены, SQLite, медиахранилища, bin/obj и временные файлы исключены из репозитория.

Для рассмотрения заявки Reddit приложите репозиторий с `API_ACCESS.md`. Не добавляйте папку данных, собственные фотографии, токены или резервные копии. Client ID вводится через интерфейс после получения доступа; Client Secret для installed app не нужен.

Windows smoke test готового EXE (отдельная пустая папка обязательна):

```powershell
& "./dist/RedditGallery/Reddit Post Organizer.exe" --data-dir "C:\Temp\GallerySmoke" --smoke-test --tray
```

Он создаёт SQLite, запускает Avalonia и трей, записывает `smoke-test.json` и завершает приложение без обращений к Reddit и без запуска очереди.
