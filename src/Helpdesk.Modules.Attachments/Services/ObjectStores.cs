using System.Collections.Concurrent;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Helpdesk.Modules.Attachments.Contracts;
using Microsoft.Extensions.Options;

namespace Helpdesk.Modules.Attachments.Services;

/// <summary>Process-local store for tests and throw-away development. Contents vanish on restart.</summary>
internal sealed class InMemoryObjectStore : IObjectStore
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public int Count => _objects.Count;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        _objects[key] = content;
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(_objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
}

/// <summary>
/// Any S3-compatible endpoint (AWS S3, Cloudflare R2, SeaweedFS, ...). Nothing here is vendor specific: only PutObject/GetObject and
/// bucket creation are used, with path-style addressing and request checksums only where required.
/// </summary>
internal sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private bool _bucketReady;

    public S3ObjectStore(IOptions<StorageOptions> options)
    {
        var s3 = options.Value.S3;
        _bucket = s3.Bucket;
        var config = new AmazonS3Config
        {
            ForcePathStyle = s3.ForcePathStyle,
            AuthenticationRegion = s3.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(s3.Endpoint))
        {
            config.ServiceURL = s3.Endpoint;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(s3.Region);
        }

        _client = new AmazonS3Client(new BasicAWSCredentials(s3.AccessKey, s3.SecretKey), config);
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
            var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (_bucketReady)
        {
            return;
        }

        await _bucketLock.WaitAsync(cancellationToken);
        try
        {
            if (_bucketReady)
            {
                return;
            }

            try
            {
                await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
                // Already there.
            }

            _bucketReady = true;
        }
        finally
        {
            _bucketLock.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _bucketLock.Dispose();
    }
}

/// <summary>Scanner used until an engine is chosen (D-08): reports Skipped, which never satisfies a required clean scan.</summary>
internal sealed class NoScanner : IAttachmentScanner
{
    public Task<ScanVerdict> ScanAsync(byte[] content, string fileName, CancellationToken cancellationToken = default) =>
        Task.FromResult(ScanVerdict.Skipped);
}
