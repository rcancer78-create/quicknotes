# ADR-003: WebView2 запрещён по умолчанию

- Дата: 2026-09-10
- Статус: Accepted (`ROADMAP.md` §11 WebView2 WYSIWYG = `[N]`)
- Реализовано в коде: да в смысле отсутствия зависимости; WYSIWYG на WebView2 не добавлялся
- Пересмотр: только после измеренного пользовательского ограничения текстового предпросмотра *и* security review; эксперимент ограничен двумя неделями spike
- Связанные документы: [VISION.md](../../VISION.md), [ROADMAP.md](../../ROADMAP.md)

## Контекст

Предпросмотр уже строится через `MarkdownPreviewService` на WPF `FlowDocument`. Подключение Evergreen WebView2 добавило бы runtime вне контроля приложения, HTML как параллельный формат и новую границу plaintext защищённых заметок (`VISION.md`).

В `QuickNotes.App.csproj` нет пакета WebView2. Поиск `WebView2` по `.cs`/`.xaml` приложения пуст.

## Решение

- **Уже в коде.** Редактор и preview — TextBox / `FlowDocumentScrollViewer` (`NoteEditorWindow.xaml`, тест `NoteEditorWindow_UsesDynamicBrushesForEditingAndPreview`).
- **Принятое направление.** WebView2, HTML как источник истины, JS-бандл и браузерное хранение защищённого текста не входят в продукт без нового ADR.
- **Эксперимент (не начат).** Двухнедельный spike допустим только если: (1) измеренное ограничение текущего preview на реальных заметках; (2) security review; (3) spike не меняет формат хранения `Note.Text`; (4) защищённый plaintext не уходит в WebView. Без этих условий код WebView2 не сливается.

## Альтернативы

- Включить WebView2 «для удобства» параллельно Markdown — отвергнуто.
- Отложить решение без `[N]` — отвергнуто: ROADMAP уже отклоняет функцию.

## Последствия

Release 1.1 («Страницы») обязан использовать существующий WPF-рендерер. Появление WebView2 в csproj без замены этого ADR — дефект процесса.

## Проверки

- Отсутствие package/reference: `QuickNotes.App/QuickNotes.App.csproj`.
- `MarkdownPreviewServiceTests`.
- Ручная матрица preview — ещё не закрыта (`ROADMAP.md` M5); это не разрешение на WebView2.
