# BrandUp.Extensions.ObjectStorage

Библиотека для работы с S3-совместимыми объектными хранилищами (Yandex Cloud Object Storage, Amazon S3, MinIO и др.) через AWS SDK.

## Пакеты

| Пакет | Описание |
|---|---|
| `BrandUp.Extensions.ObjectStorage.Abstraction` | Интерфейсы и модели. Зависимостей от AWS SDK нет. |
| `BrandUp.Extensions.ObjectStorage` | Реализация через AWSSDK.S3. |
| `BrandUp.Extensions.ObjectStorage.Testing` | Фейковая in-memory реализация для тестов. |

---

## Быстрый старт

### 1. Описать метаданные объекта

```csharp
public class UserPhotoMetadata : IObjectMetadata
{
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public DateTime UploadedAt { get; set; }
}
```

Любой класс с публичным конструктором без параметров и публичными свойствами с геттером и сеттером.  
Поддерживаемые типы свойств: `string`, `int`, `bool`, `Guid`, `DateTime`, `decimal`, `enum` и любые типы с `TypeConverter`. Значения `null` при сериализации пропускаются.

### 2. Зарегистрировать в DI

```csharp
services.AddObjectStorage(opts =>
{
    opts.ServiceUrl           = "https://storage.yandexcloud.net";
    opts.AuthenticationRegion = "ru-central1";
    opts.AccessKeyId          = "...";
    opts.SecretAccessKey      = "...";
})
.AddMapping<UserPhotoMetadata>("my-bucket/photos");
```

Формат `destination` в `AddMapping`: `bucketName` или `bucketName/prefix`. Допустимые символы: буквы, цифры и `/`.

### 3. Использовать

```csharp
// Через IObjectStorage (совместимый фасад)
public class PhotoService(IObjectStorage storage)
{
    public Task UploadAsync(Guid id, Stream photo)
        => storage.UploadAsync(id, new UserPhotoMetadata { FileName = "photo.jpg" }, photo);

    public Task<Stream?> DownloadAsync(Guid id)
        => storage.ReadAsync<UserPhotoMetadata>(id);

    public async Task<UserPhotoMetadata?> GetMetadataAsync(Guid id)
        => (await storage.FindAsync<UserPhotoMetadata>(id))?.Metadata;

    public Task<bool> DeleteAsync(Guid id)
        => storage.DeleteAsync<UserPhotoMetadata>(id);
}

// Через IObjectBucket<T> — типизированный бакет, инжектируется напрямую
public class PhotoService(IObjectBucket<UserPhotoMetadata> bucket)
{
    public Task UploadAsync(Guid id, Stream photo)
        => bucket.UploadAsync(id, new UserPhotoMetadata { FileName = "photo.jpg" }, photo);
}
```

---

## API

### `IObjectStorageClient`

Точка входа, аналог `IMongoClient`. Навигация к бакетам синхронная (без I/O).

```csharp
IObjectBucket bucket              = client.GetBucket("my-bucket");
IObjectBucket<T> typed            = client.GetBucket<UserPhotoMetadata>();

IReadOnlyList<BucketInfo> buckets = await client.ListBucketsAsync();
await client.CreateBucketAsync("new-bucket", s => s.Versioning = BucketVersioning.Enabled);
await client.DropBucketAsync("old-bucket");
```

### `IObjectBucket`

Управление бакетом, аналог `IMongoDatabase`.

```csharp
bool exists = await bucket.ExistsAsync();
BucketSettings settings = await bucket.GetSettingsAsync();

await bucket.UpdateSettingsAsync(s =>
{
    s.Versioning = BucketVersioning.Enabled;
    s.Access     = BucketAccess.PublicRead;
    s.LifecycleRules.Add(new LifecycleRule("cleanup", ExpirationDays: 30, Prefix: "temp/"));
});
```

### `IObjectBucket<TMetadata>`

Типизированные CRUD-операции, аналог `IMongoCollection<T>`. Наследует `IObjectBucket`.

| Метод | Описание |
|---|---|
| `FindOneAsync(Guid, CancellationToken)` | Метаданные объекта. `null` если не найден. |
| `OpenReadAsync(Guid, CancellationToken)` | Поток содержимого. `null` если не найден. |
| `UploadAsync(Guid, TMetadata, Stream, CancellationToken)` | Загрузить объект. |
| `DeleteOneAsync(Guid, CancellationToken)` | Удалить объект. `false` если не существовал. |

### `IObjectStorage`

Упрощённый фасад над `IObjectStorageClient` для обратной совместимости.

| Метод | Описание |
|---|---|
| `FindAsync<T>(Guid, CancellationToken)` | Метаданные объекта. `null` если не найден. |
| `ReadAsync<T>(Guid, CancellationToken)` | Поток содержимого. `null` если не найден. |
| `UploadAsync<T>(Guid, T, Stream, CancellationToken)` | Загрузить объект. |
| `DeleteAsync<T>(Guid, CancellationToken)` | Удалить объект. `false` если не существовал. |

