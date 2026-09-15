# Vestara .NET SDK

Server-side .NET SDK for Vestara crash reporting, remote logging, request correlation, and failure trails.

Supports .NET 10+.

## Installation

### ASP.NET Core Web Applications

```bash
dotnet add package Vestara.AspNetCore --version 0.1.0
```

> Installing `Vestara.AspNetCore` transitively includes the core `Vestara` package.

### Worker Services / Generic Host / Console Applications

```bash
dotnet add package Vestara --version 0.1.0
```

> Non-web applications require only the core `Vestara` package.

## Quick Start

### ASP.NET Core Web Applications

In your `Program.cs`:

```csharp
using Vestara;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddVestara(options =>
{
    options.Token = builder.Configuration["Vestara:Token"]!;
    options.Environment = builder.Environment.EnvironmentName.ToLowerInvariant();
});

builder.Services.AddVestaraAspNetCore();

var app = builder.Build();

// If the app uses UseExceptionHandler(...), register it before UseVestara().
app.UseVestara();

app.Run();
```

### Worker Services / Generic Host / Console Applications

For non-web applications, register the core SDK using `AddVestara(...)` only. Do **NOT** call `AddVestaraAspNetCore()` or `UseVestara()`.

```csharp
using Vestara;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddVestara(options =>
{
    options.Token = builder.Configuration["Vestara:Token"]!;
    options.Environment = builder.Environment.EnvironmentName.ToLowerInvariant();
});

var host = builder.Build();
host.Run();
```

## Configuration

Configure options inside `AddVestara(options => { ... })`:

- `Token`: Your Vestara project SDK token (required).
- `Environment`: Deployment environment name. Supported values: `production`, `staging`, `development`. Defaults to `production`.
- `ServiceName`: Optional logical service name to identify this application.
- `AppVersion`: Optional application version string.
- `AppIdentifier`: Optional technical identifier for the application.
- `ApiUrl`: Vestara ingestion endpoint (defaults to `https://api.vestara.dev`).
- `CaptureUnhandledExceptions`: Automatically capture unhandled AppDomain and unobserved Task exceptions (defaults to `true`).
- `BeforeSend`: Optional callback `Func<VestaraEvent, VestaraEvent?>` to inspect, modify, or drop events prior to queueing. Returning `null` drops the event.

### Before Send Hook

```csharp
builder.Services.AddVestara(options =>
{
    options.Token = builder.Configuration["Vestara:Token"]!;
    options.BeforeSend = ev =>
    {
        if (ev.Payload.TryGetValue("message", out var message) &&
            message is string text &&
            text.Contains("healthcheck", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ev;
    };
});
```

## SDK Token

Find your project SDK token in the Vestara dashboard under **Settings → SDK & Token**.

> The SDK token is write-only — it can only ingest events. It cannot read your data.

For local development, configure your token using .NET user-secrets or an environment variable:

```bash
dotnet user-secrets set "Vestara:Token" "YOUR_SDK_TOKEN"
```

or via environment variable:

```bash
export Vestara__Token="YOUR_SDK_TOKEN"
```

Never commit SDK tokens to source control.

## License

This project is licensed under the Apache-2.0 License. See [LICENSE](LICENSE) for details.

For more information, visit [https://www.vestara.dev](https://www.vestara.dev).