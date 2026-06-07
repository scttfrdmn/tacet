using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tacet;

/// <summary>
/// Configuration loaded from <c>~/.burst/config.json</c> or the path specified by
/// the <c>BURST_CONFIG_PATH</c> environment variable.
/// </summary>
public sealed class TacetConfig
{
    /// <summary>AWS region (e.g., "us-east-1").</summary>
    [JsonPropertyName("region")]
    public string Region { get; set; } = "us-east-1";

    /// <summary>S3 bucket name for task/result storage.</summary>
    [JsonPropertyName("s3_bucket")]
    public string S3Bucket { get; set; } = string.Empty;

    /// <summary>ECS cluster name.</summary>
    [JsonPropertyName("ecs_cluster")]
    public string EcsCluster { get; set; } = "burst-cluster";

    /// <summary>ECR base URI (e.g., "123456789.dkr.ecr.us-east-1.amazonaws.com").</summary>
    [JsonPropertyName("ecr_base_uri")]
    public string EcrBaseUri { get; set; } = string.Empty;

    /// <summary>ARN of the ECS task execution role.</summary>
    [JsonPropertyName("execution_role_arn")]
    public string ExecutionRoleArn { get; set; } = string.Empty;

    /// <summary>ARN of the ECS task role (for S3 access).</summary>
    [JsonPropertyName("task_role_arn")]
    public string TaskRoleArn { get; set; } = string.Empty;

    /// <summary>Default number of vCPUs per worker (Fargate units: 1, 2, 4, 8, 16).</summary>
    [JsonPropertyName("default_cpu")]
    public int DefaultCpu { get; set; } = 1;

    /// <summary>Default memory per worker in GB.</summary>
    [JsonPropertyName("default_memory_gb")]
    public int DefaultMemoryGb { get; set; } = 2;

    /// <summary>Default number of workers to launch.</summary>
    [JsonPropertyName("default_workers")]
    public int DefaultWorkers { get; set; } = 4;

    /// <summary>Maximum estimated job cost in USD before refusing to launch. 0 = unlimited.</summary>
    [JsonPropertyName("max_cost_per_job")]
    public double MaxCostPerJob { get; set; } = 0.0;

    /// <summary>Cost alert threshold in USD. A warning is printed if the estimate exceeds this.</summary>
    [JsonPropertyName("cost_alert_threshold")]
    public double CostAlertThreshold { get; set; } = 0.0;

    /// <summary>Compute backend: "fargate" (default) or "ec2".</summary>
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "fargate";

    /// <summary>Whether to use Fargate Spot for lower cost (with possible interruption).</summary>
    [JsonPropertyName("spot")]
    public bool Spot { get; set; } = false;

    /// <summary>Fargate on-demand vCPU quota. 0 = query from AWS at runtime.</summary>
    [JsonPropertyName("fargate_quota_vcpu")]
    public double FargateQuotaVcpu { get; set; } = 0.0;

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads the burst configuration from disk.
    /// The path is resolved in order:
    /// <list type="number">
    ///   <item><description><c>BURST_CONFIG_PATH</c> environment variable</description></item>
    ///   <item><description><c>~/.burst/config.json</c></description></item>
    /// </list>
    /// </summary>
    /// <exception cref="TacetSetupException">Thrown if the config file is missing or malformed.</exception>
    public static TacetConfig Load()
    {
        var path = Environment.GetEnvironmentVariable("BURST_CONFIG_PATH");
        if (string.IsNullOrEmpty(path))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, ".burst", "config.json");
        }

        if (!File.Exists(path))
            throw new TacetSetupException(
                "load config",
                $"config file not found at {path}",
                "create ~/.burst/config.json or set BURST_CONFIG_PATH");

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new TacetSetupException("load config", $"could not read {path}: {ex.Message}",
                "check file permissions");
        }

        TacetConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<TacetConfig>(json, _opts);
        }
        catch (JsonException ex)
        {
            throw new TacetSetupException("load config", $"invalid JSON in {path}: {ex.Message}",
                "check config file syntax");
        }

        if (cfg is null)
            throw new TacetSetupException("load config", "config file is empty", "populate ~/.burst/config.json");

        return cfg;
    }
}
