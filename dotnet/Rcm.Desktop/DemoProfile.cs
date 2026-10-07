using System.IO;

namespace Rcm.Desktop;

public static class DemoProfile
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryFlow");
    public static Uri ServerEndpoint => ResolveEndpoint(Environment.GetEnvironmentVariable("FACTORYFLOW_SERVER_URL"));

    public static Uri ResolveEndpoint(string? value)
    {
        var endpoint = new Uri(string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:18081/" : value, UriKind.Absolute);
        ValidateEndpoint(endpoint);
        return endpoint;
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || !endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/")
            throw new ArgumentException("FactoryFlow wymaga lokalnego adresu HTTP lub HTTPS bez ścieżki, np. http://127.0.0.1:18081/.");
    }
}
