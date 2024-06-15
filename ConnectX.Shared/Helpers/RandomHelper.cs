using System.Text;

namespace ConnectX.Shared.Helpers;

public static class RandomHelper
{
    private static readonly char[] Chars;

    static RandomHelper()
    {
        var arr = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
        Random.Shared.Shuffle(arr);

        Chars = arr;
    }

    public static string GetRandomString(int length = 12)
    {
        var sb = new StringBuilder();

        foreach (var ch in Random.Shared.GetItems(Chars, length))
            sb.Append(ch);

        return sb.ToString();
    }
}