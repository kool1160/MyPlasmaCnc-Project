namespace MyPlasm.Inspector.Transport.D2xx;

public static class D2xxVersion
{
    public static string Format(uint rawVersion)
    {
        uint major = (rawVersion >> 16) & 0xFF;
        uint minor = (rawVersion >> 8) & 0xFF;
        uint build = rawVersion & 0xFF;
        return $"{major}.{minor:D2}.{build:D2}";
    }
}
