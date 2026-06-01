using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

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
            SignatureMethod = Amazon.Runtime.SigningAlgorithm.HmacSHA256
        });
    }

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
                System.Net.HttpStatusCode.NotFound => null,
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
                System.Net.HttpStatusCode.NotFound => null,
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
                System.Net.HttpStatusCode.NotFound => false,
                _ => throw new ObjectStorageException(ex.Message, ex.StatusCode, ex)
            };
        }
    }

    public void Dispose() => _s3.Dispose();

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
}
