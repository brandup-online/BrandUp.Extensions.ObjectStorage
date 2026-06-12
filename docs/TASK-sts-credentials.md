# Задача: поддержка STS / временных кред (session token + авто-refresh)

## Контекст

`Internals/S3Client.cs` строит `AmazonS3Client(opts.AccessKeyId, opts.SecretAccessKey, new AmazonS3Config{…})`
**один раз в конструкторе** со статическими ключами, а сам `IS3Client→S3Client` зарегистрирован как
**singleton** (`Extensions/ServiceCollectionExtensions.cs`, `AddSingleton<IS3Client, S3Client>()`).

Потребитель (нода кластера BrandUp.Browser) должен грузить объекты в **Yandex Object Storage** по
**временным кредам Yandex STS** (`GetFederationToken`-стиль): `AccessKeyId` + `SecretAccessKey` +
**`SessionToken`**, срок жизни **≤ 12 часов**. Два блокера:

1. В `ObjectStorageOptions` нет `SessionToken` → подпись SigV4 уходит без заголовка
   `X-Amz-Security-Token`, и временные креды не принимаются сервером.
2. Креды зафиксированы в конструкторе singleton-клиента → по истечении ≤12ч клиент перестаёт
   авторизоваться, механизма обновления нет.

## Цель

Пакет должен уметь работать с временными кредами (session token) и **прозрачно обновлять** их до
истечения **без пересоздания** `AmazonS3Client`. Статический режим (ak/sk) продолжает работать
без изменений и без конфигурации.

## Точки в коде

| Файл | Что есть сейчас |
|------|-----------------|
| `Extensions/ServiceCollectionExtensions.cs` | `AddObjectStorage(configure)` → `AddSingleton<IS3Client, S3Client>()`; возвращает `ObjectStorageBuilder` |
| `Internals/S3Client.cs` (ctor) | `new AmazonS3Client(opts.AccessKeyId, opts.SecretAccessKey, new AmazonS3Config{ ServiceURL, AuthenticationRegion, ForcePathStyle, SignatureMethod=HmacSHA256 })` — единственная точка внедрения credentials |
| `ObjectStorageOptions.cs` | поля `ServiceUrl`/`AuthenticationRegion`/`AccessKeyId`/`SecretAccessKey`/`ForcePathStyle`; валидатор требует ak/sk обязательными |
| `ObjectStorageBuilder.cs` | `AddMapping<TMetadata>(destination)`; сюда же вешается hook провайдера кред |

> Lifetime критичен: `S3Client` — singleton ⇒ обновление кред обязано идти **внутри самих
> credentials** (через `RefreshingAWSCredentials`), а не через пересоздание клиента/скоупы.

## Изменения

### 1. `SessionToken` в опциях (простой режим — фиксированные temp-creds)

- Добавить `ObjectStorageOptions.SessionToken (string?)`.
- В `S3Client`: если `SessionToken` задан → `new SessionAWSCredentials(ak, sk, token)`; иначе как
  сейчас (`BasicAWSCredentials` через перегрузку с ak/sk).
- Покрывает короткие/одноразовые сценарии без авто-refresh.

### 2. Провайдер кред с авто-refresh (основной режим — из-за singleton + TTL ≤ 12ч)

В проекте **`BrandUp.Extensions.ObjectStorage.Abstraction`** (без зависимости на AWS SDK):

```csharp
public sealed record ObjectStorageCredentials(
    string AccessKeyId,
    string SecretAccessKey,
    string? SessionToken,
    DateTimeOffset? ExpiresUtc);

public interface IObjectStorageCredentialsProvider
{
    /// <summary>Текущие действующие креды (кеш). Читается СИНХРОННО из SDK при refresh.</summary>
    ObjectStorageCredentials GetCurrent();

    /// <summary>(Опц.) Проактивно обновить кеш кред, если они истекли/скоро истекут.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
```

В **`ObjectStorageBuilder`** (impl-проект):

