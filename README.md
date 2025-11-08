## 🎯 Общая архитектура

```
HTTP Client → ASP.NET Controller → Channel → Background Processor → Status Dictionary
     ↓              ↓                    ↓           ↓                    ↓
   Запрос →   Создание задачи →   Очередь задач → Обработка →   Хранение статусов
```

## 1. 🚀 Запуск приложения

### Startup Flow:
```csharp
// Program.cs
builder.Services.AddSingleton<ITaskProcessor, TaskProcessor>();
builder.Services.AddHostedService(provider => (TaskProcessor)provider.GetRequiredService<ITaskProcessor>());
```

**Что происходит:**
1. При старте приложения создается `TaskProcessor`
2. Запускается `IHostedService` → вызывается `StartAsync()`
3. Запускается фоновая задача `ProcessTasksAsync()`

```csharp
public Task StartAsync(CancellationToken cancellationToken)
{
    _logger.LogInformation("Starting task processor service");
    _processingTask = ProcessTasksAsync(_cancellationTokenSource.Token);
    return Task.CompletedTask;
}
```

## 2. 📥 Пользователь создает задачу

### HTTP Request Flow:
```
POST /api/tasks → TasksController.CreateTask() → ITaskProcessor.SubmitTask()
```

**В контроллере:**
```csharp
[HttpPost]
public async Task<IActionResult> CreateTask([FromBody] TaskRequest request)
{
    var taskId = _taskProcessor.SubmitTask(request);
    return Accepted(new { TaskId = taskId, Status = "Pending" });
}
```

**В процессоре:**
```csharp
public Guid SubmitTask(TaskRequest request)
{
    var taskId = Guid.NewGuid();
    var taskStatus = new TaskStatus { Id = taskId, Status = "Pending" };
    
    // 1. Сохраняем статус в словарь
    _taskStatuses.TryAdd(taskId, taskStatus);
    
    // 2. Отправляем в канал
    _channel.Writer.TryWrite((taskId, request));
    
    return taskId;
}
```

## 3. 🔄 Канал и фоновая обработка

### Channel Architecture:
```
Channel [ (taskId1, request1), (taskId2, request2), ... ]
    ↑                                   ↓
Writer (SubmitTask)              Reader (ProcessTasksAsync)
```

**Процессор ждет задачи:**
```csharp
private async Task ProcessTasksAsync(CancellationToken cancellationToken)
{
    while (await _channel.Reader.WaitToReadAsync(cancellationToken))
    {
        while (_channel.Reader.TryRead(out var item))
        {
            var (taskId, request) = item;
            
            // Ограничиваем параллелизм
            await _semaphore.WaitAsync(cancellationToken);
            
            // Запускаем обработку
            _ = ProcessSingleTaskAsync(taskId, request, cancellationToken)
                .ContinueWith(t => _semaphore.Release());
        }
    }
}
```

## 4. ⚙️ Обработка одной задачи

### Task Processing Flow:
```csharp
private async Task ProcessSingleTaskAsync(Guid taskId, TaskRequest request, CancellationToken cancellationToken)
{
    // 1. Обновляем статус на "Processing"
    status.Status = "Processing";
    status.Progress = 10;

    // 2. Имитируем обработку с прогрессом
    for (int step = 1; step <= 10; step++)
    {
        await Task.Delay(delayPerStep, cancellationToken);
        UpdateProgress(taskId, progress); // 20%, 30%, ... 100%
    }
    
    // 3. Завершаем задачу
    CompleteTask(taskId, request);
}
```

**Пример прогресса:**
```
Шаг 1: 10% → 20% (задержка 1 секунда)
Шаг 2: 20% → 30% (задержка 1 секунда)
...
Шаг 10: 90% → 100% (задержка 1 секунда)
```

## 5. 📊 Пользователь проверяет статус

### Status Check Flow:
```
GET /api/tasks/{id} → TasksController.GetTaskStatus() → ITaskProcessor.GetTaskStatus()
```

**В процессоре:**
```csharp
public TaskStatus GetTaskStatus(Guid taskId)
{
    // Просто получаем статус из ConcurrentDictionary
    return _taskStatuses.TryGetValue(taskId, out var status) ? status : null;
}
```

