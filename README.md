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

Формат `destination` в `AddMapping`: `bucketName` или `bucketName/prefix`. Допустимые символы: буквы, цифры, `-`, `.` и `/`; имя бакета должно начинаться и заканчиваться буквой или цифрой.

### 3. Использовать

```csharp
// Через IObjectStorageContext (совместимый фасад)
public class PhotoService(IObjectStorageContext storage)
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

## Контексты хранилища и несколько аккаунтов

Регистрация выше описывает одно подключение — один облачный аккаунт. Когда бакетов много или аккаунтов несколько, удобнее объявить **контекст хранилища**: класс-наследник `ObjectStorageContext`, в котором бакеты описаны свойствами. Тип контекста задаёт и состав бакетов, и подключение.

### 1. Описать контекст

```csharp
public class MediaStorage : ObjectStorageContext
{
    [Bucket]                              // ключ конфигурации = имя свойства (Photos)
    public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;

    [Bucket("videos")]                    // ключ конфигурации videos
    public IObjectBucket<VideoMetadata> Videos { get; private set; } = null!;
}

public class ArchiveStorage : ObjectStorageContext
{
    [Bucket("archive")]
    public IObjectBucket<ArchiveMetadata> Items { get; private set; } = null!;
}
```

Свойству нужен любой сеттер (`private set` или `init`) — значения проставляются при создании контейнером. Состав бакетов проверяется на этапе регистрации, а не при первом обращении.

**Ни имя бакета, ни префикс ключей в коде не задаются.** Атрибут объявляет только ключ конфигурации (по умолчанию — имя свойства); всё остальное приходит из настроек подключения — см. [Имена бакетов из конфигурации](#имена-бакетов-из-конфигурации).

### 2. Зарегистрировать

```csharp
// каждый контекст — свой аккаунт
services.AddObjectStorage<MediaStorage>(opts =>
{
    opts.ServiceUrl           = "https://storage.yandexcloud.net";
    opts.AuthenticationRegion = "ru-central1";
    opts.AccessKeyId          = "...";
    opts.SecretAccessKey      = "...";
});

services.AddObjectStorage<ArchiveStorage>(opts =>
{
    opts.ServiceUrl           = "https://s3.amazonaws.com";
    opts.AuthenticationRegion = "eu-central-1";
})
.UseCredentialsProvider<ArchiveStsProvider>();   // креды принадлежат подключению
```

Если несколько контекстов должны жить в **одном** аккаунте, подключение объявляется отдельно и переиспользуется — тогда они делят один S3-клиент, его пул соединений и цикл обновления STS:

```csharp
services.AddObjectStorageConnection("main", opts => { /* ... */ });

services.AddObjectStorage<MediaStorage>("main");
services.AddObjectStorage<ReportStorage>("main");
```

Порядок вызовов не важен: контекст можно зарегистрировать до объявления подключения.

Повторный `AddObjectStorageConnection` с тем же именем не заменяет подключение, а **дополняет** его — как обычные named options: скалярные свойства перезаписываются последним вызовом, записи `Objects` и `Buckets` сливаются. Это удобно для «базовый конфиг + доводка под окружение», но означает, что случайный дубль имени из двух модулей молча смешает конфигурации.

```csharp
services.AddObjectStorageConnection("main", opts => configuration.GetSection("ObjectStorage:Main").Bind(opts));
services.AddObjectStorageConnection("main", opts => opts.BucketNameSuffix = "-dev");   // дополняет
```

### 3. Использовать контекст

```csharp
public class MediaService(MediaStorage media, ArchiveStorage archive)
{
    public Task UploadAsync(Guid id, Stream photo)
        => media.Photos.UploadAsync(id, new PhotoMetadata { FileName = "photo.jpg" }, photo);

    public Task ArchiveAsync(Guid id, Stream data)
        => archive.Items.UploadAsync(id, new ArchiveMetadata(), data);

    // контекст сам является IObjectStorageContext
    public Task<Stream?> ReadAsync(Guid id) => media.ReadAsync<PhotoMetadata>(id);

    // создать недостающие бакеты контекста (имена и настройки берутся из конфигурации)
    public Task ProvisionAsync() => media.EnsureBucketsAsync();

