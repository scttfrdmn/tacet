namespace Tacet;

/// <summary>
/// Options that control how <see cref="Tacet.Map{T,U}"/>,
/// <see cref="Tacet.MapAsync{T,U}"/>, <see cref="Tacet.MapStreamAsync{T,U}"/>,
/// and <see cref="Tacet.MapChannel{T,U}"/> distribute work.
/// </summary>
public sealed class MapOptions
{
    /// <summary>
    /// Number of ECS workers to launch. Defaults to <see cref="TacetConfig.DefaultWorkers"/>.
    /// The actual number of chunks equals <c>min(Workers, items.Count)</c>.
    /// </summary>
    public int? Workers { get; set; }

    /// <summary>
    /// Number of vCPUs per worker (valid Fargate values: 1, 2, 4, 8, 16).
    /// Defaults to <see cref="TacetConfig.DefaultCpu"/>.
    /// </summary>
    public int? Cpu { get; set; }

    /// <summary>
    /// Memory per worker in GB.
    /// Defaults to <see cref="TacetConfig.DefaultMemoryGb"/>.
    /// </summary>
    public int? MemoryGb { get; set; }

    /// <summary>
    /// Compute backend. Supported values: <c>"fargate"</c> (default), <c>"ec2"</c>.
    /// </summary>
    public string? Backend { get; set; }

    /// <summary>
    /// Whether to use Fargate Spot for lower cost (with possible interruption).
    /// Defaults to <see cref="TacetConfig.Spot"/>.
    /// </summary>
    public bool? Spot { get; set; }

    /// <summary>
    /// Maximum estimated job cost in USD. <see cref="TacetCostLimitException"/> is thrown
    /// if the estimate exceeds this value before workers are launched. 0 = unlimited.
    /// </summary>
    public double? MaxCostUsd { get; set; }

    /// <summary>
    /// Cost alert threshold in USD. A warning is written to stderr if the estimate exceeds this.
    /// </summary>
    public double? CostAlertUsd { get; set; }

    /// <summary>
    /// AWS region override. Defaults to <see cref="TacetConfig.Region"/>.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// ECR image URI to use for workers. If unset, the library looks for
    /// <c>{ecr_base_uri}/burst-workers-csharp:latest</c>.
    /// </summary>
    public string? ImageUri { get; set; }

    /// <summary>
    /// CPU architecture for the worker container: <c>"amd64"</c> (default, x86_64)
    /// or <c>"arm64"</c> (Graviton, ~20% cheaper).
    /// </summary>
    public string? Arch { get; set; }
}
