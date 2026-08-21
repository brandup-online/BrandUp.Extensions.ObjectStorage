using System.Buffers;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Channels;
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
    readonly int _multipartParallelism;

    // Created per connection by S3ClientFactory; credentialsProvider is the one registered for that connection
    // (null for static or session-token credentials).
    public S3Client(ObjectStorageOptions opts, IObjectStorageCredentialsProvider? credentialsProvider = null)
    {
        ArgumentNullException.ThrowIfNull(opts);

        _multipartPartSize = opts.MultipartPartSize;
        _multipartThreshold = opts.MultipartThreshold;
        _multipartParallelism = Math.Clamp(opts.MultipartParallelism, 1, ObjectStorageOptions.MaxMultipartParallelism);

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
    // (see MultipartUploadAsync) up to the cap — long streams fit the 10,000-part limit with memory
    // bounded by MultipartParallelism x the current part size.
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

            // The length is known up front, so the part size is fixed. The upload takes over the buffer.
            var partSize = ComputePartSize(contentLength, _multipartPartSize);
            return await MultipartUploadAsync(bucketName, objectKey, metadata, options,
                new MultipartSource(stream, partSize, FirstRead: 0, RentPartBuffer(partSize), Growable: false), cancellationToken);
        }

        // Non-seekable: read the first part; a stream shorter than one part takes the simple path, so memory
        // is bounded by the part size instead of the whole payload.
        var firstBuffer = ArrayPool<byte>.Shared.Rent(_multipartPartSize);
        var handedOver = false;
        try
        {
            var read = await stream.ReadAtLeastAsync(
                firstBuffer.AsMemory(0, _multipartPartSize), _multipartPartSize, throwOnEndOfStream: false, cancellationToken);

            if (read < _multipartPartSize)
            {
                using var single = new MemoryStream(firstBuffer, 0, read, writable: false);
                return await PutObjectAsync(bucketName, objectKey, metadata, single, read, options, cancellationToken);
            }

            // From here the upload owns the buffer and returns it to the pool itself.
            handedOver = true;
            return await MultipartUploadAsync(bucketName, objectKey, metadata, options,
                new MultipartSource(stream, _multipartPartSize, read, new PartBuffer(firstBuffer, Pooled: true), Growable: true), cancellationToken);
        }
        finally
        {
            if (!handedOver)
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
    /// A part buffer and how it was obtained, so whoever holds it can release it correctly.
    /// <c>default</c> is a free slot with no array behind it yet.
    /// </summary>
    readonly record struct PartBuffer(byte[]? Array, bool Pooled);

    /// <summary>
    /// Only the configured part size is worth pooling. A larger part is rare and huge, and renting it would
    /// park hundreds of megabytes in the shared pool for the process lifetime, so it is allocated directly.
    /// </summary>
    PartBuffer RentPartBuffer(int size)
        => size == _multipartPartSize
            ? new PartBuffer(ArrayPool<byte>.Shared.Rent(size), Pooled: true)
            : new PartBuffer(GC.AllocateUninitializedArray<byte>(size), Pooled: false);

    static void ReturnPartBuffer(PartBuffer buffer)
    {
        if (buffer is { Array: not null, Pooled: true })
            ArrayPool<byte>.Shared.Return(buffer.Array);
    }

    /// <summary>
    /// What a multipart upload reads from: the stream, the part size, and the first buffer — already holding
    /// <see cref="FirstRead"/> bytes if the caller had to peek at the stream to pick this path.
    /// </summary>
    readonly record struct MultipartSource(
        Stream Stream, int PartSize, int FirstRead, PartBuffer FirstBuffer, bool Growable);

    /// <summary>
    /// Uploads a stream as multipart, with up to <c>MultipartParallelism</c> parts in flight. Reading is
    /// sequential — a stream has one position — so only the transfers overlap: the reader takes a buffer from
    /// a pool of that many, and each upload returns its own as soon as its part is on the wire. Memory stays
    /// at parallelism x the part size, and a slow network slows reading instead of growing the pool.
    /// <para>
    /// Parts are buffered rather than fed to the SDK directly: UploadPart offers no AutoCloseStream and no
    /// exact-read guarantee, so handing it the caller's stream is not safe.
    /// </para>
    /// <para>
    /// When the length is unknown (non-seekable stream) the part size doubles every
    /// <see cref="GrowPartEvery"/> parts up to <see cref="MaxGrownPartSize"/>, so long streams fit the
    /// 10,000-part S3 limit; buffers below the new size are replaced as they come free. S3 allows parts to
    /// differ in size — only the last may be under 5 MB.
    /// </para>
    /// </summary>
    async Task<S3StorageObject> MultipartUploadAsync(
        string bucketName, string objectKey, IDictionary<string, string> metadata, UploadOptions? options,
        MultipartSource source, CancellationToken cancellationToken)
    {
        // The pool is the bound: the reader waits here for a free buffer, so no more than `parallelism`
        // parts are ever in flight. Only the reader takes from it.
        var free = Channel.CreateUnbounded<PartBuffer>(new UnboundedChannelOptions { SingleReader = true });
        for (var i = 1; i < _multipartParallelism; i++)
            free.Writer.TryWrite(default);

        var uploads = new List<Task<PartETag>>();
        var etags = new List<PartETag>();

        var current = source.FirstBuffer;
        var holding = true;
        var partSize = source.PartSize;
        string? uploadId = null;
        long uploaded = 0;

        try
        {
            var initiate = new InitiateMultipartUploadRequest { BucketName = bucketName, Key = objectKey };
            ApplyOptions(initiate.Headers, options);
            ApplyMetadata(initiate.Metadata, metadata);

            uploadId = (await _s3.InitiateMultipartUploadAsync(initiate, cancellationToken)).UploadId;

            var partNumber = 1;

            while (true)
            {
                if (!holding)
                {
                    current = await free.Reader.ReadAsync(cancellationToken);
                    holding = true;
                }

                // Collect what finished meanwhile, and rethrow a failed part here rather than reading on
                // into an upload that is about to be aborted.
                for (var i = uploads.Count - 1; i >= 0; i--)
                {
                    if (!uploads[i].IsCompleted)
                        continue;

                    etags.Add(await uploads[i]);
                    uploads.RemoveAt(i);
                }

                if (source.Growable && partNumber > 1 && (partNumber - 1) % GrowPartEvery == 0 && partSize < MaxGrownPartSize)
                    partSize = (int)Math.Min(2L * partSize, MaxGrownPartSize);

                if (current.Array is null || current.Array.Length < partSize)
                {
                    // Drop the reference before releasing: if the rent below throws, the finally must not
                    // return an array that is already in the pool. ArrayPool does not detect a double return
                    // — it hands the same array to two renters.
                    var stale = current;
                    current = default;
                    ReturnPartBuffer(stale);

                    current = RentPartBuffer(partSize);
                }

                // The first part is already in the buffer: the caller read it to choose the multipart path.
                var read = partNumber == 1 && source.FirstRead > 0
                    ? source.FirstRead
                    : await source.Stream.ReadAtLeastAsync(
                        current.Array.AsMemory(0, partSize), partSize, throwOnEndOfStream: false, cancellationToken);

                if (read == 0)
                    break;

                if (partNumber > MaxParts)
                    throw new InvalidOperationException(
                        $"Object exceeds the S3 limit of {MaxParts} multipart parts. " +
                        "Supply a seekable stream so the part size can be computed from the payload length up front.");

                // The task owns the buffer from here and releases it itself, so the reader must let go of
                // it before anything else can throw.
                var upload = UploadPartAsync(current, partNumber, read);
                current = default;
                holding = false;
                uploads.Add(upload);

                uploaded += read;
                partNumber++;
            }

            foreach (var upload in uploads)
                etags.Add(await upload);

            uploads.Clear();

            var completed = await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucketName,
                Key = objectKey,
                UploadId = uploadId,
                // Parts complete out of order, so the manifest is sorted rather than assumed to be in order.
                PartETags = [.. etags.OrderBy(e => e.PartNumber)]
            }, cancellationToken);

            return new S3StorageObject(objectKey, uploaded, completed.ETag, metadata);
        }
        catch (AmazonS3Exception ex)
        {
            await AbandonAsync();
            throw Wrap(ex);
        }
        catch
        {
            await AbandonAsync();
            throw;
        }
        finally
        {
            // Buffers go back to the shared pool here, so nothing may still be reading from them.
            await SettleInFlightAsync();

            if (holding)
                ReturnPartBuffer(current);

            free.Writer.TryComplete();
            while (free.Reader.TryRead(out var buffer))
                ReturnPartBuffer(buffer);
        }

        async Task<PartETag> UploadPartAsync(PartBuffer buffer, int partNumber, int length)
        {
            try
            {
                using var content = new MemoryStream(buffer.Array!, 0, length, writable: false);

                var part = await _s3.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = bucketName,
                    Key = objectKey,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    PartSize = length,
                    InputStream = content,
                    DisableDefaultChecksumValidation = true
                }, cancellationToken);

                return new PartETag(partNumber, part.ETag);
            }
            finally
            {
                // Give the buffer back whatever the outcome: the request is over, and the reader may be
                // waiting for it.
                free.Writer.TryWrite(buffer);
            }
        }

        // Waits for every part still on the wire. A request that outlived the abort would keep its part
        // stored (and billed), and its buffer must not return to the pool while it is still being read.
        async Task SettleInFlightAsync()
        {
            foreach (var upload in uploads)
            {
                try
                {
                    await upload;
                }
                catch
                {
                    // the original failure matters more
                }
            }
        }

        async Task AbandonAsync()
        {
            await SettleInFlightAsync();
            await AbortSafeAsync();
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

    public async Task<bool> CopyAsync(
        string sourceBucketName, string sourceObjectKey,
        string targetBucketName, string targetObjectKey,
        IDictionary<string, string> metadata, UploadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceBucketName);
        ArgumentException.ThrowIfNullOrEmpty(sourceObjectKey);
        ArgumentException.ThrowIfNullOrEmpty(targetBucketName);
        ArgumentException.ThrowIfNullOrEmpty(targetObjectKey);
        ArgumentNullException.ThrowIfNull(metadata);

        // One HEAD answers three questions: does the source exist, does it fit a single CopyObject, and
        // which headers to carry over when the caller gave no options.
        GetObjectMetadataResponse source;
        try
        {
            source = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = sourceBucketName,
                Key = sourceObjectKey
            }, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            return ex.StatusCode switch
            {
                HttpStatusCode.NotFound => false,
                _ => throw Wrap(ex)
            };
        }

        // No size check against MaxObjectSize: an object above it could not have been stored in the first place.
        var contentLength = source.ContentLength;
        if (contentLength > MaxSinglePutSize)
            return await MultipartCopyAsync(
                sourceBucketName, sourceObjectKey, targetBucketName, targetObjectKey,
                metadata, options, source.Headers, contentLength, source.ETag, cancellationToken);

        // Not pinned to the source ETag: one CopyObject reads one version atomically, so an overwrite
        // mid-copy cannot tear the result, and failing the copy over a routine overwrite would be worse.
        try
        {
            var request = new CopyObjectRequest
            {
                SourceBucket = sourceBucketName,
                SourceKey = sourceObjectKey,
                DestinationBucket = targetBucketName,
                DestinationKey = targetObjectKey,
                // The copy is served by the target mapping, so the source metadata is replaced, not inherited.
                MetadataDirective = S3MetadataDirective.REPLACE
            };

            ApplyCopyHeaders(request.Headers, options, source.Headers);
            ApplyMetadata(request.Metadata, metadata);

            await _s3.CopyObjectAsync(request, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
        {
            // The source was removed between the HEAD and the copy — same answer as if it had never been there.
            return false;
        }
        catch (AmazonS3Exception ex)
        {
            throw Wrap(ex);
        }
    }

    /// <summary>
    /// Copies an object above the 5 GB single-copy limit range by range, server-side, up to
    /// <c>MultipartParallelism</c> ranges at once. <see cref="ComputePartSize"/> keeps the part count within
    /// <see cref="MaxParts"/>.
    /// <para>
    /// The ranges are separate requests, so each is pinned to <paramref name="sourceETag"/>: overwriting the
    /// source mid-copy fails the operation (412) instead of stitching the copy out of two versions.
    /// </para>
    /// </summary>
    async Task<bool> MultipartCopyAsync(
        string sourceBucketName, string sourceObjectKey,
        string targetBucketName, string targetObjectKey,
        IDictionary<string, string> metadata, UploadOptions? options, HeadersCollection sourceHeaders,
        long contentLength, string sourceETag, CancellationToken cancellationToken)
    {
        string? uploadId = null;
        try
        {
            // Metadata and headers of a multipart object are fixed at initiation, not at completion.
            var initiate = new InitiateMultipartUploadRequest { BucketName = targetBucketName, Key = targetObjectKey };
            ApplyCopyHeaders(initiate.Headers, options, sourceHeaders);
            ApplyMetadata(initiate.Metadata, metadata);

            uploadId = (await _s3.InitiateMultipartUploadAsync(initiate, cancellationToken)).UploadId;

            var ranges = CopyPartRanges(contentLength, ComputePartSize(contentLength, _multipartPartSize)).ToArray();
            var etags = new PartETag[ranges.Length];

            // No payload passes through the process, so the ranges are simply fanned out: a fixed set of
            // workers takes the next one until the list runs out or one of them fails.
            var next = -1;
            var failed = false;

            async Task CopyWorkerAsync()
            {
                while (!Volatile.Read(ref failed))
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= ranges.Length)
                        return;

                    var range = ranges[index];
                    try
                    {
                        var part = await _s3.CopyPartAsync(new CopyPartRequest
                        {
                            SourceBucket = sourceBucketName,
                            SourceKey = sourceObjectKey,
                            DestinationBucket = targetBucketName,
                            DestinationKey = targetObjectKey,
                            UploadId = uploadId,
                            PartNumber = range.PartNumber,
                            FirstByte = range.FirstByte,
                            LastByte = range.LastByte,
                            ETagToMatch = [sourceETag]
                        }, cancellationToken);

                        etags[index] = new PartETag(range.PartNumber, part.ETag);
                    }
                    catch
                    {
                        // Stop the other workers instead of copying gigabytes that are about to be aborted.
                        Volatile.Write(ref failed, true);
                        throw;
                    }
                }
            }

            // WhenAll settles every worker before the catch aborts the upload, so no copy outlives the abort.
            await Task.WhenAll(Enumerable
                .Range(0, Math.Min(_multipartParallelism, ranges.Length))
                .Select(_ => CopyWorkerAsync()));

            // A worker that sees the failure flag stops without claiming its range, so a hole would mean
            // completing an upload with parts missing. WhenAll faults before that can happen, but this
            // branch needs an object above 5 GB to run, so the invariant is checked rather than assumed.
            if (Array.IndexOf(etags, null) >= 0)
                throw new InvalidOperationException(
                    $"Multipart copy of '{sourceObjectKey}' produced an incomplete part manifest.");

            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = targetBucketName,
                Key = targetObjectKey,
                UploadId = uploadId,
                PartETags = [.. etags]
            }, cancellationToken);

            return true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
        {
            // The source was removed between the HEAD and the copy — same answer as the single-request path.
            await AbortSafeAsync();
            return false;
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
                    BucketName = targetBucketName,
                    Key = targetObjectKey,
                    UploadId = uploadId
                }, CancellationToken.None);
            }
            catch
            {
                // the original failure matters more
            }
        }
    }

    /// <summary>
    /// Inclusive byte ranges of a multipart copy — contiguous, non-overlapping, covering the object exactly.
    /// Separate from <see cref="MultipartCopyAsync"/> so it can be tested: that path needs an object above
    /// 5 GB to run.
    /// </summary>
    internal static IEnumerable<CopyPartRange> CopyPartRanges(long contentLength, int partSize)
    {
        var partNumber = 1;
        for (long position = 0; position < contentLength; position += partSize)
            yield return new CopyPartRange(partNumber++, position, Math.Min(position + partSize, contentLength) - 1);
    }

    internal readonly record struct CopyPartRange(int PartNumber, long FirstByte, long LastByte);

    static void ApplyMetadata(MetadataCollection target, IDictionary<string, string> metadata)
    {
        foreach (var kv in metadata)
            target.Add(EncodeMetadataKey(kv.Key), EncodeMetadataValue(kv.Value));
    }

    /// <summary>
    /// Headers of a copy. <c>MetadataDirective.REPLACE</c> clears every system header, so without options
    /// all of them are carried over from the source — a gzip asset that lost its <c>Content-Encoding</c>
    /// would reach the browser unreadable. Options, when given, replace the headers rather than merge.
    /// </summary>
    static void ApplyCopyHeaders(HeadersCollection target, UploadOptions? options, HeadersCollection source)
    {
        if (options is not null)
        {
            ApplyOptions(target, options);
            return;
        }

        target.ContentType = source.ContentType;
        target.CacheControl = source.CacheControl;
        target.ContentDisposition = source.ContentDisposition;
        target.ContentEncoding = source.ContentEncoding;
        target.ContentLanguage = source.ContentLanguage;
        target.Expires = source.Expires;
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
