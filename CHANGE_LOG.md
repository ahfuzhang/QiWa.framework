* 2026-07-25: v0.9.1
  * -bug fix:
    - mysql 连接池，取得一个连接的时候，可能 connection.state 不是 open 状态，
* 2026-07-24:  v0.9.0
  - bug fix:
    - 使用库 LibDeflate 代替 dotnet 自带的流式 gzip 库
* 2026-07-23:  v0.8.0
  - bug fix:
    * mysql connection pool: 捕获 IOException 的异常；增加 Idle time 的判断
* 2026-07-21:  v0.7.1
  - bug fix:
    * ThreadLocalLogger 中 POST 日志失败时，会导致 unobserved exception (原因不明，比较诡异，做了更严谨的处理)
    * UnhandledException 中增加 thread name 的日志输出

