using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;

namespace Tacet.Internal;

/// <summary>
/// Worker loop: runs inside the ECS container, downloads a task from S3,
/// dispatches each item through the registered function, and uploads the result.
/// </summary>
internal static class Worker
{
    /// <summary>
    /// Returns <c>true</c> when the binary is running inside an ECS worker container
    /// (i.e., <c>BURST_WORKER=1</c> is set).
    /// </summary>
    internal static bool IsWorker =>
        Environment.GetEnvironmentVariable("BURST_WORKER") == "1";

    /// <summary>
    /// Executes the worker loop.
    /// </summary>
    /// <returns>0 on success, 1 on failure (suitable for <c>Environment.Exit</c>).</returns>
    internal static async Task<int> RunAsync()
    {
        try
        {
            await RunInternalAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tacet worker fatal error: {ex}");
            return 1;
        }
    }

    private static async Task RunInternalAsync()
    {
        var sessionId = RequireEnv("BURST_SESSION_ID");
        var taskId    = RequireEnv("BURST_TASK_ID");
        var fnName    = RequireEnv("BURST_FUNCTION_NAME");
        var bucket    = RequireEnv("BURST_S3_BUCKET");
        var region    = RequireEnv("BURST_REGION");

        // Parse task index from task ID (e.g. "task-0003" → 3)
        var taskIndex = ParseTaskIndex(taskId);

        var s3 = AwsHelpers.BuildS3Client(region);

        // Write "running" status immediately so the orchestrator knows we started
        await PutTextAsync(s3, bucket, Protocol.StatusKey(sessionId, taskIndex), "running")
            .ConfigureAwait(false);

        // Verify the function is registered
        if (!FunctionRegistry.IsRegistered(fnName))
        {
            var msg = $"function '{fnName}' not registered in this worker binary";
            await PutTextAsync(s3, bucket, Protocol.ErrorKey(sessionId, taskIndex), msg).ConfigureAwait(false);
            await PutTextAsync(s3, bucket, Protocol.StatusKey(sessionId, taskIndex), "failed").ConfigureAwait(false);
            throw new InvalidOperationException(msg);
        }

        // Download task payload
        TaskPayload payload;
        try
        {
            var taskJson = await GetTextAsync(s3, bucket, Protocol.TaskKey(sessionId, taskIndex))
                .ConfigureAwait(false);
            payload = JsonSerializer.Deserialize<TaskPayload>(taskJson, Protocol.JsonOpts)
                ?? throw new InvalidOperationException("task payload deserialized to null");
        }
        catch (Exception ex)
        {
            var msg = $"downloading/parsing task: {ex.Message}";
            await PutTextAsync(s3, bucket, Protocol.ErrorKey(sessionId, taskIndex), msg).ConfigureAwait(false);
            await PutTextAsync(s3, bucket, Protocol.StatusKey(sessionId, taskIndex), "failed").ConfigureAwait(false);
            throw;
        }

        // Process each item
        var results = new JsonElement?[payload.Items.Length];
        var errors  = new string?[payload.Items.Length];
        var anyFailed = false;

        for (int i = 0; i < payload.Items.Length; i++)
        {
            try
            {
                results[i] = FunctionRegistry.Call(fnName, payload.Items[i]);
            }
            catch (Exception ex)
            {
                errors[i] = ex.Message;
                anyFailed = true;
            }
        }

        // Serialize and upload result
        var resultPayload = new ResultPayload(results, errors);
        string resultJson;
        try
        {
            resultJson = JsonSerializer.Serialize(resultPayload, Protocol.JsonOpts);
        }
        catch (Exception ex)
        {
            var msg = $"serializing result: {ex.Message}";
            await PutTextAsync(s3, bucket, Protocol.ErrorKey(sessionId, taskIndex), msg).ConfigureAwait(false);
            await PutTextAsync(s3, bucket, Protocol.StatusKey(sessionId, taskIndex), "failed").ConfigureAwait(false);
            throw new InvalidOperationException(msg, ex);
        }

        await PutTextAsync(s3, bucket, Protocol.ResultKey(sessionId, taskIndex), resultJson)
            .ConfigureAwait(false);

        var status = anyFailed ? "partial" : "done";
        await PutTextAsync(s3, bucket, Protocol.StatusKey(sessionId, taskIndex), status)
            .ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string RequireEnv(string name)
    {
        var val = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(val))
            throw new InvalidOperationException($"required environment variable {name} is not set");
        return val;
    }

    private static int ParseTaskIndex(string taskId)
    {
        // taskId format: "task-0003"
        var parts = taskId.Split('-');
        if (parts.Length == 2 && int.TryParse(parts[1], out var idx))
            return idx;
        throw new InvalidOperationException($"invalid task ID format: '{taskId}' (expected 'task-NNNN')");
    }

    private static async Task PutTextAsync(AmazonS3Client s3, string bucket, string key, string text)
    {
        var req = new PutObjectRequest
        {
            BucketName  = bucket,
            Key         = key,
            ContentBody = text,
            ContentType = "text/plain",
        };
        await s3.PutObjectAsync(req).ConfigureAwait(false);
    }

    private static async Task<string> GetTextAsync(AmazonS3Client s3, string bucket, string key)
    {
        var resp = await s3.GetObjectAsync(bucket, key).ConfigureAwait(false);
        using var reader = new StreamReader(resp.ResponseStream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}
