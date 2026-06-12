using System.Net;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using S3LifecycleRule = Amazon.S3.Model.LifecycleRule;

namespace BrandUp.Extensions.ObjectStorage.Internals;

internal class S3Client : IS3Client, IDisposable
{
    static readonly Regex TrainCaseRegex = new("(?<!^)([A-Z][a-z]|(?<=[a-z])[A-Z0-9])", RegexOptions.Compiled | RegexOptions.Singleline);

    readonly AmazonS3Client _s3;

    public S3Client(IOptions<ObjectStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value;

        _s3 = new AmazonS3Client(opts.AccessKeyId, opts.SecretAccessKey, new AmazonS3Config
        {
            ServiceURL = opts.ServiceUrl,
            AuthenticationRegion = opts.AuthenticationRegion,
            ForcePathStyle = opts.ForcePathStyle,
            SignatureMethod = Amazon.Runtime.SigningAlgorithm.HmacSHA256
        });
    }

    #region Object operations

    public async Task<S3StorageObject> UploadAsync(string bucketName, string objectKey, IDictionary<string, string> metadata, Stream stream, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(objectKey);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(stream);

        MemoryStream? buffer = null;
        Stream uploadStream;
        long contentLength;

        if (stream.CanSeek)
        {
            uploadStream = stream;
            contentLength = stream.Length - stream.Position;
        }
        else
        {
            buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Seek(0, SeekOrigin.Begin);
            uploadStream = buffer;
            contentLength = buffer.Length;
        }

        if (contentLength == 0)
            throw new InvalidOperationException("Stream contains no data.");

        try
        {
            var request = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = objectKey,
                InputStream = uploadStream,
                DisableDefaultChecksumValidation = true
            };

            foreach (var kv in metadata)
                request.Metadata.Add(EncodeMetadataKey(kv.Key), EncodeMetadataValue(kv.Value));

            var response = await _s3.PutObjectAsync(request, cancellationToken);

            return new S3StorageObject(objectKey, contentLength, response.ETag, metadata);
        }
        catch (AmazonS3Exception ex)
        {
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
        }
        finally
        {
            buffer?.Dispose();
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

            var metadata = new Dictionary<string, string>();
            foreach (var key in metadataKeys)
            {
                var encodedValue = response.Metadata[EncodeMetadataKey(key)];
                metadata[key] = encodedValue != null ? DecodeMetadataValue(encodedValue) : null!;
            }

            return new S3StorageObject(objectKey, response.ContentLength, response.ETag, metadata);
        }
        catch (AmazonS3Exception ex)
        {
            return ex.StatusCode switch
            {
                HttpStatusCode.NotFound => null,
                _ => throw new ObjectStorageException(ex.Message, ex.StatusCode, ex)
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
                _ => throw new ObjectStorageException(ex.Message, ex.StatusCode, ex)
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
                _ => throw new ObjectStorageException(ex.Message, ex.StatusCode, ex)
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
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
            throw new ObjectStorageException(ex.Message, ex.StatusCode, ex);
        }
    }

    #endregion

    #region Helpers

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
        return TrainCaseRegex.Replace(key, "-$1").Replace('_', '-').Replace("--", "-").Trim().ToLower();
    }

    internal static string EncodeMetadataValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
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

    #endregion

    public void Dispose() => _s3.Dispose();
}