**Статусы задачи:**
- `Pending` - задача в очереди, ждет обработки
- `Processing` - задача выполняется, прогресс обновляется
- `Completed` - задача успешно завершена
- `Failed` - ошибка при выполнении
- `Cancelled` - задача отменена пользователем

## 6. 🛑 Отмена задачи

### Cancel Flow:
```
DELETE /api/tasks/{id} → TasksController.CancelTask() → ITaskProcessor.CancelTask()
```

```csharp
public bool CancelTask(Guid taskId)
{
    if (status.Status == "Pending" || status.Status == "Processing")
    {
        status.Status = "Cancelled";
        return true;
    }
    return false;
}
```

## 7. 🔧 Ключевые компоненты

### ConcurrentDictionary - хранение статусов
```csharp
private readonly ConcurrentDictionary<Guid, TaskStatus> _taskStatuses;
```
- **Потокобезопасный** - множество запросов могут читать/писать одновременно
- **Быстрый доступ** - O(1) для получения статуса по GUID

### Channel - очередь задач
```csharp
private readonly Channel<(Guid taskId, TaskRequest request)> _channel;
```
- **Producer/Consumer** - контроллеры пишут, фоновая задача читает
- **Ограничение размера** - максимум 1000 задач в очереди
- **Потокобезопасный** - встроенная синхронизация

### SemaphoreSlim - ограничение параллелизма
```csharp
private readonly SemaphoreSlim _semaphore;
private readonly int _maxConcurrentTasks = 5;
```
- **Не более 5 задач** одновременно
- **Предотвращает перегрузку** системы

## 8. 🎪 Пример сценария

### Пользователь 1:
```http
POST /api/tasks
{"data": "Task 1", "processingTimeSeconds": 5}
→ Response: {"taskId": "guid1", "status": "Pending"}
```

### Пользователь 2:
```http
POST /api/tasks  
{"data": "Task 2", "processingTimeSeconds": 3}
→ Response: {"taskId": "guid2", "status": "Pending"}
```

### Система обрабатывает:
1. `guid1` начинает выполняться → статус "Processing", прогресс 10%
2. `guid2` ждет в очереди → статус "Pending"
3. Через 0.5 секунды → `guid1` прогресс 20%, `guid2` все еще "Pending"
4. Когда `guid1` завершается → `guid2` начинает выполняться

### Пользователь проверяет:
```http
GET /api/tasks/guid1
→ {"status": "Completed", "progress": 100%}

GET /api/tasks/guid2  
→ {"status": "Processing", "progress": 30%}
```

## 9. 🛡️ Обработка ошибок и завершение

### Graceful Shutdown:
```csharp
public async Task StopAsync(CancellationToken cancellationToken)
{
    // 1. Запрещаем новые задачи
    _channel.Writer.Complete();
    
    // 2. Отменяем текущую обработку
    _cancellationTokenSource.Cancel();
    
    // 3. Ждем завершения текущих задач (30 сек таймаут)
    await _processingTask.WaitAsync(TimeSpan.FromSeconds(30));
}
```

## 💡 Преимущества этой архитектуры:

1. **Асинхронность** - HTTP запросы не блокируются на время обработки
2. **Масштабируемость** - можно легко добавить несколько consumer'ов
3. **Отказоустойчивость** - задачи не теряются при перезапуске (в памяти)
4. **Контроль нагрузки** - SemaphoreSlim предотвращает перегрузку
5. **Прозрачность** - пользователь видит прогресс выполнения


## 🎯 Что такое Hosted Service?

**Hosted Service** - это служба, которая:
- Запускается при старте приложения
- Работает в фоновом режиме
- Выполняет длительные задачи
- Останавливается при завершении приложения

## 📝 Базовый интерфейс

```csharp
public interface IHostedService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
```

## 🚀 Как работает в нашем примере

### 1. Регистрация в Program.cs
```csharp
// Регистрируем как singleton (общий экземпляр)
builder.Services.AddSingleton<ITaskProcessor, TaskProcessor>();

// Регистрируем как hosted service
builder.Services.AddHostedService(provider => 
    (TaskProcessor)provider.GetRequiredService<ITaskProcessor>());
```

**Альтернативные способы регистрации:**

