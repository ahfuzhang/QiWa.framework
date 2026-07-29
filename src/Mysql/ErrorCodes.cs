#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Mysql;

public enum ErrorCodes : UInt32
{
    Success = 0,
    CreateConnectionMysqlExceptionError = 3306,
    CreateConnectionTimeoutError = 3307,
    CreateConnectionIOExceptionError = 3308,
    CreateConnectionUnknownExceptionError = 3309,
    PingMysqlExceptionError = 3310,
    PingTimeoutError = 3311,
    PingIOExceptionError = 3312,
    PingUnknownExceptionError = 3313,
    PrepareMysqlExceptionError = 3314,
    PrepareTimeoutError = 3315,
    PrepareIOExceptionError = 3316,
    PrepareUnknownExceptionError = 3317,
    ExecuteMySqlExceptionError = 3318,
    ExecuteTimeoutError = 3319,
    ExecuteIOExceptionError = 3320,
    ExecuteUnknownExceptionError = 3321,
    InvalidOperationExceptionError = 3322,
    WaitTimeoutError = 3323,
}

public static class ErrorCodesHelper
{
    public static bool CanRetry(UInt32 errorCode)
    {
        return errorCode switch
        {
            (UInt32)ErrorCodes.PingIOExceptionError or
            (UInt32)ErrorCodes.PrepareIOExceptionError or
            (UInt32)ErrorCodes.ExecuteIOExceptionError or
            (UInt32)ErrorCodes.InvalidOperationExceptionError => true,
            _ => false,
        };
    }
}
