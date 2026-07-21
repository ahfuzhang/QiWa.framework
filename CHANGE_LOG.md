* 2026-07-21:  v0.7.1
  - bug fix:
    * ThreadLocalLogger 中 POST 日志失败时，会导致 unobserved exception (原因不明，比较诡异，做了更严谨的处理)
    * UnhandledException 中增加 thread name 的日志输出