```csharp
// Способ 1: Если класс реализует IHostedService напрямую
builder.Services.AddHostedService<TaskProcessor>();

// Способ 2: Через провайдер (как в нашем примере)
builder.Services.AddHostedService(provider => 
    provider.GetRequiredService<TaskProcessor>());
```

### 2. Жизненный цикл в нашем TaskProcessor

```csharp
public class TaskProcessor : ITaskProcessor, IHostedService, IDisposable
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ВЫЗЫВАЕТСЯ ПРИ СТАРТЕ ПРИЛОЖЕНИЯ
        _logger.LogInformation("Starting task processor service");
        _processingTask = ProcessTasksAsync(_cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // ВЫЗЫВАЕТСЯ ПРИ ОСТАНОВКЕ ПРИЛОЖЕНИЯ
        _logger.LogInformation("Stopping task processor service...");
        _channel.Writer.Complete();
        _cancellationTokenSource.Cancel();
        await _processingTask.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
```

## 🔄 Полный жизненный цикл приложения

### Запуск приложения:
```
1. dotnet run
2. Build Host → Configure Services → Build
3. ↓
4. StartAsync() всех IHostedService
5. ↓
6. Запуск Kestrel веб-сервера
7. ↓
8. Приложение готово принимать HTTP запросы
```

### Остановка приложения:
```
1. Ctrl+C или остановка сервера
2. ↓
3. StopAsync() всех IHostedService (с таймаутом)
4. ↓
5. Освобождение ресурсов
6. ↓
7. Завершение приложения
```

## 🎪 Реальный пример работы

### При старте приложения:
```csharp
// Вызывается автоматически фреймворком
public Task StartAsync(CancellationToken cancellationToken)
{
    _logger.LogInformation("✅ Task processor started");
    
    // Запускаем фоновую задачу, которая ждет сообщения в Channel
    _processingTask = ProcessTasksAsync(_cancellationTokenSource.Token);
    
    return Task.CompletedTask;
}
```

**Что происходит:**
- Создается фоновая задача `ProcessTasksAsync`
- Она начинает слушать Channel через `WaitToReadAsync`
- Готова обрабатывать задачи от пользователей

### При остановке приложения:
```csharp
public async Task StopAsync(CancellationToken cancellationToken)
{
    _logger.LogInformation("🛑 Stopping task processor...");
    
    // 1. Запрещаем новые задачи
    _channel.Writer.Complete();
    
    // 2. Отменяем текущую обработку
    _cancellationTokenSource.Cancel();
    
    // 3. Ждем завершения текущих задач
    await _processingTask.WaitAsync(TimeSpan.FromSeconds(30));
    
    _logger.LogInformation("✅ Task processor stopped");
}
```

## 📊 Как это выглядит в работе

### Логи при запуске:
```
info: TaskProcessor[0]
      ✅ Starting task processor service
info: TaskProcessor[0]  
      ✅ Task processor started
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

### Логи при работе:
```
// Пользователь создает задачу
info: TasksController[0]
      Created task a1b2c3d4 with processing time 10s

// Фоновая служба обрабатывает
info: TaskProcessor[0]
      Starting processing of task a1b2c3d4
debug: TaskProcessor[0]
      Task a1b2c3d4 progress: 20%
```

### Логи при остановке:
```
info: Microsoft.Hosting.Lifetime[0]
      Application is shutting down...
info: TaskProcessor[0]
      🛑 Stopping task processor service...
info: TaskProcessor[0]
      Task processor stopping due to cancellation  
info: TaskProcessor[0]
      ✅ Task processor stopped
```

## 🎯 Преимущества использования Hosted Service

### 1. **Интеграция с жизненным циклом приложения**
- Автоматический запуск/остановка
- Graceful shutdown (корректное завершение)

### 2. **Встроенная отмена операций**
```csharp
// CancellationToken автоматически передается извне
public Task StartAsync(CancellationToken cancellationToken)
{
    // Если приложение останавливается во время запуска - получим отмену
}
```

### 3. **Dependency Injection**
```csharp
// Можем использовать любые зарегистрированные сервисы
public class TaskProcessor : IHostedService
{
    private readonly ILogger<TaskProcessor> _logger;
    private readonly IEmailService _emailService;
    
