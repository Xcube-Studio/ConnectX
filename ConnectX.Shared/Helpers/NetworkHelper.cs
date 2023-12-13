using System.Collections;
using System.Net.NetworkInformation;

namespace ConnectX.Shared.Helpers;

public static class NetworkHelper
{
    /// <summary>
    ///     获取第一个可用的端口号
    /// </summary>
    /// <returns></returns>
    public static ushort GetAvailablePrivatePort()
    {
        var random = new Random((int)(DateTime.Now.Ticks % 100000000000));
        const ushort maxPort = 65535; //系统tcp/udp端口数最大是65535           
        const ushort beginPort = 5000; //从这个端口开始检测

        ushort port;
        do
        {
            port = (ushort)random.Next(beginPort, maxPort);
        } while (!PortIsAvailable(port));

        return port;
    }


    /// <summary>
    ///     获取操作系统已用的端口号
    /// </summary>
    /// <returns></returns>
    public static IEnumerable<int> PortIsUsed()
    {
        //获取本地计算机的网络连接和通信统计数据的信息
        var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();

        //返回本地计算机上的所有Tcp监听程序
        var ipsTcp = ipGlobalProperties.GetActiveTcpListeners();

        //返回本地计算机上的所有UDP监听程序
        var ipsUdp = ipGlobalProperties.GetActiveUdpListeners();

        //返回本地计算机上的Internet协议版本4(IPV4 传输控制协议(TCP)连接的信息。
        var tcpConnInfoArray = ipGlobalProperties.GetActiveTcpConnections();

        var allPorts = ipsTcp.Select(ep => ep.Port).ToList();
        allPorts.AddRange(ipsUdp.Select(ep => ep.Port));
        allPorts.AddRange(tcpConnInfoArray.Select(conn => conn.LocalEndPoint.Port));

        return allPorts;
    }

    /// <summary>
    ///     检查指定端口是否已用
    /// </summary>
    /// <param name="port"></param>
    /// <returns></returns>
    public static bool PortIsAvailable(int port)
    {
        var portUsed = PortIsUsed();

        return portUsed.All(p => p != port);
    }
}