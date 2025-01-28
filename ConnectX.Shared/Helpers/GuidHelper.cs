using System.Security.Cryptography;
using System.Text;

namespace ConnectX.Shared.Helpers;

public static class GuidHelper
{
    public static Guid Hash(string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        var hash = MD5.HashData(bytes);

        return new Guid(hash);
    }
}