    public TaskProcessor(ILogger<TaskProcessor> logger, IEmailService emailService)
    {
        _logger = logger;
        _emailService = emailService;
    }
}
```

## 🔧 Альтернативы Hosted Service

### 1. BackgroundService (абстрактный класс)
```csharp
public class SimpleBackgroundService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Работа в фоне
            await Task.Delay(1000, stoppingToken);
        }
    }
}
```

### 2. Timer-based службы
```csharp
public class TimerService : IHostedService, IDisposable
{
    private Timer _timer;
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }
    
    private void DoWork(object state) { /* ... */ }
}
```

## 💡 В нашем случае:

Мы использовали `AddHostedService` потому что:
- Наш `TaskProcessor` должен работать постоянно
- Нужно обрабатывать задачи из Channel в фоне
- Требуется graceful shutdown при остановке приложения
- Нужна интеграция с DI контейнером

**Без Hosted Service** наш Channel был бы просто очередью, но не было бы автоматической фоновой обработки!

Теперь у нас есть полноценная система:
- ✅ HTTP API для приема задач
- ✅ Фоновая обработка через Hosted Service  
- ✅ Отслеживание статусов
- ✅ Корректное завершение

## 🎯 Назначение SemaphoreSlim

```csharp
private readonly SemaphoreSlim _semaphore;
private readonly int _maxConcurrentTasks = 5;

// В конструкторе:
_semaphore = new SemaphoreSlim(_maxConcurrentTasks, _maxConcurrentTasks);
```

**Что это значит:**
- `_maxConcurrentTasks = 5` - максимум 5 задач могут выполняться одновременно
- `new SemaphoreSlim(5, 5)` - начальное количество = 5, максимальное количество = 5

## 🔄 Как работает SemaphoreSlim

### Принцип работы:
```
SemaphoreSlim как "билетная система":
- Начально: 5 билетов доступно
- Каждая задача берет 1 билет при старте
- Когда билетов нет → новые задачи ждут
- При завершении задачи → возвращает билет
```

## 📝 Конкретный код работы с семафором

```csharp
private async Task ProcessTasksAsync(CancellationToken cancellationToken)
{
    while (await _channel.Reader.WaitToReadAsync(cancellationToken))
    {
        while (_channel.Reader.TryRead(out var item))
        {
            var (taskId, request) = item;
            
            // ⭐ ВЗЯТИЕ БИЛЕТА - ждем пока не освободится место
            await _semaphore.WaitAsync(cancellationToken);
            
            // ⭐ ЗАПУСК ОБРАБОТКИ - теперь есть свободный "слот"
            _ = ProcessSingleTaskAsync(taskId, request, cancellationToken)
                .ContinueWith(t => 
                {
                    // ⭐ ВОЗВРАТ БИЛЕТА - когда задача завершилась
                    _semaphore.Release();
                });
        }
    }
}
```

## 🎪 Визуализация работы

### Пример с 3 задачами и лимитом 2:

```
Время | Semaphore | Задача 1 | Задача 2 | Задача 3
------|-----------|----------|----------|----------
T0    | 2 билета  | STARTED  | PENDING  | PENDING
T1    | 1 билет   | RUNNING  | STARTED  | WAITING  
T2    | 0 билетов | RUNNING  | RUNNING  | WAITING
T3    | 1 билет   | COMPLETE | RUNNING  | STARTED
T4    | 0 билетов | -        | RUNNING  | RUNNING
```

## 🔍 Детальный разбор сценария

### Сценарий: 8 задач поступают одновременно

```csharp
// Представьте что вызывается 8 раз подряд:
var taskId = _taskProcessor.SubmitTask(new TaskRequest 
{ 
    Data = "Task X", 
    ProcessingTimeSeconds = 10 
});
```

**Что происходит:**

1. **Задачи 1-5**: Немедленно получают билеты и начинают выполняться
2. **Задачи 6-8**: Ждут в очереди семафора

```
Состояние системы:
- Выполняются: Task1, Task2, Task3, Task4, Task5
- Ожидают семафор: Task6, Task7, Task8
- SemaphoreSlim: 0 доступных билетов
```

3. **Когда Task1 завершается**:
```csharp
// В ContinueWith вызывается:
_semaphore.Release(); // ↑ увеличивает счетчик с 0 до 1
```

4. **Task6** немедленно получает билет и начинает выполняться
5. Процесс повторяется пока все задачи не выполнятся

## ⚡ Практический пример

```csharp
// Допустим у нас 3 параллельных задачи по 5 секунд каждая

