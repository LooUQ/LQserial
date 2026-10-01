# LQserial

A Windows serial terminal with a scriptable command list, built with WPF (.NET 10).

- Large terminal pane showing sent (`TX:`) and received data, with optional timestamps and hex view
- Event pane logging port open/close, errors, file transfers and script runs
- 29-row command list: per-row HEX / Enter / delay, send a single row or run the selected rows N times
- Reads and writes QCOM-format `.ini` scripts and reopens the last script at startup
- Send File, Save Log, DTR/RTS control, flow control options
- Built-in help (F1)

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```
dotnet run
```

## Script format

Scripts use the QCOM `.ini` layout: a `[SET]` section with `RunTimes` and `DelayTime`, then
sections `[1]`..`[29]` each with `Cho`, `CMD`, `Delay`, `HEX` and `Enter`.

## License

MIT. Copyright (c) 2026 LooUQ Incorporated. See [LICENSE](LICENSE).
