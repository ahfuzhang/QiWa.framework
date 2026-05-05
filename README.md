
# QiWa.framework

这是高性能微服务框架 QiWa 的基础库。

This is the foundation library for the high-performance microservice framework QiWa.

* NuGet 地址：https://www.nuget.org/packages/QiWa.framework/

```bash
dotnet add package QiWa.framework --version 0.4.0
```

## 提供的组件

* Compress
  - GzipCompressor: 基于 ThreadLocal 的 gzip 的压缩和解压缩能力
  - ZstdCompressor: 基于 ThreadLocal 的 zstd 的压缩和解压缩能力
* ConsoleLogger: 高性能的 JSON 日志输出组件
* DebugUtil
  - GlobalExceptionHandler: 提供全局的线程上的异常拦截能力
* FileUtils
* Helper
  - ScopeGuard: 提供类似 golang 的 defer 的能力
* KestrelWrap: 包装 Kestrel 基本的数据接收和发送的封装
* Metrics: 包装 ThreadLocal 的高性能的 Counter
  - MetricsBase: 包装 metrics 的格式化功能
  - LatencyHistogram: 用于接口延迟统计的 Histogram
* StringUtils
* Syscall
  - 封装 write() 系统调用

## 工程参数

* 代码覆盖率 94%
* 分支覆盖率 90%

## License

This project is licensed under the [MIT License](LICENSE).

Copyright (c) 2026 Fuchun Zhang