// БЕЗ SemaphoreSlim:
// Все 3 задачи запускаются сразу → 3 параллельных операции
// Общее время: ~5 секунд
// Потребление ресурсов: ВЫСОКОЕ

// С SemaphoreSlim (max = 2):
// Задачи 1 и 2 запускаются сразу
// Задача 3 ждет завершения Task1 или Task2
// Общее время: ~7-8 секунд  
// Потребление ресурсов: КОНТРОЛИРУЕМОЕ
```

## 🛡️ Зачем это нужно в нашем проекте

### 1. **Защита от перегрузки**
```csharp
// Без ограничения:
// 1000 пользователей → 1000 одновременных задач → сервер "падает"

// С SemaphoreSlim:
// 1000 пользователей → 5 одновременных задач → сервер стабилен
// Остальные 995 задач ждут в очереди
```

### 2. **Контроль ресурсов**
- **Память** - ограничение одновременных обработчиков
- **CPU** - не перегружаем процессор
- **Соединения БД** - если бы они использовались

### 3. **Качество обслуживания**
```csharp
// Первые 5 пользователей получают быстрое выполнение
// Остальные ждут, но система не "ложится"
```

## 🔧 Альтернативные подходы

### Без SemaphoreSlim (проблемы):
```csharp
// ПЛОХОЙ КОД - перегрузка сервера
_ = ProcessSingleTaskAsync(taskId, request, cancellationToken);
```

### С ограничением в Channel (менее гибко):
```csharp
// Ограничение только очереди, но не параллелизма
var options = new BoundedChannelOptions(5) { ... };
```

## 📊 Мониторинг работы

Вы можете добавить логирование для отладки:

```csharp
await _semaphore.WaitAsync(cancellationToken);
var currentCount = _maxConcurrentTasks - _semaphore.CurrentCount;
_logger.LogDebug($"Задача {taskId} начала выполнение. Активных задач: {currentCount}");

// ...

_semaphore.Release();
currentCount = _maxConcurrentTasks - _semaphore.CurrentCount; 
_logger.LogDebug($"Задача {taskId} завершена. Активных задач: {currentCount}");
```

## 💡 Ключевые преимущества в нашем случае

1. **✅ Предотвращает "расползание" памяти** - максимум 5 больших объектов одновременно
2. **✅ Стабильность API** - сервер не падает под нагрузкой
3. **✅ Справедливость** - задачи выполняются в порядке поступления
4. **✅ Простота** - всего 2 метода: `WaitAsync()` и `Release()`

SemaphoreSlim в этом проекте работает как **регулировщик движения**, который пропускает только ограниченное количество машин (задач) одновременно, предотвращая заторы и аварии! 🚦



## 🎯 1. Обработка видео и медиа-файлов

```csharp
public class VideoProcessingRequest
{
    public IFormFile VideoFile { get; set; }
    public string OutputFormat { get; set; }
    public int Quality { get; set; }
    public bool ApplyWatermark { get; set; }
}

// Использование
[HttpPost("process-video")]
public IActionResult ProcessVideo([FromForm] VideoProcessingRequest request)
{
    var taskId = _videoProcessor.SubmitTask(request);
    return Accepted(new { taskId, status = "Encoding started" });
}
```

**Преимущества:**
- ✅ Кодирование видео может занимать минуты/часы
- ✅ Пользователь не ждет завершения, получает ID для отслеживания
- ✅ Можно обрабатывать несколько видео параллельно с контролем нагрузки
- ✅ Реальный прогресс: "Анализ видео → Кодирование → Добавление водяного знака → Сохранение"

## 🏥 2. Медицинская диагностика и анализ снимков

```csharp
public class MedicalAnalysisRequest
{
    public IFormFile MriImage { get; set; }
    public string AnalysisType { get; set; } // "tumor_detection", "bone_fracture"
    public bool Urgent { get; set; }
}