    // произвольные операции: Client работает с физическими именами, поэтому имя берём у бакета
    public Task DropAsync() => media.Client.DropBucketAsync(media.Photos.Name);
}
```

`IObjectBucket<TMetadata>` каждого контекста дополнительно регистрируется в DI, поэтому старый способ инъекции бакета работает и здесь. Если один и тот же тип метаданных объявлен в двух контекстах, инъекция `IObjectBucket<T>` становится неоднозначной — резолв бросит исключение с указанием обоих контекстов, обращаться нужно через контекст.

### Настройка из конфигурации

Имя подключения контекста — полное имя его типа, поэтому опции удобно связывать с секциями конфигурации:

```csharp
services.AddObjectStorage<MediaStorage>(opts => configuration.GetSection("ObjectStorage:Media").Bind(opts));
services.AddObjectStorage<ArchiveStorage>(opts => configuration.GetSection("ObjectStorage:Archive").Bind(opts));
```

Каждое подключение валидируется отдельно: отсутствие `ServiceUrl`, региона или кредов сообщается с указанием подключения.

### Имена бакетов из конфигурации

Имя бакета и префикс ключей объектов задаются только в опциях подключения, в секции `Objects` — в коде их нет. Ключ, по которому они ищутся, объявляет атрибут: `[Bucket]` — имя свойства, `[Bucket("videos")]` — указанный ключ.

Задать их можно прямо в `AddObjectStorage`:

```csharp
services.AddObjectStorage<MediaStorage>(opts =>
{
    opts.ServiceUrl           = "https://storage.yandexcloud.net";
    opts.AuthenticationRegion = "ru-central1";
    opts.Objects["Photos"]    = "prod-photos";
    opts.Objects["videos"]    = "prod-videos/hd-2024";   // бакет prod-videos, префикс ключей hd-2024
});
```

…либо взять из конфигурации:

```json
{
  "ObjectStorage": {
    "Media": {
      "ServiceUrl": "https://storage.yandexcloud.net",
      "AuthenticationRegion": "ru-central1",
      "Objects": {
        "Photos": "prod-photos",
        "videos": "prod-videos/hd-2024"
      }
    }
  }
}
```

```csharp
services.AddObjectStorage<MediaStorage>(opts => configuration.GetSection("ObjectStorage:Media").Bind(opts));
```

Правила:

- значение — `bucket` либо `bucket/prefix`: часть до первого `/` это имя бакета, остаток — префикс ключей объектов;
- имя бакета = `BucketNamePrefix` + значение + `BucketNameSuffix` (префикс ключей не затрагивается);
- сравнение ключей регистронезависимое — и в опциях, и при биндинге из конфигурации;
- ненастроенный бакет — ошибка при создании контекста, с указанием контекста, свойства и ожидаемого ключа (`Objects:videos`);
- итоговое имя проверяется по правилам S3, поэтому, например, суффикс `"-"` даст ошибку с указанием контекста и свойства.

`BucketNamePrefix`/`BucketNameSuffix` удобны для окружений — базовые имена лежат в общем конфиге, а различия задаются одним параметром:

```csharp
services.AddObjectStorage<MediaStorage>(opts =>
{
    // ...
    opts.BucketNameSuffix = builder.Environment.IsDevelopment() ? "-dev" : null;   // prod-photos-dev
});
```

Для подключения по умолчанию (`AddMapping`) имя объявлено в коде, поэтому конфигурация не обязательна: `Objects["legacy"]` подменяет имя бакета `legacy`, а префикс/суффикс применяются в любом случае.

### Настройки создаваемых бакетов

Секция `Objects` отвечает за «какой объект в каком бакете лежит», секция `Buckets` — за «с какими параметрами бакет создаётся». Вторая ключуется **именем бакета**, а не ключом конфигурации, поэтому у бакета, который делят несколько свойств, настройки задаются один раз.

```json
"Media": {
  "Objects": {
    "Photos": "media/photos",
    "videos": "media/videos"
  },
  "Buckets": {
    "media": {
      "Versioning": "Enabled",
      "Access": "Private",
      "LifecycleRules": [ { "Id": "tmp", "ExpirationDays": 7, "Prefix": "tmp/" } ]
    }
  }
}
```

То же можно объявить в коде при регистрации — конфигурация накладывается поверх:

```csharp
services.AddObjectStorage<MediaStorage>(opts => configuration.GetSection("ObjectStorage:Media").Bind(opts))
    .ConfigureBucket<PhotoMetadata>(s => s.Versioning = BucketVersioning.Enabled)
    .ConfigureBucket("videos", s => s.LifecycleRules.Add(new("raw", ExpirationDays: 30, Prefix: "hd/")));