### `ObjectStorageOptions`

| Свойство | Описание |
|---|---|
| `ServiceUrl` | URL эндпоинта S3 |
| `AuthenticationRegion` | Регион авторизации |
| `AccessKeyId` | Идентификатор ключа доступа |
| `SecretAccessKey` | Секретный ключ доступа |

### `BucketSettings`

| Свойство | Тип | Описание |
|---|---|---|
| `Versioning` | `BucketVersioning` | `Disabled` / `Enabled` / `Suspended` |
| `Access` | `BucketAccess` | `Private` / `PublicRead` |
| `LifecycleRules` | `List<LifecycleRule>` | Правила жизненного цикла |

`LifecycleRule(string Id, int? ExpirationDays, string? Prefix, bool Enabled)`

---

## Работа с JSON

Extension-методы `ReadJsonAsync` / `UploadJsonAsync` работают с любым `IObjectMetadata` — никаких дополнительных интерфейсов реализовывать не нужно.

```csharp
public class ReportContent
{
    public string Title { get; set; }
    public decimal Value { get; set; }
}

public class ReportMetadata : IObjectMetadata   // обычный IObjectMetadata
{
    public string Author { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

```csharp
// Загрузка
await storage.UploadJsonAsync<ReportMetadata, ReportContent>(
    id, new ReportMetadata { Author = "admin" }, reportContent);

// Чтение — возвращает null если объект не найден
ReportContent? content = await storage.ReadJsonAsync<ReportMetadata, ReportContent>(id);

// Опционально: настройки сериализации
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
await storage.UploadJsonAsync<ReportMetadata, ReportContent>(id, metadata, content, options);
var result = await storage.ReadJsonAsync<ReportMetadata, ReportContent>(id, options);

// Через IObjectBucket<T>
await bucket.UploadJsonAsync<ReportMetadata, ReportContent>(id, metadata, content);
var result = await bucket.ReadJsonAsync<ReportMetadata, ReportContent>(id);
```

---

## Исключения

При ошибках S3 бросается `ObjectStorageException`. Свойство `StatusCode` содержит HTTP-код ответа, `InnerException` — оригинальное `AmazonS3Exception`.

```csharp
catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { }
catch (ObjectStorageException ex) { }
```

---

## Тестирование

Пакет `BrandUp.Extensions.ObjectStorage.Testing` предоставляет in-memory реализацию всех интерфейсов без зависимостей от AWS SDK.

### Настройка

```csharp
services.AddFakeObjectStorage()
    .AddMapping<UserPhotoMetadata>("photos/users")
    .WithBucket("photos");       // предсоздать бакет (опционально)
```

### Использование в тестах

```csharp
// Стандартные интерфейсы работают как обычно
var storage = sp.GetRequiredService<IObjectStorage>();
var bucket  = sp.GetRequiredService<IObjectBucket<UserPhotoMetadata>>();
var client  = sp.GetRequiredService<IObjectStorageClient>();

// FakeObjectStore — инспекция и управление состоянием
var store = sp.GetRequiredService<FakeObjectStore>();

Assert.True(store.BucketExists("photos"));
Assert.Equal(1, store.GetObjectCount("photos"));

store.Clear();                              // сброс между тестами
store.CreateBucket("extra");               // ручное создание бакета
store.PutObject("photos", "key", bytes, metadata); // предзаполнение данными
```

---

## Yandex Cloud Object Storage

### Конфигурация

```csharp
services.AddObjectStorage(opts =>
{
    opts.ServiceUrl           = "https://storage.yandexcloud.net";
    opts.AuthenticationRegion = "ru-central1";
    opts.AccessKeyId          = "<идентификатор статического ключа>";
    opts.SecretAccessKey      = "<секретный ключ>";
})
.AddMapping<UserPhotoMetadata>("my-bucket/photos");
```

Ключи доступа создаются в консоли Yandex Cloud: **IAM → Сервисные аккаунты → Ключи доступа**.

### Совместимость

| Функция | YC |
|---|:---:|
| Загрузка / чтение / удаление объектов | ✅ |
| Метаданные объектов | ✅ |
| Создание / удаление бакетов | ✅ |
| Список бакетов | ✅ |
| Версионирование | ✅ |
| Lifecycle rules | ✅ |
| Управление доступом (ACL) | ⚠️ |

ACL-операции работают через S3-совместимый API, однако публичный доступ может быть заблокирован политикой организации. Для управления публичным доступом рекомендуется консоль YC или CLI: `yc storage bucket update --public-read`.

Имена бакетов в Yandex Cloud уникальны глобально — не только в рамках вашего аккаунта.
