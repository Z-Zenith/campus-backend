using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace BackendApi.Services;

// TWA-06: real file upload for course materials, backed by Cloudflare R2 (S3-compatible).
// R2 buckets are private by default (https://developers.cloudflare.com/r2/buckets/public-buckets/)
// — course materials are RBAC-gated, so a permanently-public bucket URL would bypass
// CommunityController's own view-authorization check (CanViewMaterialAsync). Objects are
// fetched via a short-lived presigned URL generated per authorized download instead.
public interface IMaterialStorageClient
{
    Task UploadAsync(Stream content, string key, string contentType, CancellationToken ct = default);
    Task<string> GetPresignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct = default);
}

// Configuration keys mirror the existing Copyleaks/AiServices pattern in appsettings.json —
// empty placeholders committed, real values supplied via environment/secrets in deployment,
// never hardcoded. See Cloudflare's own aws-sdk-net sample:
// https://developers.cloudflare.com/r2/examples/aws/aws-sdk-net/
public class R2MaterialStorageClient : IMaterialStorageClient
{
    private readonly IConfiguration configuration;
    private readonly Lazy<(AmazonS3Client Client, string BucketName)> lazyClient;

    public R2MaterialStorageClient(IConfiguration configuration)
    {
        this.configuration = configuration;
        // Built lazily, not in the constructor, so a deployment with R2 unconfigured (e.g.
        // local dev never touching Materials) doesn't fail DI/app startup — only the actual
        // upload/download call path fails closed, same convention as CopyleaksClient.
        lazyClient = new Lazy<(AmazonS3Client, string)>(BuildClient);
    }

    private (AmazonS3Client Client, string BucketName) BuildClient()
    {
        var accountId = configuration["Cloudflare:R2:AccountId"];
        var accessKey = configuration["Cloudflare:R2:AccessKeyId"];
        var secretKey = configuration["Cloudflare:R2:SecretAccessKey"];
        var bucketName = configuration["Cloudflare:R2:BucketName"];
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(accessKey)
            || string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ExternalServiceNotConfiguredException("Cloudflare R2");
        }

        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        var client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = $"https://{accountId}.r2.cloudflarestorage.com",
        });
        return (client, bucketName);
    }

    public async Task UploadAsync(Stream content, string key, string contentType, CancellationToken ct = default)
    {
        var (client, bucketName) = lazyClient.Value;
        // R2 doesn't support AWSSDK.S3's streaming SigV4 payload signing or the SDK's default
        // checksum validation — both must be explicitly disabled per request, not globally on
        // the client config. See https://developers.cloudflare.com/r2/examples/aws/aws-sdk-net/.
        var request = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true,
        };
        await client.PutObjectAsync(request, ct);
    }

    public Task<string> GetPresignedDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct = default)
    {
        var (client, bucketName) = lazyClient.Value;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucketName,
            Key = key,
            Expires = DateTime.UtcNow.Add(expiry),
            Verb = HttpVerb.GET,
        };
        return Task.FromResult(client.GetPreSignedURL(request));
    }
}