```

**Автоматически создаются только те бакеты, для которых настройки объявлены явно** — через `ConfigureBucket` или записью в секции `Buckets`. Пустая запись означает «бакет наш, создать с настройками по умолчанию»:

```jsonc
"Buckets": { "media": {} }
```

```csharp
.ConfigureBucket<PhotoMetadata>(_ => { })
```

Бакеты без объявленных настроек считаются созданными снаружи (инфраструктурой) и `EnsureBucketsAsync` их не трогает. Исключение — делегат, переданный в сам вызов: он применяется ко всем бакетам контекста и включает их все.

```csharp
await media.EnsureBucketsAsync();                                     // только объявленные
await media.EnsureBucketsAsync(s => s.Access = BucketAccess.Private); // все бакеты контекста
```

Порядок применения: настройки из `ConfigureBucket` → секция `Buckets` → делегат вызова. `LifecycleRules` накапливаются, остальные свойства перезаписываются.

**Настройки применяются только при создании.** Существующий бакет не переконфигурируется: правка `Versioning`, `Access` или `LifecycleRules` в конфигурации на уже созданные бакеты не поедет. Так сделано намеренно — бакеты обычно общие для нескольких приложений, поэтому изменение их параметров уместно выполнять явно, в сервисном приложении (например в миграциях):

```csharp
await media.Photos.UpdateSettingsAsync(s =>
{
    s.LifecycleRules.RemoveAll(r => r.Id == "tmp");
    s.LifecycleRules.Add(new("tmp", ExpirationDays: 30, Prefix: "tmp/"));
});
```

Имена резолвятся при создании контекста, поэтому корректность настроенного значения проверяется тогда же — с указанием контекста, свойства и ключа конфигурации. То же работает и для подключения по умолчанию: `AddMapping<T>("legacy/items")` объявляет бакет `legacy`, а `Objects["legacy"]` подменяет его физическое имя.

### Типизированные ключи объектов

По умолчанию объект идентифицируется `Guid`. Тип ключа можно задать вторым параметром `IObjectBucket<TMetadata, TKey>` — прямо типом свойства контекста:

```csharp
public class DocumentStorage : ObjectStorageContext
{
    [Bucket] public IObjectBucket<PhotoMetadata> Photos { get; private set; } = null!;             // Guid, как раньше
    [Bucket] public IObjectBucket<PageMetadata, string> Pages { get; private set; } = null!;       // строковый ключ
    [Bucket] public IObjectBucket<InvoiceMetadata, long> Invoices { get; private set; } = null!;   // числовой
    [Bucket] public IObjectBucket<ReportMetadata, ReportKey> Reports { get; private set; } = null!; // структурный
}

await storage.Pages.UploadAsync("landing/index", metadata, stream);
var invoice = await storage.Invoices.FindOneAsync(20260728001);
```

Поддерживаемые типы ключа: `Guid` (сериализуется как раньше — формат `d`, существующие данные читаются), `string`, `int`, `long` и любой тип, реализующий `IObjectKey`. Неподдерживаемый тип — ошибка на этапе регистрации контекста.

#### Структурные ключи: `ObjectKey`

Для ключей, собираемых из нескольких значений, есть базовый класс `ObjectKey`: строка ключа формируется из публичных свойств, расширение файла подставляется автоматически:

```csharp
[ObjectKeyFormat("{UserId}/{CreatedOn:yyyy/MM}/{Number}", Extension = ".json")]
public sealed class ReportKey : ObjectKey
{
    public Guid UserId { get; init; }
    public DateOnly CreatedOn { get; init; }
    public int Number { get; init; }
}