// Использование
[HttpPost("analyze-mri")]
public IActionResult AnalyzeMRI([FromForm] MedicalAnalysisRequest request)
{
    var taskId = _medicalProcessor.SubmitTask(request);
    return Accepted(new { 
        taskId, 
        status = "AI analysis queued",
        estimatedTime = request.Urgent ? "5 minutes" : "30 minutes" 
    });
}
```

**Преимущества:**
- ✅ Сложные AI-алгоритмы работают долго
- ✅ Приоритизация: срочные анализы обрабатываются быстрее
- ✅ Врач может отправить несколько снимков и отслеживать прогресс
- ✅ Сохранение результатов для истории пациента

## 📊 3. Генерация сложных отчетов и аналитики

```csharp
public class ReportGenerationRequest
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string[] Departments { get; set; }
    public ReportType Type { get; set; } // Financial, Sales, Analytics
    public ExportFormat Format { get; set; } // PDF, Excel, HTML
}

// Использование
[HttpPost("generate-report")]
public IActionResult GenerateReport([FromBody] ReportGenerationRequest request)
{
    var taskId = _reportProcessor.SubmitTask(request);
    return Accepted(new { taskId, status = "Data collection started" });
}
```

**Преимущества:**
- ✅ Сбор данных из multiple источников (БД, API, файлы)
- ✅ Сложные вычисления и агрегации
- ✅ Прогресс: "Сбор данных → Анализ → Форматирование → Экспорт"
- ✅ Пользователь может закрыть браузер - отчет будет готов позже

## 🛒 4. Массовые операции в e-commerce

```csharp
public class BulkOperationRequest
{
    public IFormFile ProductFile { get; set; } // CSV/Excel
    public OperationType Operation { get; set; } // Import, Update, PriceChange
    public bool SendNotifications { get; set; }
}

// Использование
[HttpPost("bulk-product-import")]
public IActionResult BulkImport([FromForm] BulkOperationRequest request)
{
    var taskId = _bulkProcessor.SubmitTask(request);
    return Accepted(new { 
        taskId, 
        status = "File validation started",
        message = "You will receive email when import completes" 
    });
}
```

**Преимущества:**
- ✅ Обработка тысяч товаров без таймаута
- ✅ Валидация каждого товара с отчетом об ошибках
- ✅ Прогресс: "Валидация → Импорт → Обновление индексов → Отправка уведомлений"
- ✅ Возможность отмены операции

## 🔍 5. Поисковые и SEO задачи

```csharp
public class SeoAnalysisRequest
{
    public string[] Urls { get; set; }
    public AnalysisDepth Depth { get; set; } // Quick, Deep, Comprehensive
    public bool CheckBacklinks { get; set; }
}

// Использование
[HttpPost("analyze-seo")]
public IActionResult AnalyzeSEO([FromBody] SeoAnalysisRequest request)
{
    var taskId = _seoProcessor.SubmitTask(request);
    return Accepted(new { 
        taskId, 
        status = "Starting website crawling",
        estimatedUrls = request.Urls.Length * 1000 
    });
}
```

**Преимущества:**
- ✅ Обход тысяч страниц с паузами (не DDoS)
- ✅ Параллельный анализ multiple сайтов
- ✅ Промежуточные результаты доступны сразу
- ✅ Длительные операции (проверка бэклинков, анализ конкурентов)

## 🎮 6. Генерация игрового контента

```csharp
public class WorldGenerationRequest
{
    public string Seed { get; set; }
    public WorldSize Size { get; set; } // Small, Medium, Large
    public string[] Biomes { get; set; }
    public bool GenerateStructures { get; set; }
}

// Использование
[HttpPost("generate-world")]
public IActionResult GenerateWorld([FromBody] WorldGenerationRequest request)
{
    var taskId = _gameProcessor.SubmitTask(request);
    return Accepted(new { 
        taskId, 
        status = "Terrain generation started",
        estimatedSize = $"{CalculateSize(request.Size)} MB" 
    });
}
```

**Преимущества:**
- ✅ Процедурная генерация требует много вычислений
- ✅ Поэтапный прогресс: "Террейн → Биомы → Структуры → Ресурсы"
- ✅ Возможность предпросмотра частичного результата
- ✅ Отмена генерации, если пользователь передумал

## 📧 7. Массовая рассылка email

```csharp
public class EmailCampaignRequest
{
    public string[] Recipients { get; set; }
    public string TemplateId { get; set; }
    public PersonalizationLevel Personalization { get; set; }
    public DateTime? ScheduleFor { get; set; }
}

