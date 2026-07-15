Добавляем еще один режим в приложение. Он должен включаться параметром командной строки --review.

Таже через параметры командной строки должны передаваться

1) идентификатор проекта на гитлабе
2) идентификатор мердж реквеста в проекте на гитлабе

После индексации (если она еще не сделана) и подключения MCP серверов должен отправляться запрос к LLM чтобы она инициировала вызов инструмента get_merge_request_diffs, после чего получив изменения из мердж реквеста с помощью этого инстурмента сформировала комментарий ревью оценив потенциальные баги, архитектурные проблемы и рекомендации. При формаировании комментария нужно использовать информацию из RAG.

Полученный от LLM комментарий ревью приложение должно вывести в консоль

Рассуждая последовательно подготовь план для реализации данной функциональности

---

Now I have a complete understanding of the codebase. Let me formulate the implementation plan.

---

## План реализации режима `--review`

### 1. Расширение парсинга CLI аргументов (`Program.cs`)

**Что:** Добавить в существующий парсер (строки 45-58) распознавание новых флагов:
- `--review` — включает режим ревью (boolean flag)
- `--project-id <value>` — идентификатор проекта GitLab (string, required when --review)
- `--merge-request-iid <value>` — идентификатор мердж реквеста (string, required when --review)

**Изменения:**
- `Program.cs`, строки 43-58: добавить переменные `runReview` (bool), `projectId` (string?), `mergeRequestIid` (string?)
- Обновить строку Usage (строки 6-32) с описанием нового режима
- Валидация: при `--review` оба параметра (`project-id`, `merge-request-iid`) обязательны

### 2. Новый метод `RunReviewAsync` в `Program.cs`

**Что:** Создать новый async-метод, аналогичный `RunChatAsync`, но для однократного автоматического ревью.

**Последовательность:**
1. Открыть векторное хранилище (если индекс не существует — пропустить, но вывести предупреждение)
2. Проверить доступность LLM API (как в `RunChatAsync`, строки 243-263)
3. Подключить MCP серверы (git + zereight-mcp-gitlab, как в `RunChatAsync`, строки 271-290)
4. Сформировать промпт для LLM с инструкцией вызвать `get_merge_request_diffs` с переданными project_id и merge_request_iid
5. Дождаться результата вызова инструмента (diff мердж-реквеста)
6. Выполнить RAG-поиск по репозиторию для контекста (архитектурные паттерны, стайлгайд и т.д.)
7. Сформировать финальный промпт для LLM: diff + RAG-контекст = запрос на ревью-комментарий
8. Вывести полученный комментарий в консоль

**Подпись метода:**
```csharp
static async Task RunReviewAsync(string targetDir, string resolvedTargetDir, 
    string projectId, string mergeRequestIid)
```

### 3. Метод для автоматического вызова `get_merge_request_diffs` через LLM

**Что:** Вспомогательный метод, который отправляет запрос к LLM с инструкцией вызвать инструмент `get_merge_request_diffs`, затем извлекает результат.

**Реализация:** Повторить логику tool-calling из `ChatService.ProcessWithToolCallingAsync` (строки 144-468), но в упрощённом виде:
- Отправить system prompt с определением MCP инструментов (через `ToolPromptBuilder.BuildSystemPromptWithTools`)
- Отправить user message с инструкцией: "Вызови инструмент get_merge_request_diffs с параметрами project_id={projectId}, merge_request_iid={mergeRequestIid}"
- Парсить ответ LLM: либо `tool_calls` (OpenAI формат), либо XML/backtick-обёрнутый вызов (`ExtractXmlToolCalls`)
- Выполнить `mcpManager.CallToolAsync("get_merge_request_diffs", args)` через существующий `_mcpManager`
- Вернуть строку с diff

### 4. RAG-контекст для ревью

**Что:** Перед формированием финального запроса к LLM выполнить RAG-поиск по репозиторию для получения контекста.

**Реализация:**
- Создать `EnhancedRagPipeline` с существующими компонентами (embedding service, vector store)
- Выполнить `ExecuteAsync` с query типа: "code review best practices, architecture patterns, common bugs in this project"
- Использовать результат (chunks) как контекст для формирования промпта ревью

### 5. Формирование финального промпта для LLM-ревью

**Что:** Собрать все данные (diff + RAG chunks) в промпт для LLM с инструкцией на ревью.

**Структура system prompt:**
- Информация о доступных MCP инструментах (если подключены)
- Инструкция: проанализировать diff мердж-реквеста, оценить потенциальные баги, архитектурные проблемы, дать рекомендации
- Использовать контекст из RAG (найденные чанки о проекте)

**Структура user message:**
```
=== MERGE REQUEST DIFF ===
{diff content from get_merge_request_diffs}

=== PROJECT CONTEXT (from RAG) ===
{top K chunks from EnhancedRagPipeline}

Please provide a code review comment covering:
1. Potential bugs and edge cases
2. Architecture concerns
3. Recommendations for improvement
```

### 6. Вывод результата в консоль

**Что:** После получения ответа от LLM вывести его в консоль с форматированием.

**Реализация:**
- Распечатать заголовок `=== CODE REVIEW: MR #{mergeRequestIid} ===`
- Вывести ответ LLM (очищенный от JSON/citation обёрток, если есть)
- Вывести источники из RAG (если использовались)

### 7. Обновление `prompt.md` (опционально)

Удалить или обновить файл `D:\RiderProjects\Assistant\prompt.md`, так как задача будет реализована.

---

### Сводная таблица файлов для изменения:

| Файл | Изменения |
|---|---|
| `Assistant/Program.cs` | Парсинг CLI (`--review`, `--project-id`, `--merge-request-iid`); новый метод `RunReviewAsync`; вспомогательный метод для tool-calling |
| `Assistant/ChatService.cs` | Без изменений (использует существующие методы tool-calling) |
| `Assistant/McpServerManager.cs` | Без изменений (использует существующий `ConnectAsync`, `CallToolAsync`) |
| `Assistant/ToolPromptBuilder.cs` | Без изменений (использует существующий `BuildSystemPromptWithTools`) |
| `prompt.md` | Удалить/обновить (задача реализована) |

### Зависимости между компонентами:
- `RunReviewAsync` использует существующие: `SqliteVectorStore`, `OpenAiCompatibleEmbeddingService`, `OpenAiCompatibleLlmService`, `McpServerManager`, `EnhancedRagPipeline`, `ToolPromptBuilder`
- Tool-calling логика дублируется из `ChatService.ProcessWithToolCallingAsync` в упрощённом виде (без интерактивного цикла, без валидации цитирований)
- RAG pipeline используется для получения контекста проекта перед формированием ревью