var key = new ReportKey { UserId = userId, CreatedOn = new(2026, 7, 28), Number = 7 };
// key.ToKeyString() -> "1f0f.../2026/07/7.json"
await storage.Reports.UploadJsonAsync(key, metadata, content);
```

Правила:

- без атрибута свойства соединяются через `/` в порядке объявления;
- шаблон — плейсхолдеры `{Свойство}` или `{Свойство:формат}` (формат инвариантный; `Guid` по умолчанию `d`);
- `Extension` добавляется в конец, точка в начале необязательна; не задан — не добавляется ничего;
- свойство со значением `null` или ключ без единого свойства — ошибка;
- ключи сравниваются по типу и итоговой строке (`Equals`/`GetHashCode` переопределены).

Строковые и структурные ключи валидируются **до обращения к хранилищу**: ключ должен быть непустым, без управляющих символов и без символов из списка AWS «characters to avoid» (`` \ { } ^ % ` [ ] " < > ~ # | ``). Пробелы, юникод и прочие допустимые в S3 символы не ограничиваются. `Guid`, `int` и `long` безопасны по построению и не проверяются.

Для нестандартной сериализации реализуйте `IObjectKey` напрямую — единственный метод `ToKeyString()`.

`Bucket<TMetadata, TKey>()`, generic-перегрузки `FindAsync`/`ReadAsync`/`UploadAsync`/`DeleteAsync` на контексте и JSON-расширения работают с типизированными ключами; типизированный бакет также регистрируется в DI (`IObjectBucket<PageMetadata, string>`).

### Обратная совместимость

`AddObjectStorage(opts => …)` + `AddMapping<T>(...)` продолжает работать как раньше: это подключение по умолчанию с безымянными `IObjectStorageClient`, `IObjectStorageContext` и `IObjectBucket<T>`. Его можно комбинировать с контекстами в одном приложении. Для контекстов безымянные `IObjectStorageClient`/`IObjectStorageContext` не регистрируются — используйте сам контекст и его `Client`.

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

### `IObjectBucket<TMetadata>` / `IObjectBucket<TMetadata, TKey>`