// Использование
[HttpPost("send-campaign")]
public IActionResult SendCampaign([FromBody] EmailCampaignRequest request)
{
    var taskId = _emailProcessor.SubmitTask(request);
    return Accepted(new { 
        taskId, 
        status = "Preparing templates",
        totalRecipients = request.Recipients.Length 
    });
}
```

**Преимущества:**
- ✅ Отправка тысяч emails с контролем rate limiting
- ✅ Персонализация каждого письма
- ✅ Отслеживание прогресса: "Подготовка → Отправка → Обработка bounce-писем"
- ✅ Возможность паузы/отмены рассылки

## 🛠️ 8. CI/CD и сборка проектов

```csharp
public class BuildRequest
{
    public string RepositoryUrl { get; set; }
    public string Branch { get; set; }
    public string BuildConfiguration { get; set; }
    public bool RunTests { get; set; }
    public bool DeployToStaging { get; set; }
}

// Использование
[HttpPost("trigger-build")]
public IActionResult TriggerBuild([FromBody] BuildRequest request)
{
    var taskId = _ciProcessor.SubmitTask(request);
    return Accepted(new { 
        taskId, 
        status = "Cloning repository",
        estimatedDuration = "10-15 minutes" 
    });
}
```

**Преимущества:**
- ✅ Длительные процессы: клонирование → установка зависимостей → сборка → тесты → деплой
- ✅ Параллельные сборки разных проектов
- ✅ Реальный-time лог сборки
- ✅ Возможность отменить сборку

## 🎨 9. AI генерация контента

```csharp
public class ContentGenerationRequest
{
    public string Prompt { get; set; }
    public ContentType Type { get; set; } // Article, Image, Code
    public string Style { get; set; }
    public int Length { get; set; }
}

// Использование
[HttpPost("generate-content")]
public IActionResult GenerateContent([FromBody] ContentGenerationRequest request)
{
    var taskId = _aiProcessor.SubmitTask(request);
    return Accepted(new { 
        taskId, 
        status = "AI model loading",
        estimatedTime = "30-60 seconds" 
    });
}
```

**Преимущества:**
- ✅ AI модели работают долго, особенно для больших запросов
- ✅ Поэтапная генерация: "Понимание промпта → Генерация → Оптимизация"
- ✅ Возможность получать частичные результаты
- ✅ Очередь запросов к ограниченным AI ресурсам

## 📈 10. Финансовые расчеты и симуляции

```csharp
public class FinancialSimulationRequest
{
    public InvestmentPortfolio Portfolio { get; set; }
    public int Years { get; set; }
    public int SimulationCount { get; set; } // Монте-Карло симуляции
    public MarketCondition Conditions { get; set; }
}

// Использование
[HttpPost("run-simulation")]
public IActionResult RunSimulation([FromBody] FinancialSimulationRequest request)
{
    var taskId = _financeProcessor.SubmitTask(request);
    return Accepted(new { 
        taskId, 
        status = "Running Monte Carlo simulations",
        progress = $"0/{request.SimulationCount} iterations" 
    });
}
```

**Преимущества:**
- ✅ Тысячи итераций сложных расчетов
- ✅ Промежуточные результаты и прогресс
- ✅ Возможность отменить долгий расчет
- ✅ Параллельные симуляции для разных сценариев

## 💡 Ключевые преимущества подхода во всех случаях:

1. **🚀 Отзывчивость API** - мгновенный ответ вместо минут ожидания
2. **📊 Контроль прогресса** - пользователь видит, что происходит
3. **🔄 Асинхронность** - сервер не блокируется на долгие операции
4. **⚖️ Балансировка нагрузки** - контроль параллельных задач
5. **🛡️ Отказоустойчивость** - задачи не теряются при ошибках
6. **⏸️ Управление** - пауза, возобновление, отмена операций
7. **📈 Масштабируемость** - легко добавить больше воркеров

Такой подход идеален для любого сценария, где операция занимает больше 2-3 секунд и пользователю важно понимать прогресс выполнения!
