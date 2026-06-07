using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.S3;
using Amazon.S3.Model;
using SysTask = System.Threading.Tasks.Task;
using Ec2Filter = Amazon.EC2.Model.Filter;
using EcsKvp = Amazon.ECS.Model.KeyValuePair;
using EcsCpuArch = Amazon.ECS.CPUArchitecture;
using EcsOsFamily = Amazon.ECS.OSFamily;

namespace Tacet.Internal;

/// <summary>
/// Resolved options after merging <see cref="MapOptions"/> with <see cref="TacetConfig"/> defaults.
/// </summary>
internal sealed record ResolvedOptions(
    int Workers,
    int Cpu,
    int MemoryGb,
    string Backend,
    bool Spot,
    double MaxCostUsd,
    double CostAlertUsd,
    string Region,
    string S3Bucket,
    string EcsCluster,
    string ExecutionRoleArn,
    string TaskRoleArn,
    string ImageUri,
    string Arch);

/// <summary>
/// Full orchestration for a single burst session: chunk → S3 → manifest → ECS → poll → collect.
/// </summary>
internal static class Session
{
    private const int MaxConcurrentUploads = 20;

    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs the full burst lifecycle for the given items and registered function name.
    /// </summary>
    internal static async Task<List<U>> RunAsync<T, U>(
        IEnumerable<T> items,
        string fnName,
        MapOptions? opts,
        CancellationToken ct)
        where T : notnull
        where U : notnull
    {
        var cfg = TacetConfig.Load();
        var resolved = Resolve(opts, cfg);

        // Cost guard
        var costPerHour = Protocol.EstimateCostPerHour(resolved.Cpu, resolved.MemoryGb, resolved.Workers);
        if (resolved.MaxCostUsd > 0 && costPerHour > resolved.MaxCostUsd)
            throw new TacetCostLimitException(resolved.MaxCostUsd, costPerHour);

        if (resolved.CostAlertUsd > 0 && costPerHour > resolved.CostAlertUsd)
            Console.Error.WriteLine($"tacet: cost alert — estimated ${costPerHour:F2}/hr exceeds threshold ${resolved.CostAlertUsd:F2}/hr");

        var itemList = items.ToList();
        if (itemList.Count == 0)
            return new List<U>();

        var sessionId = Protocol.GenerateSessionId();
        var chunkCount = Math.Min(resolved.Workers, itemList.Count);
        var chunks = ChunkItems(itemList, chunkCount);

        var s3 = AwsHelpers.BuildS3Client(resolved.Region);

        // Upload all chunks concurrently (max 20 in-flight)
        await UploadChunksAsync(s3, resolved.S3Bucket, sessionId, fnName, chunks, ct)
            .ConfigureAwait(false);

        // Write initial manifest
        var manifest = BuildManifest(sessionId, resolved, chunks.Count, itemList.Count, "running", costPerHour);
        await PutJsonAsync(s3, resolved.S3Bucket, Protocol.ManifestKey(sessionId), manifest, ct)
            .ConfigureAwait(false);

        // Discover VPC and launch ECS tasks
        var ecs = AwsHelpers.BuildEcsClient(resolved.Region);
        var ec2 = AwsHelpers.BuildEc2Client(resolved.Region);

        var (subnets, securityGroup) = await DiscoverVpcAsync(ec2, ct).ConfigureAwait(false);

        var taskDefArn = await RegisterTaskDefinitionAsync(
            ecs, sessionId, resolved, ct).ConfigureAwait(false);

        await LaunchWorkersAsync(
            ecs, taskDefArn, sessionId, fnName, resolved, subnets, securityGroup, chunks.Count, ct)
            .ConfigureAwait(false);

        // Poll until all chunks terminal
        var statuses = await PollUntilDoneAsync(s3, resolved.S3Bucket, sessionId, chunks.Count, ct)
            .ConfigureAwait(false);

        // Download and assemble results
        var results = await CollectResultsAsync<U>(s3, resolved.S3Bucket, sessionId, chunks, statuses, ct)
            .ConfigureAwait(false);

        // Fire-and-forget cleanup
        _ = SysTask.Run(() => CleanupAsync(s3, resolved.S3Bucket, sessionId, chunks.Count), CancellationToken.None);

        return results;
    }

