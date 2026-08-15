using System.Buffers;
using System.Net;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using S3LifecycleRule = Amazon.S3.Model.LifecycleRule;

namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class S3Client : IS3Client, IDisposable
{
    static readonly Regex TrainCaseRegex = new("(?<!^)([A-Z][a-z]|(?<=[a-z])[A-Z0-9])", RegexOptions.Compiled | RegexOptions.Singleline);

    readonly AmazonS3Client _s3;
    readonly int _multipartPartSize;
    readonly long _multipartThreshold;

    // Created per connection by S3ClientFactory; credentialsProvider is the one registered for that connection
    // (null for static or session-token credentials).
    public S3Client(ObjectStorageOptions opts, IObjectStorageCredentialsProvider? credentialsProvider = null)
    {
        ArgumentNullException.ThrowIfNull(opts);

        _multipartPartSize = opts.MultipartPartSize;
        _multipartThreshold = opts.MultipartThreshold;

        // Built once: a RefreshingAWSCredentials renews temp creds in place, so the singleton client is never recreated.
        _s3 = new AmazonS3Client(CreateCredentials(opts, credentialsProvider), new AmazonS3Config
        {
            ServiceURL = opts.ServiceUrl,
            AuthenticationRegion = opts.AuthenticationRegion,
            ForcePathStyle = opts.ForcePathStyle,
            SignatureMethod = SigningAlgorithm.HmacSHA256
        });
    }

    // Priority: registered provider (auto-refresh) -> session token (fixed temp creds) -> static ak/sk.
    internal static AWSCredentials CreateCredentials(ObjectStorageOptions opts, IObjectStorageCredentialsProvider? provider)
    {
        if (provider is not null)
            return new ProviderRefreshingCredentials(provider);

        if (!string.IsNullOrEmpty(opts.SessionToken))
            return new SessionAWSCredentials(opts.AccessKeyId, opts.SecretAccessKey, opts.SessionToken);

        return new BasicAWSCredentials(opts.AccessKeyId, opts.SecretAccessKey);
    }

    // Bridges IObjectStorageCredentialsProvider (cached, possibly temporary) to the SDK's refresh mechanism.
    // GenerateNewCredentials() is synchronous, so the provider must serve cached creds without blocking;
    // the consumer renews the cache out-of-band (e.g. on a timer) before ExpiresUtc.
    sealed class ProviderRefreshingCredentials(IObjectStorageCredentialsProvider provider) : RefreshingAWSCredentials
    {
        protected override CredentialsRefreshState GenerateNewCredentials()
        {
            var c = provider.GetCurrent();
            return new CredentialsRefreshState(
                new ImmutableCredentials(c.AccessKeyId, c.SecretAccessKey, c.SessionToken),
                (c.ExpiresUtc ?? DateTimeOffset.UtcNow.AddHours(1)).UtcDateTime);
        }
    }

    #region Object operations

    // S3 rejects PartNumber > 10000, so the part size scales up for very large seekable payloads.
    internal const int MaxParts = 10_000;

    /// <summary>S3 caps a single object at 5 TB.</summary>
    internal const long MaxObjectSize = 5L * 1024 * 1024 * 1024 * 1024;

    /// <summary>S3 caps a single PUT (and a single multipart part) at 5 GB.</summary>
    internal const long MaxSinglePutSize = 5L * 1024 * 1024 * 1024;

    // A non-seekable stream has an unknown length, so the part size doubles every GrowPartEvery parts
    // (see Parts) up to the cap — long streams fit the 10,000-part limit with bounded memory.
    internal const int GrowPartEvery = 1_000;
    internal const int MaxGrownPartSize = 512 * 1024 * 1024;

    internal static int ComputePartSize(long contentLength, int minPartSize)
        => (int)Math.Max(minPartSize, (contentLength + MaxParts - 1) / MaxParts);

    public async Task<S3StorageObject> UploadAsync(string bucketName, string objectKey, IDictionary<string, string> metadata, Stream stream, UploadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(objectKey);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek)
        {
            UploadStreamGuard.ThrowIfConsumed(stream);

            var contentLength = stream.Length - stream.Position;
            if (contentLength > MaxObjectSize)
                throw new ArgumentException(
                    $"Content length {contentLength} exceeds the S3 object size limit of {MaxObjectSize} bytes (5 TB).", nameof(stream));

            // Empty objects are legal in S3 (markers, placeholders) and go through the simple path.
            if (contentLength <= _multipartThreshold)
                return await PutObjectAsync(bucketName, objectKey, metadata, stream, contentLength, options, cancellationToken);

            // A scaled-up part (payload > MaxParts * configured part size) is a rare, huge buffer: renting
            // it would round up to the next power-of-two pool bucket (~2x waste) and park it in the shared
            // pool for the process lifetime, so such buffers are allocated directly instead.
            var partSize = ComputePartSize(contentLength, _multipartPartSize);
            var pooled = partSize == _multipartPartSize;
            var buffer = pooled ? ArrayPool<byte>.Shared.Rent(partSize) : GC.AllocateUninitializedArray<byte>(partSize);
            try
            {
                return await MultipartUploadAsync(bucketName, objectKey, metadata, options,
                    Parts(buffer, partSize, firstRead: 0, stream, growable: false, cancellationToken), cancellationToken);
            }
            finally
            {
                if (pooled)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Non-seekable: read the first part; a stream shorter than one part takes the simple path, so memory
        // is bounded by the part size instead of the whole payload.
        var firstBuffer = ArrayPool<byte>.Shared.Rent(_multipartPartSize);
        try
        {
            var read = await stream.ReadAtLeastAsync(
                firstBuffer.AsMemory(0, _multipartPartSize), _multipartPartSize, throwOnEndOfStream: false, cancellationToken);

            if (read < _multipartPartSize)
            {
                using var single = new MemoryStream(firstBuffer, 0, read, writable: false);
                return await PutObjectAsync(bucketName, objectKey, metadata, single, read, options, cancellationToken);
            }

            return await MultipartUploadAsync(bucketName, objectKey, metadata, options,
                Parts(firstBuffer, _multipartPartSize, read, stream, growable: true, cancellationToken), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(firstBuffer);
        }
    }

    async Task<S3StorageObject> PutObjectAsync(string bucketName, string objectKey, IDictionary<string, string> metadata, Stream stream, long contentLength, UploadOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = objectKey,
                InputStream = stream,
                AutoCloseStream = false,
                DisableDefaultChecksumValidation = true
            };

            ApplyOptions(request.Headers, options);
            ApplyMetadata(request.Metadata, metadata);

            var response = await _s3.PutObjectAsync(request, cancellationToken);

            return new S3StorageObject(objectKey, contentLength, response.ETag, metadata);
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    /// <summary>
    /// Splits any stream into parts over one shared (pooled) buffer. Parts are consumed strictly
    /// sequentially (each UploadPart completes before the next part is read), so a single buffer serves
    /// the whole upload — no per-part allocations. Do not parallelize part uploads without revisiting this.
    /// Buffering also keeps the SDK away from the caller's stream: UploadPart offers no AutoCloseStream
    /// and no exact-read guarantee, so feeding it the source directly is not safe.
    /// When <paramref name="growable"/> (non-seekable source of unknown length), the part size doubles
    /// every <see cref="GrowPartEvery"/> parts up to <see cref="MaxGrownPartSize"/>, so long streams fit
    /// the 10,000-part S3 limit with bounded memory. The initial buffer is owned (rented and returned) by
    /// the caller; grown buffers are rented and returned here.
    /// </summary>
    static async IAsyncEnumerable<MemoryStream> Parts(
        byte[] buffer, int partSize, int firstRead, Stream stream, bool growable,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var initialBuffer = buffer;
        try
        {
            if (firstRead > 0)
                yield return new MemoryStream(buffer, 0, firstRead, writable: false);

            var partsProduced = firstRead > 0 ? 1 : 0;
            while (true)
            {
                if (growable && partsProduced > 0 && partsProduced % GrowPartEvery == 0 && partSize < MaxGrownPartSize)
                {
                    partSize = (int)Math.Min(2L * partSize, MaxGrownPartSize);
                    if (!ReferenceEquals(buffer, initialBuffer))
                        ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(partSize);
                }

                var read = await stream.ReadAtLeastAsync(
                    buffer.AsMemory(0, partSize), partSize, throwOnEndOfStream: false, cancellationToken);
                if (read == 0)
                    yield break;

                yield return new MemoryStream(buffer, 0, read, writable: false);
                partsProduced++;
            }
        }
        finally
        {
            if (!ReferenceEquals(buffer, initialBuffer))
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    async Task<S3StorageObject> MultipartUploadAsync(
        string bucketName, string objectKey, IDictionary<string, string> metadata, UploadOptions? options,
        IAsyncEnumerable<MemoryStream> parts, CancellationToken cancellationToken)
    {
        string? uploadId = null;
        try
        {
            var initiate = new InitiateMultipartUploadRequest { BucketName = bucketName, Key = objectKey };
            ApplyOptions(initiate.Headers, options);
            ApplyMetadata(initiate.Metadata, metadata);

            uploadId = (await _s3.InitiateMultipartUploadAsync(initiate, cancellationToken)).UploadId;

            var etags = new List<PartETag>();
            long uploaded = 0;
            var partNumber = 1;

            await foreach (var content in parts)
            {
                if (partNumber > MaxParts)
                    throw new InvalidOperationException(
                        $"Object exceeds the S3 limit of {MaxParts} multipart parts. " +
                        "Supply a seekable stream so the part size can be computed from the payload length up front.");

                using (content)
                {
                    var part = await _s3.UploadPartAsync(new UploadPartRequest
                    {
                        BucketName = bucketName,
                        Key = objectKey,
                        UploadId = uploadId,
                        PartNumber = partNumber,
                        PartSize = content.Length,
                        InputStream = content,
                        DisableDefaultChecksumValidation = true
                    }, cancellationToken);

                    etags.Add(new PartETag(partNumber, part.ETag));
                    uploaded += content.Length;
                    partNumber++;
                }
            }

            var completed = await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucketName,
                Key = objectKey,
                UploadId = uploadId,
                PartETags = etags
            }, cancellationToken);

            return new S3StorageObject(objectKey, uploaded, completed.ETag, metadata);
        }
        catch (AmazonS3Exception ex)
        {
            await AbortSafeAsync();
            throw Wrap(ex);
        }
        catch
        {
            await AbortSafeAsync();
            throw;
        }

        // Best-effort cleanup: an abandoned multipart upload keeps storing (and billing) its parts.
        async Task AbortSafeAsync()
        {
            if (uploadId is null)
                return;

            try
            {
                await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = bucketName,
                    Key = objectKey,
                    UploadId = uploadId
                }, CancellationToken.None);
            }
            catch
            {
                // the original failure matters more
            }
        }
    }

    static void ApplyMetadata(MetadataCollection target, IDictionary<string, string> metadata)
    {
        foreach (var kv in metadata)
            target.Add(EncodeMetadataKey(kv.Key), EncodeMetadataValue(kv.Value));
    }

    static void ApplyOptions(HeadersCollection headers, UploadOptions? options)
    {
        if (options is null)
            return;

        if (!string.IsNullOrEmpty(options.ContentType))
            headers.ContentType = options.ContentType;
        if (!string.IsNullOrEmpty(options.CacheControl))
            headers.CacheControl = options.CacheControl;
        if (!string.IsNullOrEmpty(options.ContentDisposition))
            headers.ContentDisposition = options.ContentDisposition;
    }

    public async IAsyncEnumerable<ObjectListItem> ListObjectsAsync(
        string bucketName, string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);

        string? continuationToken = null;
        do
        {
            ListObjectsV2Response response;
            try
            {
                response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucketName,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                }, cancellationToken);
            }
            catch (AmazonS3Exception ex)
            {
                throw Wrap(ex);
            }

            foreach (var entry in response.S3Objects ?? [])
                yield return new ObjectListItem(entry.Key, entry.Size ?? 0, entry.ETag, entry.LastModified);

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;

            // A truncated page without a token would silently yield a partial listing — fail loudly instead.
            if (response.IsTruncated == true && continuationToken is null)
                throw new InvalidOperationException(
                    $"Bucket '{bucketName}' returned a truncated listing without a continuation token.");
        }
        while (continuationToken is not null);
    }

    public async Task<Uri> GetPresignedUrlAsync(
        string bucketName, string objectKey, TimeSpan expiresIn, bool forWrite, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(objectKey);
        PresignedUrlLimits.Validate(expiresIn, nameof(expiresIn));

        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucketName,
            Key = objectKey,
            Verb = forWrite ? HttpVerb.PUT : HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiresIn)
        };

        if (forWrite && !string.IsNullOrEmpty(contentType))
            request.ContentType = contentType;

        // The SDK presigns for HTTPS by default; follow the scheme of the configured endpoint (MinIO is
        // typically plain http in development).
        if (_s3.Config.ServiceURL?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true)
            request.Protocol = Protocol.HTTP;

        try
        {
            return new Uri(await _s3.GetPreSignedURLAsync(request));
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public async Task<S3StorageObject?> FindAsync(string bucketName, string objectKey, IEnumerable<string> metadataKeys, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(objectKey);
        ArgumentNullException.ThrowIfNull(metadataKeys);

        try
        {
            var response = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucketName,
                Key = objectKey
            }, cancellationToken);

            // Keys absent on the object are omitted, not null-filled: deserialization then leaves the
            // property at its default, so objects written before a metadata property existed stay readable.
            var metadata = new Dictionary<string, string>();
            foreach (var key in metadataKeys)
            {
                var encodedValue = response.Metadata[EncodeMetadataKey(key)];
                if (encodedValue != null)
                    metadata[key] = DecodeMetadataValue(encodedValue);
            }

            return new S3StorageObject(objectKey, response.ContentLength, response.ETag, metadata);
        }
        catch (AmazonS3Exception ex)
        {
            return ex.StatusCode switch
            {
                HttpStatusCode.NotFound => null,
                _ => throw Wrap(ex)
            };
        }
    }

    public async Task<Stream?> ReadAsync(string bucketName, string objectKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(objectKey);

        try
        {
            var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucketName,
                Key = objectKey
            }, cancellationToken);

            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex)
        {
            return ex.StatusCode switch
            {
                HttpStatusCode.NotFound => null,
                _ => throw Wrap(ex)
            };
        }
    }

    public async Task<bool> DeleteAsync(string bucketName, string objectKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(objectKey);

        try
        {
            await _s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucketName,
                Key = objectKey
            }, cancellationToken);

            return true;
        }
        catch (AmazonS3Exception ex)
        {
            return ex.StatusCode switch
            {
                HttpStatusCode.NotFound => false,
                _ => throw Wrap(ex)
            };
        }
    }

    #endregion

    #region Bucket operations

    public async Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucketName }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket" || ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public async Task CreateBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.PutBucketAsync(new PutBucketRequest
            {
                BucketName = bucketName,
                UseClientRegion = true
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            // Error code is carried over so callers can tell "already exists" from other conflicts.
            throw Wrap(ex);
        }
    }

    public async Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucketName }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public async Task<IReadOnlyList<BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.ListBucketsAsync(cancellationToken);
            return response.Buckets
                .Select(b => new BucketInfo(b.BucketName, b.CreationDate.GetValueOrDefault()))
                .ToList();
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    #endregion

    #region Bucket settings

    public async Task<BucketVersioning> GetVersioningAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = bucketName }, cancellationToken);

            return response.VersioningConfig?.Status?.Value switch
            {
                "Enabled" => BucketVersioning.Enabled,
                "Suspended" => BucketVersioning.Suspended,
                _ => BucketVersioning.Disabled
            };
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public async Task SetVersioningAsync(string bucketName, BucketVersioning versioning, CancellationToken cancellationToken)
    {
        if (versioning == BucketVersioning.Disabled)
            return;

        try
        {
            await _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = bucketName,
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = versioning == BucketVersioning.Enabled
                        ? VersionStatus.Enabled
                        : VersionStatus.Suspended
                }
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public async Task<BucketAccess> GetAccessAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucketName }, cancellationToken);
            var isPublicRead = response.Grants?.Any(g =>
                g.Grantee?.URI == "http://acs.amazonaws.com/groups/global/AllUsers" &&
                g.Permission == S3Permission.READ) ?? false;

            return isPublicRead ? BucketAccess.PublicRead : BucketAccess.Private;
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public async Task SetAccessAsync(string bucketName, BucketAccess access, CancellationToken cancellationToken)
    {
        try
        {
            var current = await _s3.GetBucketAclAsync(
                new GetBucketAclRequest { BucketName = bucketName }, cancellationToken);

            var request = new PutBucketAclRequest
            {
                BucketName = bucketName,
                GrantFullControl = $"id=\"{current.Owner.Id}\""
            };

            if (access == BucketAccess.PublicRead)
                request.GrantRead = "uri=\"http://acs.amazonaws.com/groups/global/AllUsers\"";

            await _s3.PutBucketAclAsync(request, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public async Task<IReadOnlyList<LifecycleRule>> GetLifecycleAsync(string bucketName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetLifecycleConfigurationAsync(
                new GetLifecycleConfigurationRequest { BucketName = bucketName }, cancellationToken);

            var rules = response.Configuration?.Rules;
            if (rules is null || rules.Count == 0)
                return [];

            return rules.Select(ToLifecycleRule).ToList();
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchLifecycleConfiguration")
        {
            return [];
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public async Task SetLifecycleAsync(string bucketName, IReadOnlyList<LifecycleRule> rules, CancellationToken cancellationToken)
    {
        try
        {
            if (rules.Count == 0)
            {
                await _s3.DeleteLifecycleConfigurationAsync(
                    new DeleteLifecycleConfigurationRequest { BucketName = bucketName }, cancellationToken);
                return;
            }

            await _s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
            {
                BucketName = bucketName,
                Configuration = new LifecycleConfiguration
                {
                    Rules = rules.Select(ToS3LifecycleRule).ToList()
                }
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchLifecycleConfiguration")
        {
            // already empty — ignore
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    #endregion

    #region Helpers

    // ErrorCode is always carried over so callers can branch on it ("NoSuchBucket", "InvalidBucketName", ...)
    // against production exactly like against the Testing fake.
    static ObjectStorageException Wrap(AmazonS3Exception ex)
        => new(ex.Message, ex.StatusCode, ex.ErrorCode, ex);

    static LifecycleRule ToLifecycleRule(S3LifecycleRule r)
    {
        string? prefix = null;
        if (r.Filter?.LifecycleFilterPredicate is LifecyclePrefixPredicate p && !string.IsNullOrEmpty(p.Prefix))
            prefix = p.Prefix;

        return new LifecycleRule(
            r.Id,
            r.Expiration?.Days,
            prefix,
            r.Status == LifecycleRuleStatus.Enabled);
    }

    static S3LifecycleRule ToS3LifecycleRule(LifecycleRule r) => new()
    {
        Id = r.Id,
        Status = r.Enabled ? LifecycleRuleStatus.Enabled : LifecycleRuleStatus.Disabled,
        Filter = new LifecycleFilter
        {
            LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = r.Prefix ?? string.Empty }
        },
        Expiration = r.ExpirationDays.HasValue
            ? new LifecycleRuleExpiration { Days = r.ExpirationDays.Value }
            : null
    };

    internal static string EncodeMetadataKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return TrainCaseRegex.Replace(key, "-$1").Replace('_', '-').Replace("--", "-").Trim().ToLowerInvariant();
    }

    internal static string EncodeMetadataValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // S3 caps user metadata at 2 KB per object and hex doubles every byte, so ASCII-safe values travel
        // as-is. The plaintext must never be something the decoder would parse as hex (e.g. "25", "DEAD") —
        // such values stay hex-encoded, which keeps the two formats unambiguous without any marker. Older
        // library versions read the plaintext too: their decoder has the same fall-back-to-original branch.
        if (IsHeaderSafe(value) && !LooksLikeHex(value))
            return value;

        return Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value));
    }

    internal static string DecodeMetadataValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromHexString(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    /// <summary>Printable ASCII with no outer or consecutive spaces — survives an HTTP header byte-for-byte.</summary>
    static bool IsHeaderSafe(string value)
    {
        if (value.Length == 0)
            return false;   // keep the historic empty-value round-trip through the hex path

        if (value[0] == ' ' || value[^1] == ' ')
            return false;   // HTTP trims outer whitespace of header values

        var previousWasSpace = false;
        foreach (var c in value)
        {
            if (c < 0x20 || c > 0x7E)
                return false;

            // Header-normalizing intermediaries may collapse runs of spaces — encode such values instead.
            if (c == ' ' && previousWasSpace)
                return false;

            previousWasSpace = c == ' ';
        }

        return true;
    }

    /// <summary>Exactly what <see cref="Convert.FromHexString(string)"/> accepts: even length, hex digits only.</summary>
    static bool LooksLikeHex(string value)
    {
        if (value.Length % 2 != 0)
            return false;

        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    #endregion

    public void Dispose() => _s3.Dispose();
}
