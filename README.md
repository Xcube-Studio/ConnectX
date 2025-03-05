# ConnectX

A cross-platform Minecraft P2P online multi-player library in C#, developed using high-performance sockets for excellent forwarding performance, with P2P powered by the Zerotier SDK.

Proudly powered by another of our open-source projects: [Hive.Framework](https://github.com/Corona-Studio/Hive.Framework)

## Architecture

![ConnectX drawio](https://github.com/user-attachments/assets/fe47401c-6543-48a1-9c22-3617dfa9ce42#gh-light-mode-only)
![ConnectX dark drawio](https://github.com/user-attachments/assets/4d77b985-4c63-4c2b-a3f6-5e3b98ef9ff0#gh-dark-mode-only)

## Quick Start!

We are using the MSDI(Microsoft.Extensions.DependencyInjection) as our DI container. The best practice is to use `.NET Generic Host` for you program. 

First, add the following method for the Server Config. `ConnectXServerIp` will be the Backend address for the ConnectX.Server.

```c#
private static IClientSettingProvider GetConnectXSettings()
{
    var serverIp = IPAddress.None;

    try
    {
        var ips = Dns.GetHostAddresses(ConnectXServerIp);
        var ipv4Addresses = ips
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .Where(ip => !ip.IsLocalIpAddress())
            .ToArray();

        if (ipv4Addresses.Length > 0)
            serverIp = ipv4Addresses[0];
    }
    catch (Exception ex)
    {
        Log.Logger.Error(ex, "Failed to resolve ConnectX server IP.");
    }

    return new DefaultClientSettingProvider
    {
        ServerAddress = serverIp,
        ServerPort = ConnectXServerPort,
        JoinP2PNetwork = true
    };
}
```

Then, just add one more line to complete the setup!

```diff
private static void ConfigureServices(IServiceCollection services)
{
    // ...
+   services.UseConnectX(GetConnectXSettings);
    // ...
}
```

## License

MIT. This means that you can modify or use our code for any purpose, however, copyright notice and permission notice shall be included in all copies or substantial portions of your software.

## Stats



## Disclaimer

ConnectX is not affiliated with Mojang or any part of its software.

## Hall of Shame

Here, we'll list all programs using our code without obeying the  MIT License.