    /// <summary>
    /// Tolerant variant of <see cref="RunAsync{T,U}"/>: never throws
    /// <see cref="TacetPartialException"/>. Returns one <see cref="TacetResult{U}"/> per item.
    /// </summary>
    internal static async Task<List<TacetResult<U>>> RunTolerantAsync<T, U>(
        IEnumerable<T> items,
        string fnName,
        MapOptions? opts,
        CancellationToken ct)
        where T : notnull
        where U : notnull
    {
        var cfg = TacetConfig.Load();
        var resolved = Resolve(opts, cfg);

        // Cost guard
        var costPerHour = Protocol.EstimateCostPerHour(resolved.Cpu, resolved.MemoryGb, resolved.Workers);
        if (resolved.MaxCostUsd > 0 && costPerHour > resolved.MaxCostUsd)
            throw new TacetCostLimitException(resolved.MaxCostUsd, costPerHour);

        if (resolved.CostAlertUsd > 0 && costPerHour > resolved.CostAlertUsd)
            Console.Error.WriteLine($"tacet: cost alert — estimated ${costPerHour:F2}/hr exceeds threshold ${resolved.CostAlertUsd:F2}/hr");

        var itemList = items.ToList();
        if (itemList.Count == 0)
            return new List<TacetResult<U>>();

        var sessionId = Protocol.GenerateSessionId();
        var chunkCount = Math.Min(resolved.Workers, itemList.Count);
        var chunks = ChunkItems(itemList, chunkCount);

        var s3 = AwsHelpers.BuildS3Client(resolved.Region);

        await UploadChunksAsync(s3, resolved.S3Bucket, sessionId, fnName, chunks, ct)
            .ConfigureAwait(false);

        var manifest = BuildManifest(sessionId, resolved, chunks.Count, itemList.Count, "running", costPerHour);
        await PutJsonAsync(s3, resolved.S3Bucket, Protocol.ManifestKey(sessionId), manifest, ct)
            .ConfigureAwait(false);

        var ecs = AwsHelpers.BuildEcsClient(resolved.Region);
        var ec2 = AwsHelpers.BuildEc2Client(resolved.Region);

        var (subnets, securityGroup) = await DiscoverVpcAsync(ec2, ct).ConfigureAwait(false);

        var taskDefArn = await RegisterTaskDefinitionAsync(
            ecs, sessionId, resolved, ct).ConfigureAwait(false);

        await LaunchWorkersAsync(
            ecs, taskDefArn, sessionId, fnName, resolved, subnets, securityGroup, chunks.Count, ct)
            .ConfigureAwait(false);

        var statuses = await PollUntilDoneAsync(s3, resolved.S3Bucket, sessionId, chunks.Count, ct)
            .ConfigureAwait(false);

        var results = await CollectResultsTolerantAsync<U>(s3, resolved.S3Bucket, sessionId, chunks, statuses, ct)
            .ConfigureAwait(false);

        _ = SysTask.Run(() => CleanupAsync(s3, resolved.S3Bucket, sessionId, chunks.Count), CancellationToken.None);

        return results;
    }

