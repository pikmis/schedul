# Schedule I — база русских переводов

Файл `TranslaitBROO.txt` подтягивается модом `ScheduleI URL DLL` (Schedule I Russian Translation) с GitHub при запуске игры — обновляйте переводы здесь, и они разойдутся по всем игрокам без пересборки DLL.

## Формат

- Каждая строка: `оригинал=перевод`, UTF-8 без BOM.
- Первая строка **обязательна**: `# SCHEDULEI_RU_BASE` (иначе DLL отклонит файл).
- Пустые строки и строки на `#` игнорируются.
- Левая часть (ключ) и теги/плейсхолдеры не изменять; поздние дубли перекрывают ранние.

## Источники в DLL

- Основной: `https://raw.githubusercontent.com/pikmis/schedul/main/TranslaitBROO.txt`
- Зеркало (если raw заблокирован/тормозит): `https://cdn.jsdelivr.net/gh/pikmis/schedul@main/TranslaitBROO.txt`

Кэш последней удачной загрузки DLL хранит у себя в `BepInEx\config\TranslaitBROO.remote.txt`, поэтому офлайн игра продолжает работать.
