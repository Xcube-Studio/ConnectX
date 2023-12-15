using System.Reflection;
using ConnectX.Client.Models;
using ConnectX.Client.Route.Packet;
using ConnectX.Shared.Helpers;
using Hive.Codec.Abstractions;
using Hive.Network.Shared;
using Microsoft.Extensions.Logging;

namespace ConnectX.Client.Route;

public class RouterPacketDispatcher
{
    private readonly CancellationTokenSource _cancelTokenSource;
    private readonly Dictionary<Type, CallbackWarp> _receiveCallbackDic = new ();
    
    private readonly Router _router;
    private readonly IPacketCodec _codec;
    private readonly ILogger<RouterPacketDispatcher> _logger;

    public RouterPacketDispatcher(
        Router router,
        IPacketCodec codec,
        ILogger<RouterPacketDispatcher> logger)
    {
        _router = router;
        _codec = codec;
        _logger = logger;
        _cancelTokenSource = new CancellationTokenSource();
        router.OnDelivery += OnReceiveTransDatagram;
    }

    public void Send(Guid target, object data)
    {
        _logger.LogTrace(
            "[ROUTER_DISPATCHER] Send {DataType} to {Target}",
            data.GetType().Name, target);
        
        SendToRouter(target, data);
    }
    
    private void SendToRouter(Guid targetId, object datagram)
    {
        using var stream = RecycleMemoryStreamManagerHolder.Shared.GetStream();
        _codec.Encode(datagram, stream);

        stream.Seek(0, SeekOrigin.Begin);
        
        _router.Send(targetId, stream.GetMemory());
    }

    /// <summary>
    ///     发送并接收，使用processor处理结果，如果processor返回true，则停止等待并返回true，否则继续接收下一个包，直到超时返回false
    /// </summary>
    /// <returns>返回处理结果，如果processor返回了true，则为true<br />如果processor一直没返回true，超时了则返回false</returns>
    public async Task<bool> SendAndListenOnce<T>(
        Guid target,
        object data,
        Func<T, bool> processor,
        CancellationToken token = default)
    {
        var received = false;
        
        _receiveCallbackDic[typeof(T)].TempCallback[target] = (T t, PacketContext _) => { received = processor(t); };
        SendToRouter(target, data);
        
        await TaskHelper.WaitUntilAsync(() => received, token == default ? _cancelTokenSource.Token : token);

        _receiveCallbackDic[typeof(T)].TempCallback.Remove(target);

        return received;
    }

    public void OnReceive<T>(Action<T, PacketContext> callback)
    {
        if (!_receiveCallbackDic.ContainsKey(typeof(T))) _receiveCallbackDic.Add(typeof(T), new CallbackWarp());
        _receiveCallbackDic[typeof(T)].UniformCallback.Add(callback);
    }
    
    public void OnReceive<T>(Guid receiver, Action<T, PacketContext> callback)
    {
        if (!_receiveCallbackDic.ContainsKey(typeof(T))) _receiveCallbackDic.Add(typeof(T), new CallbackWarp());
        _receiveCallbackDic[typeof(T)].SpecificCallback.Add(receiver, callback);
    }

    private void OnReceiveTransDatagram(P2PPacket packet)
    {
        void InvokeCallback(MethodBase actMethod, object receiver, object message1)
        {
            actMethod.Invoke(receiver, new[] { message1, new PacketContext(packet.From, this) });
        }
        
        using var stream = RecycleMemoryStreamManagerHolder.Shared.GetStream(packet.Payload.Span);
        var message = _codec.Decode(stream);
        var messageType = message!.GetType();
        
        _logger.LogTrace(
            "[ROUTER_DISPATCHER] Received {DataType} from {From}",
            messageType.Name, packet.From);


        if (!_receiveCallbackDic.TryGetValue(messageType, out var callbackWarp)) return;

        var genericActionType = typeof(Action<,>).MakeGenericType(messageType, typeof(PacketContext));
        var actMethod = genericActionType.GetMethod("Invoke");

        if (callbackWarp.TempCallback.Count > 0)
            lock (callbackWarp.TempCallback)
            {
                if (callbackWarp.TempCallback.TryGetValue(packet.From, out var value))
                    InvokeCallback(actMethod, value, message);
            }

        // 调用同步回调

        if (callbackWarp.SpecificCallback.TryGetValue(packet.From, out var cbValue))
            InvokeCallback(actMethod, cbValue, message);

        foreach (var callback in callbackWarp.UniformCallback) InvokeCallback(actMethod, callback, message);
    }

    private readonly struct CallbackWarp()
    {
        public readonly List<object> UniformCallback = [];
        public readonly Dictionary<Guid, object> SpecificCallback = [];
        public readonly Dictionary<Guid, object> TempCallback = [];
    }
}