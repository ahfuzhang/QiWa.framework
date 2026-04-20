#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.KestrelWrap;

using Microsoft.AspNetCore.Http;
using System.IO;
using System.Diagnostics;
using QiWa.Common;
using QiWa.Compress;
using QiWa.ConsoleLogger;
using Microsoft.Extensions.ObjectPool;

public class ContextObjectPolicy<ContextType>
    : PooledObjectPolicy<ContextType>
    where ContextType : QiWa.Common.IResettable, new()
{
    public override ContextType Create()
        => new ContextType();

    public override bool Return(ContextType ctx)
    {
        ctx.Reset();
        return true; // true = 放回池
    }
}
