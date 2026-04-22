# IL Forge

Compile-time dependency injection for Unity using IL post-processing. No reflection, no runtime overhead — services are wired directly into your IL at build time.

## Installation

Add to your Unity project via `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.huyhung1404.ilforge": "https://github.com/huyhung1404/com.huyhung1404.ilforge.git"
  }
}
```

**Requires:** Unity 2019.1+, `com.unity.nuget.mono-cecil` 1.11.4

## Quick Start

### 1. Enable ILForge

Go to **Project Settings > IL Forge** and toggle the switch on.

### 2. Register a service

```csharp
using ILForge;

public class GameBootstrap : MonoBehaviour
{
    [Service]
    private void RegisterServices(ILogger logger, IAudioManager audio)
    {
        // This method is called by your code.
        // ILForge injects IL at the start to store each parameter
        // into a global static field, making them available to [Wired].
    }

    private void Awake()
    {
        RegisterServices(new ConsoleLogger(), new FmodAudioManager());
    }
}
```

### 3. Consume a service

```csharp
using ILForge;

public class PlayerController : MonoBehaviour
{
    [Wired] private ILogger _logger;
    [Wired] private IAudioManager _audio;

    private void Start()
    {
        _logger.Log("PlayerController ready");
        _audio.Play("spawn");
    }
}
```

That's it. At compile time, ILForge generates IL that assigns the registered instances to `_logger` and `_audio` inside `Awake` — no `GetComponent`, no `FindObjectOfType`, no service locator boilerplate.

## Attributes

### `[Service]`

Marks a method as a service registration point. Each parameter becomes a service that can be injected via `[Wired]`.

```csharp
[Service]
private void Register(ILogger logger, IInputSystem input) { }
```

At compile time, ILForge injects `stsfld` instructions at the start of this method to store each argument into a centralized static field.

### `[Wired]`

Marks a field or auto-property for injection. ILForge injects `ldsfld` + `stfld` to assign the matching service.

```csharp
// Field injection
[Wired] private ILogger _logger;

// Auto-property injection
[Wired] public IInputSystem Input { get; private set; }
```

**Restrictions:**
- Cannot be used on `static` members
- Only auto-properties are supported (properties with custom getter/setter will produce a warning)
- The field type must not be an unbound generic parameter

### `[AfterWired]`

Marks a method to be called **after** all `[Wired]` fields on the type are assigned. Useful for initialization logic that depends on injected services.

```csharp
[Wired] private ILogger _logger;

[AfterWired]
private void OnServicesReady()
{
    _logger.Log("All services injected!");
}
```

**Order:** Use `[AfterWired(order)]` to control execution order when multiple methods exist:

```csharp
[AfterWired(0)]
private void InitFirst() { }

[AfterWired(1)]
private void InitSecond() { }
```

**Restrictions:** Method must have **no parameters**.

### `[WiredRegister]`

Overrides **where** the wiring code runs. By default, ILForge injects into `Awake` (MonoBehaviour) or constructors (plain classes). With `[WiredRegister]`, injection happens at the start of the marked method instead.

```csharp
public class NetworkPlayer : MonoBehaviour
{
    [Wired] private ILogger _logger;

    [WiredRegister]
    public void Initialize()
    {
        // ILForge injects wiring code HERE instead of Awake.
        // Your code runs after injection is complete.
        _logger.Log("NetworkPlayer initialized");
    }
}
```

This is useful when you need to control the exact moment injection occurs — for example, in pooled objects or network-spawned entities where `Awake` runs too early.

**Restrictions:** Method must be an instance method with **no parameters** and must have a body.

## Scopes

By default, all services and wired fields use `GlobalScope`. You can create custom scopes to segment services into separate containers:

```csharp
using ILForge;

// Define a custom scope
public sealed class GameplayScope : Scope { }
```

```csharp
// Register services in a specific scope
[Service(typeof(GameplayScope))]
private void RegisterGameplay(IEnemySpawner spawner, IScoreManager score) { }

// Consume from that scope
[Wired(typeof(GameplayScope))] private IEnemySpawner _spawner;
[Wired(typeof(GameplayScope))] private IScoreManager _score;
```

A `[Wired]` field only resolves from the same scope as the `[Service]` that registered it. Mismatched scopes result in a compile-time error.

## How It Works

ILForge uses Unity's `ILPostProcessor` to rewrite IL at compile time:

```
Compile
  -> ILPostProcessor (WiredWeaver) runs for each assembly
     -> Generates ILForge.Generate.dll with static fields for all services
     -> [Service] methods: injects stsfld to store parameters into static fields
     -> [Wired] fields: injects ldsfld + stfld to read from static fields
     -> Hooks execution into Awake / constructor / [WiredRegister] method
  -> Domain reload — everything resolves at runtime with zero reflection
```

### Injection Points

| Type | Default injection point |
|------|------------------------|
| `MonoBehaviour` | Start of `Awake()` (created automatically if missing) |
| Plain class | After `base()` constructor call |
| Struct | Start of constructor |
| Any (with `[WiredRegister]`) | Start of the marked method |

### Execution Order

When a type has `[Wired]` fields, ILForge generates two hidden methods:

1. **`ILForge_InitWired`** — assigns all `[Wired]` fields from static storage
2. **`ILForge_ExecuteAfterWired`** — calls `ILForge_InitWired`, then all `[AfterWired]` methods in order

The executor is called from the injection point (Awake / constructor / `[WiredRegister]`).

## Complete Example

```csharp
// --- Interfaces ---
public interface ILogger
{
    void Log(string message);
}

public interface IAudioManager
{
    void Play(string clip);
}
```

```csharp
// --- Implementations ---
public class ConsoleLogger : ILogger
{
    public void Log(string message) => Debug.Log(message);
}

public class SimpleAudioManager : IAudioManager
{
    public void Play(string clip) => Debug.Log($"Playing: {clip}");
}
```

```csharp
// --- Bootstrap: register services ---
using ILForge;

public class AppBootstrap : MonoBehaviour
{
    [Service]
    private void Register(ILogger logger, IAudioManager audio) { }

    private void Awake()
    {
        Register(new ConsoleLogger(), new SimpleAudioManager());
    }
}
```

```csharp
// --- Consumer: automatic injection ---
using ILForge;

public class Enemy : MonoBehaviour
{
    [Wired] private ILogger _logger;
    [Wired] private IAudioManager _audio;

    [AfterWired]
    private void OnReady()
    {
        _logger.Log("Enemy spawned");
        _audio.Play("enemy_spawn");
    }
}
```

```csharp
// --- Deferred injection with WiredRegister ---
using ILForge;

public class PooledBullet : MonoBehaviour
{
    [Wired] private IAudioManager _audio;

    [WiredRegister]
    public void Activate()
    {
        // Wiring happens here, not in Awake
        _audio.Play("bullet_fire");
    }
}
```

```csharp
// --- Plain class injection (constructor) ---
using ILForge;

public class GameAnalytics
{
    [Wired] private ILogger _logger;

    [AfterWired]
    private void Init()
    {
        _logger.Log("Analytics initialized");
    }

    public void TrackEvent(string name)
    {
        _logger.Log($"Event: {name}");
    }
}

// Usage:
var analytics = new GameAnalytics(); // _logger is injected in the constructor
analytics.TrackEvent("level_start");
```

## License

MIT