```csharp
public ObjectStorageBuilder UseCredentialsProvider<TProvider>()
    where TProvider : class, IObjectStorageCredentialsProvider;       // services.AddSingleton<IObjectStorageCredentialsProvider, TProvider>()

public ObjectStorageBuilder UseCredentialsProvider(
    Func<IServiceProvider, IObjectStorageCredentialsProvider> factory);
```

В **`S3Client`** — если провайдер зарегистрирован, построить `AmazonS3Client` с подклассом
`Amazon.Runtime.RefreshingAWSCredentials`:

```csharp
sealed class ProviderRefreshingCredentials(IObjectStorageCredentialsProvider provider) : RefreshingAWSCredentials
{
    protected override CredentialsRefreshState GenerateNewCredentials()
    {
        var c = provider.GetCurrent();
        return new CredentialsRefreshState(
            new ImmutableCredentials(c.AccessKeyId, c.SecretAccessKey, c.SessionToken),
            (c.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(1)).UtcDateTime);
    }
    // PreemptExpiryTime по умолчанию ~15 мин — для 12ч токенов достаточно.
}
```

`AmazonS3Client` создаётся **один раз**; refresh идёт внутри credentials → singleton-lifetime не мешает.

**Нюанс sync/async (важно для реализации):** `RefreshingAWSCredentials.GenerateNewCredentials()`
синхронный, а получение свежих кред у потребителя async (нода тянет их с Control Plane по каналу).
Рекомендуемый паттерн: провайдер держит **закешированные** текущие креды, обновляемые **вне** SDK
(потребитель проактивно тянет новые по таймеру до `ExpiresUtc`), `GetCurrent()` отдаёт кеш
синхронно; `RefreshAsync` — точка проактивного обновления. Так sync-refresh SDK развязан с
async-получением кред.

### 3. Валидатор и приоритет

- Если зарегистрирован `IObjectStorageCredentialsProvider` → `AccessKeyId`/`SecretAccessKey` в
  опциях **не обязательны**. `ObjectStorageOptionsValidator`: требовать `(AccessKeyId && SecretAccessKey)`
  **или** наличие провайдера; `ServiceUrl`/`AuthenticationRegion` обязательны всегда.
- Приоритет выбора credentials в `S3Client`: **провайдер** → иначе **`SessionToken`** → иначе
  **static ak/sk** (текущее поведение).

### 4. Тесты

- static keys — без регрессий;
- `SessionToken` задан → исходящий запрос несёт `X-Amz-Security-Token` (через локальный
  S3-совместимый сервер/мок);
- провайдер: смена значения `GetCurrent()` после «истечения» → SDK берёт новые креды на следующем
  запросе, `AmazonS3Client` **не** пересоздаётся;
- `BrandUp.Extensions.ObjectStorage.Testing` (fake) — креды игнорирует, как и сейчас.

## Вне объёма

- **Минтинг STS** (вызов `https://sts.yandexcloud.net`, `GetFederationToken` / scoped-policy) — на
  стороне потребителя (Control Plane), не в этом пакете. Пакет только **потребляет** временные креды
  и обновляет их. (При желании позже — отдельный optional-helper с минтингом, отдельной задачей.)

## Критерии приёмки

- ObjectStorage можно сконфигурировать с временными кредами (session token) и грузить/читать объекты
  в Yandex Object Storage.
- При TTL ≤ 12ч аплоады продолжают работать после обновления кред — без рестарта и без пересоздания
  singleton-клиента.
- Статический режим (ak/sk) не сломан; валидатор корректен для обоих режимов.

## Как использует потребитель (для контекста)

`ClusterAgent` (нода BrandUp.Browser) реализует `IObjectStorageCredentialsProvider`: `GetCurrent()`
отдаёт последние креды, присланные Control Plane (в bootstrap-ответе на регистрацию), а по таймеру
до `ExpiresUtc` запрашивает новые у Control Plane по каналу и обновляет кеш. Минтинг (Yandex STS) —
на Control Plane.
