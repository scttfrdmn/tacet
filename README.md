# Tacet

Cloud bursting for .NET 8 — distribute CPU-intensive work across AWS ECS/Fargate workers with a single method call.

Tacet is part of the [burst family](https://github.com/scttfrdmn/burst-core): five-language cloud bursting libraries (Go, Python, TypeScript, Julia, C#) sharing a common AWS backend and wire protocol.

## Install

```bash
dotnet add package Tacet
```

## Quickstart

```csharp
using Tacet;

// 1. Register your function at startup (before IsWorker check)
Tacet.Register("process", (MyInput input) => Process(input));

// 2. In main(), route worker invocations
if (Tacet.IsWorker)
    Environment.Exit(await Tacet.RunWorkerAsync());

// 3. Distribute work — results come back in original order
var results = await Tacet.MapAsync(
    items,
    (MyInput x) => Task.FromResult(Process(x)),
    new MapOptions { Workers = 8 }
);
```

## Configuration

Create `~/.burst/config.json` (or set `BURST_CONFIG_PATH`):

```json
{
  "region": "us-east-1",
  "s3_bucket": "my-burst-bucket",
  "ecs_cluster": "burst-cluster",
  "ecr_base_uri": "123456789.dkr.ecr.us-east-1.amazonaws.com",
  "execution_role_arn": "arn:aws:iam::123456789:role/burst-execution",
  "task_role_arn": "arn:aws:iam::123456789:role/burst-task",
  "default_cpu": 1,
  "default_memory_gb": 2,
  "default_workers": 4
}
```

## API Reference

### `Tacet.Register<T, U>(name, fn)`

Registers a function under `name`. Must be called before any `Map*` call. Both `T` and `U` must be JSON-serializable.

### `Tacet.IsWorker`

Returns `true` when the process is running inside an ECS worker container (`BURST_WORKER=1`). Check this at the start of `main()`.

### `Tacet.RunWorkerAsync()`

Executes the worker loop. Returns exit code `0` on success, `1` on failure. Pair with `IsWorker`:

```csharp
if (Tacet.IsWorker)
    Environment.Exit(await Tacet.RunWorkerAsync());
```

### `Tacet.Map<T, U>(items, fn, opts?)`

Synchronous blocking map. Distributes `items` across workers and returns results in order.

```csharp
var doubled = Tacet.Map(numbers, (int x) => x * 2);
```

### `Tacet.MapAsync<T, U>(items, fn, opts?, ct?)`

Async map. Preferred over `Map` in async contexts.

```csharp
var results = await Tacet.MapAsync(records, async (Record r) => await ProcessAsync(r));
```

### `Tacet.MapStreamAsync<T, U>(items, fn, opts?, ct?)`

Returns `IAsyncEnumerable<U>`. Yields results as chunks complete — lower latency when you can start processing early results.

```csharp
await foreach (var result in Tacet.MapStreamAsync(items, fn))
    Console.WriteLine(result);
```

### `Tacet.MapChannel<T, U>(items, fn, opts?)`

Returns `ChannelReader<U>`. Results are written to the channel as chunks complete. Useful for producer-consumer pipelines.

```csharp
var reader = Tacet.MapChannel(items, fn);
await foreach (var result in reader.ReadAllAsync())
    await sink.WriteAsync(result);
```

## LINQ Extensions

```csharp
using Tacet;

// SelectBurst — synchronous
var results = myList.SelectBurst((int x) => x * 2);

// SelectBurstAsync — async
var results = await myList.SelectBurstAsync(async (int x) => await ComputeAsync(x));

// AsParallelBurst — fluent builder
var results = await myList
    .AsParallelBurst(new MapOptions { Workers = 16 })
    .Select((MyItem x) => Transform(x));
```

## Pool — Reuse Options Across Jobs

```csharp
using var pool = new TacetPool(new MapOptions { Workers = 8, Cpu = 2, Spot = true });

var r1 = await pool.MapAsync(batch1, fn);
var r2 = await pool.MapAsync(batch2, fn);
```

## MapOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Workers` | `int?` | config | Number of ECS workers |
| `Cpu` | `int?` | config | vCPUs per worker (Fargate: 1, 2, 4, 8, 16) |
| `MemoryGb` | `int?` | config | Memory per worker in GB |
| `Backend` | `string?` | `"fargate"` | Compute backend |
| `Spot` | `bool?` | config | Use Fargate Spot |
| `MaxCostUsd` | `double?` | config | Max estimated cost; throws `TacetCostLimitException` |
| `CostAlertUsd` | `double?` | config | Print warning if cost exceeds threshold |
| `Region` | `string?` | config | AWS region override |
| `Arch` | `string` | `"amd64"` | Worker CPU arch: `"amd64"` or `"arm64"` |
| `ImageUri` | `string?` | auto | ECR image URI override |

## Exceptions

| Exception | When |
|---|---|
| `TacetPartialException` | Some tasks failed; `Results`/`Errors` carry per-item details |
| `TacetCostLimitException` | Estimated cost exceeds `MaxCostUsd` |
| `TacetQuotaException` | AWS quota forced a worker reduction |
| `TacetTimeoutException` | Context cancelled/timed out |
| `TacetSetupException` | Config missing, AWS permission error, or function not registered |

## License

Apache-2.0
