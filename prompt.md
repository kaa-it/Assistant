## План: Пост-ревью — публикация комментария в GitLab MR

### Контекст
Режим `--review` уже реализован и выводит сгенерированный LLM комментарий ревью в консоль. Необходимо добавить шаг **после** вывода комментария — инициировать через LLM вызов MCP-инструмента `create_merge_request_note` для публикации этого комментария в GitLab MR.

### Доступный инструмент (zereight-mcp-gitlab)
```javascript
// create_merge_request_note parameters:
{
  project_id: string;       // Project ID или URL-encoded path
  merge_request_iid: string; // IID мердж-реквеста  
  body: string;             // Текст комментария (ревью)
}
```

### Последовательность шагов после вывода ревью в консоль:

**Step 7 (новый): Отправить LLM инструкцию опубликовать ревью через MCP `create_merge_request_note`**

После вывода cleaned review-комментария в консоль (строки 610-645 Program.cs), добавить блок:

```csharp
// Step 7: Post review comment to GitLab MR via MCP tool
if (mcpManager != null && mcpManager.IsConnected)
{
    try
    {
        Console.WriteLine("\n=== Публикация комментария ревью в GitLab MR... ===");
        
        // Отправить LLM с инструкцией вызвать create_merge_request_note
        var posted = await PostReviewCommentViaMcpAsync(
            llmApiUrl, llmModel, llmApiKey, 
            mcpManager, projectId, mergeRequestIid, cleaned);
        
        if (posted)
            Console.WriteLine("Комментарий ревью успешно опубликован в GitLab MR.");
        else
            Console.WriteLine("Не удалось опубликовать комментарий — LLM не вернул корректный вызов инструмента.");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Предупреждение: не удалось опубликовать комментарий в GitLab: {ex.Message}");
        Console.ResetColor();
    }
}
else
{
    Console.WriteLine("\n[Примечание] MCP сервер GitLab не подключён — комментарий не опубликован в MR.");
    Console.WriteLine("Для публикации комментария запустите с переменными GITLAB_PERSONAL_ACCESS_TOKEN и GITLAB_API_URL.");
}
```

### Новый вспомогательный метод: `PostReviewCommentViaMcpAsync`

**Подпись:**
```csharp
static async Task<bool> PostReviewCommentViaMcpAsync(
    string llmApiUrl, string llmModel, string? llmApiKey,
    McpServerManager mcpManager, 
    string projectId, string mergeRequestIid,
    string reviewComment)
```

**Логика:**
1. Отправить запрос к LLM с system prompt, содержащим определение MCP инструментов (через `ToolPromptBuilder.BuildSystemPromptWithTools`, как в `ChatService.ProcessWithToolCallingAsync` строка 184)
2. User message: "Вызови инструмент `create_merge_request_note` с параметрами project_id='{projectId}', merge_request_iid='{mergeRequestIid}', body='<reviewComment>'"
3. Парсить ответ LLM:
   - Либо native `tool_calls` (OpenAI формат) — строки 400-453 ChatService.cs
   - Либо XML `` блоки — `ExtractXmlToolCalls` строки 736-864 ChatService.cs
   - Либо JSON без обёртки (fallback) — строки 804-861 ChatService.cs
4. Если инструмент найден — выполнить `mcpManager.CallToolAsync("create_merge_request_note", args)`
5. Вывести результат в консоль (успех/ошибка)
6. Вернуть `true` при успехе, `false` если LLM не вернул вызов инструмента

### Интеграция в `RunReviewAsync` (Program.cs)

Вставить после строки 645 (после блока вывода ревью в консоль), но **перед** строками 647-650 (dispose):

```csharp
// === Step 7: Post review comment to GitLab MR (NEW) ===
if (mcpManager != null && mcpManager.IsConnected)
{
    try { ... } catch { ... }
}

// === End Step 7 ===

llm.Dispose();
if (mcpManager != null) await mcpManager.DisposeAsync();
```

### Сводная таблица изменений:

| Файл | Изменения |
|---|---|
| `Assistant/Program.cs` | Добавить блок Step 7 в `RunReviewAsync` (после строки ~645); добавить метод `PostReviewCommentViaMcpAsync` (после строки ~650) |
| `Assistant/Program.cs` | Добавить `using System.Text.RegularExpressions;` (если ещё не добавлен) для парсинга XML-блоков |
| `Assistant/ChatService.cs` | Без изменений (использует существующие методы tool-calling) |
| `Assistant/McpServerManager.cs` | Без изменений (использует существующий `CallToolAsync`) |
| `Assistant/ToolPromptBuilder.cs` | Без изменений (использует существующий `BuildSystemPromptWithTools`) |

### Зависимости:
- `PostReviewCommentViaMcpAsync` использует существующие: `ToolPromptBuilder.BuildSystemPromptWithTools`, `mcpManager.CallToolAsync`
- Логика парсинга ответа LLM (tool_calls, XML `` блоки, JSON fallback) — дублируется из `ChatService.ProcessWithToolCallingAsync` (строки 400-861) в упрощённом виде
- `cleaned` review-комментарий из Step 6 передаётся как аргумент `body` для `create_merge_request_note`