    /// <summary>
    /// Streaming variant: yields results as individual chunks complete.
    /// </summary>
    internal static async IAsyncEnumerable<U> StreamAsync<T, U>(
        IEnumerable<T> items,
        string fnName,
        MapOptions? opts,
        [EnumeratorCancellation] CancellationToken ct)
        where T : notnull
        where U : notnull
    {
        var cfg = TacetConfig.Load();
        var resolved = Resolve(opts, cfg);

        var itemList = items.ToList();
        if (itemList.Count == 0)
            yield break;

        var sessionId = Protocol.GenerateSessionId();
        var chunkCount = Math.Min(resolved.Workers, itemList.Count);
        var chunks = ChunkItems(itemList, chunkCount);

        var s3 = AwsHelpers.BuildS3Client(resolved.Region);
        await UploadChunksAsync(s3, resolved.S3Bucket, sessionId, fnName, chunks, ct).ConfigureAwait(false);

        var manifest = BuildManifest(sessionId, resolved, chunks.Count, itemList.Count, "running",
            Protocol.EstimateCostPerHour(resolved.Cpu, resolved.MemoryGb, resolved.Workers));
        await PutJsonAsync(s3, resolved.S3Bucket, Protocol.ManifestKey(sessionId), manifest, ct).ConfigureAwait(false);

        var ecs = AwsHelpers.BuildEcsClient(resolved.Region);
        var ec2 = AwsHelpers.BuildEc2Client(resolved.Region);
        var (subnets, securityGroup) = await DiscoverVpcAsync(ec2, ct).ConfigureAwait(false);
        var taskDefArn = await RegisterTaskDefinitionAsync(ecs, sessionId, resolved, ct).ConfigureAwait(false);
        await LaunchWorkersAsync(ecs, taskDefArn, sessionId, fnName, resolved, subnets, securityGroup, chunks.Count, ct)
            .ConfigureAwait(false);

        var done = new bool[chunks.Count];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < chunks.Count; i++)
            {
                if (done[i]) continue;
                var status = await TryGetStatusAsync(s3, resolved.S3Bucket, sessionId, i).ConfigureAwait(false);
                if (status is "done" or "partial" or "failed")
                {
                    done[i] = true;
                    if (status is "done" or "partial")
                    {
                        var payload = await DownloadResultAsync(s3, resolved.S3Bucket, sessionId, i, ct)
                            .ConfigureAwait(false);
                        foreach (var result in DeserializeResults<U>(payload, chunks[i].Count))
                            yield return result;
                    }
                }
            }
            if (done.All(x => x)) break;
            await SysTask.Delay(2_000, ct).ConfigureAwait(false);
        }

        _ = SysTask.Run(() => CleanupAsync(s3, resolved.S3Bucket, sessionId, chunks.Count), CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Option resolution
    // -------------------------------------------------------------------------

    private static ResolvedOptions Resolve(MapOptions? opts, TacetConfig cfg)
    {
        var workers   = opts?.Workers   ?? cfg.DefaultWorkers;
        var cpu       = opts?.Cpu       ?? cfg.DefaultCpu;
        var memoryGb  = opts?.MemoryGb  ?? cfg.DefaultMemoryGb;
        var backend   = opts?.Backend   ?? cfg.Backend;
        var spot      = opts?.Spot      ?? cfg.Spot;
        var maxCost   = opts?.MaxCostUsd    ?? cfg.MaxCostPerJob;
        var costAlert = opts?.CostAlertUsd  ?? cfg.CostAlertThreshold;
        var region    = opts?.Region    ?? cfg.Region;
        var arch      = opts?.Arch ?? "amd64";

        var imageUri = opts?.ImageUri
            ?? $"{cfg.EcrBaseUri}/burst-workers-csharp:latest";

        return new ResolvedOptions(
            workers, cpu, memoryGb, backend, spot, maxCost, costAlert,
            region, cfg.S3Bucket, cfg.EcsCluster, cfg.ExecutionRoleArn, cfg.TaskRoleArn,
            imageUri, arch);
    }

    // -------------------------------------------------------------------------
    // Chunking
    // -------------------------------------------------------------------------

    private static List<List<JsonElement>> ChunkItems<T>(List<T> items, int chunkCount) where T : notnull
    {
        var serialized = items
            .Select(item => JsonSerializer.SerializeToElement(item, Protocol.JsonOpts))
            .ToList();

        var chunks = new List<List<JsonElement>>(chunkCount);
        var baseSize = serialized.Count / chunkCount;
        var remainder = serialized.Count % chunkCount;
        int offset = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            chunks.Add(serialized.GetRange(offset, size));
            offset += size;
        }
        return chunks;
    }

    // -------------------------------------------------------------------------
    // S3 upload
    // -------------------------------------------------------------------------

    private static async SysTask UploadChunksAsync(
        AmazonS3Client s3,
        string bucket,
        string sessionId,
        string fnName,
        List<List<JsonElement>> chunks,
        CancellationToken ct)
    {
        var sem = new SemaphoreSlim(MaxConcurrentUploads);
        var tasks = chunks.Select(async (chunk, i) =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var payload = new TaskPayload(chunk.ToArray(), fnName, i);
                var json = JsonSerializer.Serialize(payload, Protocol.JsonOpts);
                await PutTextAsync(s3, bucket, Protocol.TaskKey(sessionId, i), json, ct).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        });
        await SysTask.WhenAll(tasks).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Manifest
    // -------------------------------------------------------------------------

    private static Manifest BuildManifest(
        string sessionId,
        ResolvedOptions r,
        int chunkCount,
        int taskCount,
        string status,
        double costPerHour)
    {
        return new Manifest(
            SessionId: sessionId,
            Language: Protocol.Language,
            Status: status,
            TasksTotal: chunkCount,
            TasksComplete: 0,
            TasksFailed: 0,
            WorkersActive: r.Workers,
            CostActual: 0.0,
            CostEstimatePerHour: costPerHour,
            CreatedAt: DateTime.UtcNow.ToString("O"),
            ChunkCount: chunkCount,
            TaskCount: taskCount,
            WorkersRequested: r.Workers,
            WorkersActual: r.Workers,
            Cpu: r.Cpu,
            MemoryGb: r.MemoryGb,
            Backend: r.Backend,
            Spot: r.Spot,
            Region: r.Region,
            EnvHash: string.Empty,
            LibraryVersion: Protocol.LibraryVersion);
    }

    // -------------------------------------------------------------------------
    // VPC discovery
    // -------------------------------------------------------------------------

    private static async Task<(List<string> Subnets, string SecurityGroup)> DiscoverVpcAsync(
        AmazonEC2Client ec2,
        CancellationToken ct)
    {
        // Find the default VPC
        var vpcResp = await ec2.DescribeVpcsAsync(new DescribeVpcsRequest
        {
            Filters = new List<Ec2Filter>
            {
                new Ec2Filter { Name = "isDefault", Values = new List<string> { "true" } }
            }
        }, ct).ConfigureAwait(false);

        string vpcId;
        if (vpcResp.Vpcs.Count > 0)
        {
            vpcId = vpcResp.Vpcs[0].VpcId;
        }
        else
        {
            throw new TacetSetupException(
                "discover VPC",
                "no default VPC found in region",
                "create a default VPC or specify subnet/security group IDs via MapOptions");
        }

        // Find subnets in the default VPC
        var subnetResp = await ec2.DescribeSubnetsAsync(new DescribeSubnetsRequest
        {
            Filters = new List<Ec2Filter>
            {
                new Ec2Filter { Name = "vpc-id", Values = new List<string> { vpcId } },
                new Ec2Filter { Name = "defaultForAz", Values = new List<string> { "true" } },
            }
        }, ct).ConfigureAwait(false);

        var subnets = subnetResp.Subnets.Select(s => s.SubnetId).ToList();
        if (subnets.Count == 0)
            throw new TacetSetupException("discover VPC subnets", "no default subnets found",
                "ensure default VPC has subnets or specify subnet IDs");

        // Find the default security group
        var sgResp = await ec2.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest
        {
            Filters = new List<Ec2Filter>
            {
                new Ec2Filter { Name = "vpc-id", Values = new List<string> { vpcId } },
                new Ec2Filter { Name = "group-name", Values = new List<string> { "default" } },
            }
        }, ct).ConfigureAwait(false);

        var sg = sgResp.SecurityGroups.FirstOrDefault()?.GroupId
            ?? throw new TacetSetupException("discover security group",
                "no default security group found in default VPC",
                "check VPC configuration");

        return (subnets, sg);
    }

    // -------------------------------------------------------------------------
    // ECS task definition registration
    // -------------------------------------------------------------------------

    private static async Task<string> RegisterTaskDefinitionAsync(
        AmazonECSClient ecs,
        string sessionId,
        ResolvedOptions r,
        CancellationToken ct)
    {
        var family = $"burst-{sessionId}";
        var cpuUnits = r.Cpu * 1024; // Fargate expects CPU in units (1 vCPU = 1024 units)
        var memoryMb = r.MemoryGb * 1024;

        var cpuArch = r.Arch == "arm64"
            ? EcsCpuArch.ARM64
            : EcsCpuArch.X86_64;

        var env = new List<EcsKvp>
        {
            new() { Name = "BURST_WORKER",     Value = "1" },
            new() { Name = "BURST_SESSION_ID", Value = sessionId },
            new() { Name = "BURST_S3_BUCKET",  Value = r.S3Bucket },
            new() { Name = "BURST_REGION",     Value = r.Region },
        };

        var req = new RegisterTaskDefinitionRequest
        {
            Family                  = family,
            NetworkMode             = NetworkMode.Awsvpc,
            RequiresCompatibilities = new List<string> { "FARGATE" },
            Cpu                     = cpuUnits.ToString(),
            Memory                  = memoryMb.ToString(),
            ExecutionRoleArn        = r.ExecutionRoleArn,
            TaskRoleArn             = r.TaskRoleArn,
            RuntimePlatform = new RuntimePlatform
            {
                CpuArchitecture       = cpuArch,
                OperatingSystemFamily = OSFamily.LINUX,
            },
            ContainerDefinitions = new List<ContainerDefinition>
            {
                new()
                {
                    Name        = "worker",
                    Image       = r.ImageUri,
                    Essential   = true,
                    Environment = env,
                    LogConfiguration = new LogConfiguration
                    {
                        LogDriver = LogDriver.Awslogs,
                        Options = new Dictionary<string, string>
                        {
                            ["awslogs-group"]         = "/burst/workers",
                            ["awslogs-region"]        = r.Region,
                            ["awslogs-stream-prefix"] = "burst",
                            ["awslogs-create-group"]  = "true",
                        },
                    },
                }
            },
        };

        try
        {
            var resp = await ecs.RegisterTaskDefinitionAsync(req, ct).ConfigureAwait(false);
            return resp.TaskDefinition.TaskDefinitionArn;
        }
        catch (Exception ex)
        {
            throw new TacetSetupException("register task definition", ex.Message,
                "ensure execution role has ecs:RegisterTaskDefinition permission");
        }
    }

    // -------------------------------------------------------------------------
    // Worker launch
    // -------------------------------------------------------------------------

    private static async SysTask LaunchWorkersAsync(
        AmazonECSClient ecs,
        string taskDefArn,
        string sessionId,
        string fnName,
        ResolvedOptions r,
        List<string> subnets,
        string securityGroup,
        int chunkCount,
        CancellationToken ct)
    {
        var clusterName = r.EcsCluster;

        // Spot uses capacity provider strategy; on-demand uses explicit LaunchType=FARGATE.
        // Setting both on the same request causes an ECS validation error.
        var capacityStrategy = r.Spot
            ? new List<CapacityProviderStrategyItem>
              {
                  new() { CapacityProvider = "FARGATE_SPOT", Weight = 1 }
              }
            : null;

        var tasks = Enumerable.Range(0, chunkCount).Select(async i =>
        {
            var req = new RunTaskRequest
            {
                Cluster         = clusterName,
                TaskDefinition  = taskDefArn,
                NetworkConfiguration = new NetworkConfiguration
                {
                    AwsvpcConfiguration = new AwsVpcConfiguration
                    {
                        Subnets         = subnets,
                        SecurityGroups  = new List<string> { securityGroup },
                        AssignPublicIp  = AssignPublicIp.ENABLED,
                    }
                },
                Overrides = new TaskOverride
                {
                    ContainerOverrides = new List<ContainerOverride>
                    {
                        new()
                        {
                            Name = "worker",
                            Environment = new List<EcsKvp>
                            {
                                new() { Name = "BURST_TASK_ID",        Value = Protocol.TaskId(i) },
                                new() { Name = "BURST_FUNCTION_NAME",  Value = fnName },
                            }
                        }
                    }
                },
            };

            if (r.Spot && capacityStrategy is not null)
                req.CapacityProviderStrategy = capacityStrategy;
            else
                req.LaunchType = LaunchType.FARGATE;

            var resp = await ecs.RunTaskAsync(req, ct).ConfigureAwait(false);
            if (resp.Failures.Count > 0)
            {
                var f = resp.Failures[0];
                throw new TacetSetupException("launch worker",
                    $"ECS RunTask failure for task {i}: {f.Reason} — {f.Detail}",
                    "check ECS cluster capacity and task role permissions");
            }
        });

        await SysTask.WhenAll(tasks).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Polling
    // -------------------------------------------------------------------------

    private static async Task<string?[]> PollUntilDoneAsync(
        AmazonS3Client s3,
        string bucket,
        string sessionId,
        int chunkCount,
        CancellationToken ct)
    {
        var statuses = new string?[chunkCount];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var pending = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                if (IsTerminal(statuses[i])) continue;

                var s = await TryGetStatusAsync(s3, bucket, sessionId, i).ConfigureAwait(false);
                if (s is not null)
                    statuses[i] = s;

                if (!IsTerminal(statuses[i]))
                    pending++;
            }

            if (pending == 0) break;
            await SysTask.Delay(2_000, ct).ConfigureAwait(false);
        }

        return statuses;
    }

    private static async Task<string?> TryGetStatusAsync(
        AmazonS3Client s3, string bucket, string sessionId, int index)
    {
        try
        {
            var resp = await s3.GetObjectAsync(bucket, Protocol.StatusKey(sessionId, index))
                .ConfigureAwait(false);
            using var reader = new StreamReader(resp.ResponseStream);
            return (await reader.ReadToEndAsync().ConfigureAwait(false)).Trim();
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static bool IsTerminal(string? status) =>
        status is "done" or "partial" or "failed";

    // -------------------------------------------------------------------------
    // Result collection
    // -------------------------------------------------------------------------

    private static async Task<List<U>> CollectResultsAsync<U>(
        AmazonS3Client s3,
        string bucket,
        string sessionId,
        List<List<JsonElement>> chunks,
        string?[] statuses,
        CancellationToken ct)
        where U : notnull
    {
        var results = new List<U>(chunks.Sum(c => c.Count));
        var partialResults = new List<JsonElement?>();
        var partialErrors = new List<string?>();
        var anyPartial = false;

        for (int i = 0; i < chunks.Count; i++)
        {
            if (statuses[i] == "failed")
            {
                // Entire chunk failed — fill nulls
                for (int j = 0; j < chunks[i].Count; j++)
                {
                    partialResults.Add(null);
                    partialErrors.Add($"chunk {i} failed entirely");
                }
                anyPartial = true;
                continue;
            }

            var payload = await DownloadResultAsync(s3, bucket, sessionId, i, ct).ConfigureAwait(false);

            for (int j = 0; j < payload.Results.Length; j++)
            {
                if (payload.Errors[j] is not null)
                {
                    partialResults.Add(null);
                    partialErrors.Add(payload.Errors[j]);
                    anyPartial = true;
                }
                else
                {
                    var item = payload.Results[j];
                    var deserialized = item.HasValue
                        ? item.Value.Deserialize<U>(Protocol.JsonOpts)
                        : default;
                    if (deserialized is not null)
                    {
                        results.Add(deserialized);
                        partialResults.Add(item);
                        partialErrors.Add(null);
                    }
                    else
                    {
                        partialResults.Add(null);
                        partialErrors.Add("null result");
                        anyPartial = true;
                    }
                }
            }
        }

        if (anyPartial)
        {
            var failed  = partialErrors.Count(e => e is not null);
            var success = partialErrors.Count(e => e is null);
            throw new TacetPartialException(partialResults, partialErrors, failed, success);
        }

        return results;
    }

    /// <summary>
    /// Tolerant variant of <see cref="CollectResultsAsync{U}"/>: never throws
    /// <see cref="TacetPartialException"/>. Returns one <see cref="TacetResult{U}"/> per item.
    /// </summary>
    internal static async Task<List<TacetResult<U>>> CollectResultsTolerantAsync<U>(
        AmazonS3Client s3,
        string bucket,
        string sessionId,
        List<List<JsonElement>> chunks,
        string?[] statuses,
        CancellationToken ct)
        where U : notnull
    {
        var out_ = new List<TacetResult<U>>(chunks.Sum(c => c.Count));

        for (int i = 0; i < chunks.Count; i++)
        {
            if (statuses[i] == "failed")
            {
                for (int j = 0; j < chunks[i].Count; j++)
                    out_.Add(TacetResult<U>.Failure($"chunk {i} failed entirely"));
                continue;
            }

            var payload = await DownloadResultAsync(s3, bucket, sessionId, i, ct).ConfigureAwait(false);

            for (int j = 0; j < payload.Results.Length; j++)
            {
                if (payload.Errors[j] is not null)
                {
                    out_.Add(TacetResult<U>.Failure(payload.Errors[j]!));
                }
                else
                {
                    var item = payload.Results[j];
                    var deserialized = item.HasValue
                        ? item.Value.Deserialize<U>(Protocol.JsonOpts)
                        : default;
                    if (deserialized is not null)
                        out_.Add(TacetResult<U>.Success(deserialized));
                    else
                        out_.Add(TacetResult<U>.Failure("null result"));
                }
            }
        }

        return out_;
    }

    private static async Task<ResultPayload> DownloadResultAsync(
        AmazonS3Client s3, string bucket, string sessionId, int index, CancellationToken ct)
    {
        var resp = await s3.GetObjectAsync(bucket, Protocol.ResultKey(sessionId, index), ct)
            .ConfigureAwait(false);
        using var reader = new StreamReader(resp.ResponseStream);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ResultPayload>(json, Protocol.JsonOpts)
            ?? throw new InvalidOperationException($"result payload for chunk {index} deserialized to null");
    }

    private static IEnumerable<U> DeserializeResults<U>(ResultPayload payload, int expectedCount)
        where U : notnull
    {
        for (int i = 0; i < payload.Results.Length; i++)
        {
            if (payload.Errors[i] is null && payload.Results[i].HasValue)
            {
                var deserialized = payload.Results[i]!.Value.Deserialize<U>(Protocol.JsonOpts);
                if (deserialized is not null)
                    yield return deserialized;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Cleanup
    // -------------------------------------------------------------------------

    private static async SysTask CleanupAsync(AmazonS3Client s3, string bucket, string sessionId, int chunkCount)
    {
        var keys = new List<string>(chunkCount * 3);
        for (int i = 0; i < chunkCount; i++)
        {
            keys.Add(Protocol.TaskKey(sessionId, i));
            keys.Add(Protocol.ResultKey(sessionId, i));
            keys.Add(Protocol.StatusKey(sessionId, i));
            keys.Add(Protocol.ErrorKey(sessionId, i));
        }

        // Delete in batches of 1000 (S3 limit)
        for (int start = 0; start < keys.Count; start += 1000)
        {
            var batch = keys.Skip(start).Take(1000)
                .Select(k => new KeyVersion { Key = k })
                .ToList();
            try
            {
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects = batch,
                }).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup; ignore errors
            }
        }
    }

    // -------------------------------------------------------------------------
    // Low-level S3 helpers
    // -------------------------------------------------------------------------

    private static async SysTask PutTextAsync(
        AmazonS3Client s3, string bucket, string key, string text, CancellationToken ct)
    {
        var req = new PutObjectRequest
        {
            BucketName  = bucket,
            Key         = key,
            ContentBody = text,
            ContentType = "text/plain",
        };
        await s3.PutObjectAsync(req, ct).ConfigureAwait(false);
    }

    private static async SysTask PutJsonAsync<T>(
        AmazonS3Client s3, string bucket, string key, T obj, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(obj, Protocol.JsonOpts);
        var req = new PutObjectRequest
        {
            BucketName  = bucket,
            Key         = key,
            ContentBody = json,
            ContentType = "application/json",
        };
        await s3.PutObjectAsync(req, ct).ConfigureAwait(false);
    }
}
