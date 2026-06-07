using Amazon;
using Amazon.EC2;
using Amazon.ECS;
using Amazon.S3;

namespace Tacet.Internal;

/// <summary>
/// Factory methods for AWS SDK clients used by the burst orchestrator.
/// </summary>
internal static class AwsHelpers
{
    /// <summary>
    /// Builds an <see cref="AmazonS3Client"/> configured for <paramref name="region"/>.
    /// Uses <c>ForcePathStyle = true</c> to avoid 301 redirects on regional buckets
    /// and respects <c>AWS_ENDPOINT_URL</c> for local testing with substrate/localstack.
    /// </summary>
    internal static AmazonS3Client BuildS3Client(string region)
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            ForcePathStyle = true,
        };

        var endpointUrl = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");
        if (!string.IsNullOrEmpty(endpointUrl))
            config.ServiceURL = endpointUrl;

        return new AmazonS3Client(config);
    }

    /// <summary>
    /// Builds an <see cref="AmazonECSClient"/> for <paramref name="region"/>.
    /// </summary>
    internal static AmazonECSClient BuildEcsClient(string region) =>
        new(RegionEndpoint.GetBySystemName(region));

    /// <summary>
    /// Builds an <see cref="AmazonEC2Client"/> for <paramref name="region"/>.
    /// </summary>
    internal static AmazonEC2Client BuildEc2Client(string region) =>
        new(RegionEndpoint.GetBySystemName(region));
}