Типизированные CRUD-операции, аналог `IMongoCollection<T>`. Наследует `IObjectBucket`; `IObjectBucket<TMetadata>` — частный случай с ключом `Guid` (наследует `IObjectBucket<TMetadata, Guid>`). Типы ключа — см. [Типизированные ключи объектов](#типизированные-ключи-объектов).

| Метод | Описание |
|---|---|
| `FindOneAsync(TKey, CancellationToken)` | Метаданные объекта. `null` если не найден. |
| `OpenReadAsync(TKey, CancellationToken)` | Поток содержимого. `null` если не найден. |
| `UploadAsync(TKey, TMetadata, Stream, CancellationToken)` | Загрузить объект. |
| `DeleteOneAsync(TKey, CancellationToken)` | Удалить объект. `false` если не существовал. |

### `IObjectStorageContext`

Упрощённый фасад над `IObjectStorageClient` для обратной совместимости.

| Метод | Описание |
|---|---|
| `FindAsync<T>(Guid, CancellationToken)` | Метаданные объекта. `null` если не найден. |
| `ReadAsync<T>(Guid, CancellationToken)` | Поток содержимого. `null` если не найден. |
| `UploadAsync<T>(Guid, T, Stream, CancellationToken)` | Загрузить объект. |
| `DeleteAsync<T>(Guid, CancellationToken)` | Удалить объект. `false` если не существовал. |

### `ObjectStorageContext`

Базовый класс типизированного контекста; сам реализует `IObjectStorageContext`. См. [Контексты хранилища и несколько аккаунтов](#контексты-хранилища-и-несколько-аккаунтов).

| Член | Описание |
|---|---|
| `Client` | `IObjectStorageClient` подключения контекста — управление бакетами того же аккаунта. Принимает **физические** имена, резолв ключей к нему не применяется. |
| `Bucket<TMetadata>()` | Бакет по типу метаданных (ключ `Guid`). |
| `Bucket<TMetadata, TKey>()` | Бакет с типизированным ключом; несоответствие объявленному типу ключа — исключение. |
| `Bucket(Type)` | То же без дженерика. |
| `Buckets` | Все бакеты контекста. |
| `EnsureBucketsAsync(configure?, ct)` | Создаёт недостающие бакеты, для которых объявлены настройки (`ConfigureBucket` или секция `Buckets`); остальные не трогает, существующие не переконфигурирует. `configure` накладывается поверх и включает все бакеты контекста. Гонку с параллельным провижинингом (409 «бакет уже существует») проглатывает. См. [Настройки создаваемых бакетов](#настройки-создаваемых-бакетов). |

`[Bucket(key = null)]` на свойстве `IObjectBucket<TMetadata>` или `IObjectBucket<TMetadata, TKey>` объявляет бакет: `key` — ключ конфигурации, по которому берутся имя бакета и префикс ключей объектов (по умолчанию — имя свойства). См. [Имена бакетов из конфигурации](#имена-бакетов-из-конфигурации).

Generic-перегрузки фасада с типизированным ключом (`FindAsync<TMetadata, TKey>(TKey)` и т.д.) доступны на классе контекста.

### `ObjectStorageOptions`

Опции задаются на подключение: у контекста с собственным подключением — свой набор, у контекстов на общем подключении — один общий.

| Свойство | Описание |
|---|---|
| `ServiceUrl` | URL эндпоинта S3 |
| `AuthenticationRegion` | Регион авторизации |
| `AccessKeyId` | Идентификатор ключа доступа |
| `SecretAccessKey` | Секретный ключ доступа |
| `SessionToken` | Токен сессии для временных (STS) кред. Запросы подписываются заголовком `X-Amz-Security-Token`. См. [Временные креды (STS)](#временные-креды-sts). |
| `Objects` | Где лежат объекты: ключ — ключ из `[Bucket]` / имя свойства контекста (либо имя бакета из `AddMapping`); значение — `bucket` либо `bucket/prefix`. См. [Имена бакетов из конфигурации](#имена-бакетов-из-конфигурации). |
| `Buckets` | Параметры бакетов: ключ — **имя бакета** (как в `Objects`), значение — `Versioning`, `Access`, `LifecycleRules`. Наличие записи включает автосоздание бакета. См. [Настройки создаваемых бакетов](#настройки-создаваемых-бакетов). |
| `BucketNamePrefix` | Префикс к именам бакетов (например `dev-`). |
| `BucketNameSuffix` | Суффикс к именам бакетов (например `-dev`). Комбинируется с `BucketNamePrefix`. |
| `ForcePathStyle` | Path-style адресация (`{serviceUrl}/{bucket}`) вместо virtual-hosted (`{bucket}.{serviceUrl}`). Нужно для MinIO. По умолчанию `false`. |

При статическом доступе обязательны `AccessKeyId` + `SecretAccessKey`. Если зарегистрирован провайдер кред (`UseCredentialsProvider`), они становятся необязательными. `ServiceUrl` и `AuthenticationRegion` обязательны всегда.

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

## Временные креды (STS)

Поддерживаются три режима аутентификации. Приоритет при выборе: **провайдер → `SessionToken` → статические `AccessKeyId`/`SecretAccessKey`**.

### 1. Статические ключи

Режим по умолчанию — см. [Быстрый старт](#2-зарегистрировать-в-di).

### 2. Фиксированный токен сессии

Для коротких/одноразовых сценариев с уже полученными временными кредами без авто-обновления. SDK подписывает запросы заголовком `X-Amz-Security-Token`.

```csharp
services.AddObjectStorage(opts =>
{
    opts.ServiceUrl           = "https://storage.yandexcloud.net";
    opts.AuthenticationRegion = "ru-central1";
    opts.AccessKeyId          = "...";
    opts.SecretAccessKey      = "...";
    opts.SessionToken         = "...";
});
```

### 3. Провайдер с авто-обновлением

Основной режим для временных кред с ограниченным сроком жизни (например, Yandex STS, TTL ≤ 12 ч). `AmazonS3Client` создаётся **один раз**, а креды обновляются «на месте» — singleton-клиент не пересоздаётся.

```csharp
public sealed record ObjectStorageCredentials(
    string AccessKeyId, string SecretAccessKey, string? SessionToken, DateTimeOffset? ExpiresUtc);

public interface IObjectStorageCredentialsProvider
{
    ObjectStorageCredentials GetCurrent();                                  // читается синхронно SDK при подписи
    Task RefreshAsync(CancellationToken cancellationToken = default);       // проактивное обновление кеша
}
```

```csharp
services.AddObjectStorage(opts =>
{
    opts.ServiceUrl           = "https://storage.yandexcloud.net";
    opts.AuthenticationRegion = "ru-central1";
    // AccessKeyId / SecretAccessKey не нужны — креды отдаёт провайдер
})
.UseCredentialsProvider<MyCredentialsProvider>()   // либо UseCredentialsProvider(sp => ...)
.AddMapping<UserPhotoMetadata>("my-bucket/photos");
```

> **Sync/async:** `GetCurrent()` обязан отдавать **закешированные** креды синхронно (вызывается SDK при подписи каждого запроса). Получение свежих кред — асинхронное и идёт **вне** SDK: потребитель проактивно обновляет кеш до `ExpiresUtc` (например, по таймеру), `RefreshAsync` — точка такого обновления. Минтинг STS (вызов STS-эндпоинта) — на стороне потребителя, пакет только потребляет готовые креды.

---

## MinIO

MinIO не поддерживает virtual-hosted адресацию, поэтому обязателен `ForcePathStyle = true`.

MinIO также не поддерживает bucket ACL (`PutBucketAcl` с grant-заголовками): явная установка `Access` завершится ошибкой. Настройки бакетов пишутся только при реальном изменении, поэтому пока `Access` не трогается — запрос ACL не отправляется и всё работает.

```csharp
services.AddObjectStorage(opts =>
{
    opts.ServiceUrl           = "http://localhost:9000";
    opts.AuthenticationRegion = "us-east-1";
    opts.AccessKeyId          = "minioadmin";
    opts.SecretAccessKey      = "minioadmin";
    opts.ForcePathStyle       = true;
})
.AddMapping<UserPhotoMetadata>("photos");
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
var storage = sp.GetRequiredService<IObjectStorageContext>();
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

### Контексты в тестах

Контекст хранилища регистрируется тем же типом, что и в продакшене — подменяется только хранилище:

```csharp
var builder = services.AddFakeObjectStorage<MediaStorage>()
    .WithBucket("photos");

var media = sp.GetRequiredService<MediaStorage>();
await media.Photos.UploadAsync(id, metadata, stream);

Assert.Equal(1, builder.Store.GetObjectCount("photos"));
```

По умолчанию у каждого контекста свой `FakeObjectStore`; чтобы сэмулировать несколько контекстов в одном аккаунте, передайте общий store:

```csharp
var store = new FakeObjectStore();
services.AddFakeObjectStorage<MediaStorage>(store);
services.AddFakeObjectStorage<ReportStorage>(store);
```

Фейковая регистрация повторяет контракт прода: если один тип метаданных объявлен в двух контекстах, инъекция `IObjectBucket<T>` бросает исключение с именами обоих контекстов, а обращаться нужно через контекст. Имена бакетов фиксируются при первом резолве контекста, поэтому `WithBucketName`/`WithBucketNamePrefix`/`WithBucketNameSuffix` после этого бросают исключение вместо молчаливого игнорирования.

Настройки бакетов задаются так же, как при реальной регистрации, и по тем же правилам определяют, какие бакеты создаются:

```csharp
services.AddFakeObjectStorage<MediaStorage>()
    .ConfigureBucket("photos", s => s.Versioning = BucketVersioning.Enabled);

await sp.GetRequiredService<MediaStorage>().EnsureBucketsAsync();   // создаст только photos
```

Бакеты задаются так же, как в конфигурации подключения, по тем же ключам (в тестах, если имя не задано, используется сам ключ):

```csharp
services.AddFakeObjectStorage<MediaStorage>()
    .WithBucketName("Photos", "it-photos")   // точечно; можно и "it-photos/prefix"
    .WithBucketNamePrefix("it-")             // или всем сразу
    .WithBucketNameSuffix("-1");
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
| Временные креды (STS) | ✅ |
| Управление доступом (ACL) | ⚠️ |

ACL-операции работают через S3-совместимый API, однако публичный доступ может быть заблокирован политикой организации. Для управления публичным доступом рекомендуется консоль YC или CLI: `yc storage bucket update --public-read`.

Имена бакетов в Yandex Cloud уникальны глобально — не только в рамках вашего аккаунта.

### Временные креды Yandex STS

Формат временного ключа Yandex STS (`Key ID` + `Secret key` + `Session token`, TTL ≤ 12 ч) совпадает с `ObjectStorageCredentials`, а адресация — virtual-hosted (`ForcePathStyle` оставить `false`). Используйте [`SessionToken`](#2-фиксированный-токен-сессии) или [провайдер с авто-обновлением](#3-провайдер-с-авто-обновлением).

> ⚠️ Политика временного ключа Yandex STS привязана к **одному** бакету — одним ключом нельзя работать с несколькими бакетами. Если узлу нужны несколько бакетов под временными кредами, потребуется отдельный ключ/провайдер на каждый.
