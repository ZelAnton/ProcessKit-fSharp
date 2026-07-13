```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.22621.6060/22H2/2022Update/SunValley2)
Unknown processor
.NET SDK 10.0.301
  [Host] : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v3 DEBUG

Job=ShortRun  Toolchain=InProcessNoEmitToolchain  IterationCount=1  
LaunchCount=1  WarmupCount=1  

```
| Method           | LineLength | Terminator | Mean     | Error | Gen0   | Allocated |
|----------------- |----------- |----------- |---------:|------:|-------:|----------:|
| **LogSpawnNoLogger** | **16**         | **Any**        | **29.80 ns** |    **NA** | **0.0076** |      **64 B** |
| **LogSpawnNoLogger** | **16**         | **Cr**         | **25.16 ns** |    **NA** | **0.0076** |      **64 B** |
| **LogSpawnNoLogger** | **16**         | **CrLf**       | **30.15 ns** |    **NA** | **0.0076** |      **64 B** |
| **LogSpawnNoLogger** | **16**         | **Lf**         | **34.62 ns** |    **NA** | **0.0076** |      **64 B** |
| **LogSpawnNoLogger** | **4096**       | **Any**        | **27.18 ns** |    **NA** | **0.0076** |      **64 B** |
| **LogSpawnNoLogger** | **4096**       | **Cr**         | **33.12 ns** |    **NA** | **0.0076** |      **64 B** |
| **LogSpawnNoLogger** | **4096**       | **CrLf**       | **30.86 ns** |    **NA** | **0.0076** |      **64 B** |
| **LogSpawnNoLogger** | **4096**       | **Lf**         | **35.02 ns** |    **NA** | **0.0076** |      **64 B** |
