# BrandUp.Extensions.ObjectStorage

Библиотека для работы с S3-совместимыми объектными хранилищами (Yandex Cloud Object Storage, Amazon S3, MinIO и др.) через AWS SDK.

## Установка

```
dotnet add package BrandUp.Extensions.ObjectStorage
```

## Быстрый старт

### 1. Описать метаданные объекта

Любой класс с публичным конструктором без параметров и публичными свойствами:

```csharp
public class UserPhotoMetadata : IObjectMetadata
{
    public string FileName { get; set; }
    public string ContentType { get; set; }
    public DateTime UploadedAt { get; set; }
}
```

### 2. Зарегистрировать в DI

```csharp
services.AddObjectStorage(opts =>
{
    opts.ServiceUrl          = "https://storage.yandexcloud.net";
    opts.AuthenticationRegion = "ru-central1";
    opts.AccessKeyId         = "...";
    opts.SecretAccessKey     = "...";
})
.AddMapping<UserPhotoMetadata>("my-bucket/photos");
```

Формат `destination` в `AddMapping`: `bucketName` или `bucketName/prefix`.  
Допустимые символы: буквы, цифры и `/`.

### 3. Использовать `IObjectStorage`

```csharp
public class PhotoService(IObjectStorage storage)
{
    public async Task UploadAsync(Guid id, Stream photo)
    {
        var metadata = new UserPhotoMetadata
        {
            FileName    = "photo.jpg",
            ContentType = "image/jpeg",
            UploadedAt  = DateTime.UtcNow
        };

        await storage.UploadAsync(id, metadata, photo);
    }

    public Task<Stream?> DownloadAsync(Guid id)
        => storage.ReadAsync<UserPhotoMetadata>(id);

    public async Task<UserPhotoMetadata?> GetMetadataAsync(Guid id)
    {
        var item = await storage.FindAsync<UserPhotoMetadata>(id);
        return item?.Metadata;
    }

    public Task<bool> DeleteAsync(Guid id)
        => storage.DeleteAsync<UserPhotoMetadata>(id);
}
```

## API

### `IObjectStorage`

| Метод | Описание |
|---|---|
| `FindAsync<T>(Guid, CancellationToken)` | Получить объект с метаданными. Возвращает `null`, если не найден. |
| `ReadAsync<T>(Guid, CancellationToken)` | Получить поток содержимого. Возвращает `null`, если не найден. |
| `UploadAsync<T>(Guid, T, Stream, CancellationToken)` | Загрузить объект с метаданными. |
| `DeleteAsync<T>(Guid, CancellationToken)` | Удалить объект. Возвращает `false`, если объект не существовал. |

### `ObjectStorageOptions`

| Свойство | Описание |
|---|---|
| `ServiceUrl` | URL эндпоинта S3 (например `https://storage.yandexcloud.net`) |
| `AuthenticationRegion` | Регион авторизации (например `ru-central1`) |
| `AccessKeyId` | Идентификатор ключа доступа |
| `SecretAccessKey` | Секретный ключ доступа |

## Исключения

При ошибках S3 бросается `ObjectStorageException`. Свойство `StatusCode` содержит HTTP-код ответа хранилища, `InnerException` — оригинальное `AmazonS3Exception`.

```csharp
try
{
    await storage.UploadAsync(id, metadata, stream);
}
catch (ObjectStorageException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
{
    // доступ запрещён
}
catch (ObjectStorageException ex)
{
    // прочие ошибки хранилища — ex.InnerException содержит AmazonS3Exception
}
```

## Метаданные

Свойства класса метаданных сериализуются в пользовательские метаданные S3-объекта.  
Поддерживаемые типы свойств: `string`, `int`, `bool`, `Guid`, `DateTime`, `decimal`, `enum` и любые типы, для которых доступен `TypeConverter`.

Значения `null` при сериализации пропускаются.

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

### Совместимость функций

| Функция | YC |
|---|:---:|
| Загрузка / чтение / удаление объектов | ✅ |
| Метаданные объектов | ✅ |
| Создание / удаление бакетов | ✅ |
| Список бакетов | ✅ |
| Версионирование | ✅ |
| Lifecycle rules (срок хранения) | ✅ |
| Управление доступом (ACL) | ⚠️ |

### Ограничения ACL в Yandex Cloud

Yandex Cloud использует **IAM-политики** как основной механизм управления доступом. ACL-операции (`GetAccessAsync` / `SetAccessAsync` через `IObjectBucket.UpdateSettingsAsync`) технически выполняются через S3-совместимый API, однако публичный доступ к бакету может быть заблокирован политикой организации независимо от настроек ACL.

Для управления публичным доступом рекомендуется использовать консоль Yandex Cloud или CLI (`yc storage bucket update --public-read`).

### Имена бакетов

В Yandex Cloud имена бакетов уникальны глобально. При использовании `CreateBucketAsync` убедитесь, что имя уникально в рамках всего Yandex Cloud, а не только вашего аккаунта.
