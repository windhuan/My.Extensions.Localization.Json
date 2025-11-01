using System;
using System.IO;
using System.Reflection;

namespace My.Extensions.Localization.Json.Internal;

public static class PathHelpers
{
    public static string GetApplicationRoot() => AppContext.BaseDirectory;
}